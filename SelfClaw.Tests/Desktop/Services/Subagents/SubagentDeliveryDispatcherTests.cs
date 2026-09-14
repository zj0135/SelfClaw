using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentDeliveryDispatcherTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("empty")]
    [InlineData("cancel-after-lease")]
    [InlineData("success")]
    [InlineData("cancel-during-execution")]
    [InlineData("busy-parent")]
    [InlineData("failed-first-parent")]
    public async Task Admission_is_released_across_lease_acquisition_and_handoff(string outcome)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var now = DateTimeOffset.UtcNow;
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(
            SubagentTaskStatus.Succeeded, new TurnFinalization(new MessageRecord(task.ChildTurnId, task.ChildConversationId,
                MessageRole.Assistant, "result", MessageStatus.Completed, now, now), []), "result", null, null, now));
        var parent = await context.Conversations.GetConversationAsync(task.ParentConversationId)
            ?? throw new InvalidOperationException("Missing parent.");
        var store = new InterceptingSubagentDeliveryStore(new SqliteSubagentDeliveryRepository(context.Database));
        using var cancellation = new CancellationTokenSource();
        store.LeaseFailure = outcome switch { "io" or "failed-first-parent" => new IOException("lease failed"), "cancel" => new OperationCanceledException(), _ => null };
        store.LeaseFailureParentId = outcome == "failed-first-parent" ? parent.Id : null;
        store.ReturnEmptyLease = outcome == "empty";
        store.AfterLease = outcome == "cancel-after-lease" ? cancellation.Cancel : null;
        using var sessions = new ConversationSessionCoordinator(context.Conversations, new SilentTranscriptSink());
        var targetParent = parent;
        ConversationRuntimeState? busy = null;
        if (outcome is "busy-parent" or "failed-first-parent")
        {
            var second = await context.CreateTaskAsync();
            var completedAt = now.AddMilliseconds(10);
            await context.Tasks.TryCompleteAsync(second.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(
                SubagentTaskStatus.Succeeded, new TurnFinalization(new MessageRecord(second.ChildTurnId, second.ChildConversationId,
                    MessageRole.Assistant, "second result", MessageStatus.Completed, completedAt, completedAt), []),
                "second result", null, null, completedAt));
            targetParent = await context.Conversations.GetConversationAsync(second.ParentConversationId)
                ?? throw new InvalidOperationException("Missing second parent.");
            if (outcome == "busy-parent") busy = await sessions.StartTurnAsync(parent);
        }
        using var activity = new AgentActivityCoordinator(context.Approvals, NullLogger<AgentActivityCoordinator>.Instance);
        var notifications = new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance);
        var runtime = new ControlledSubagentRuntime();
        using var engine = new ConversationTurnEngine(context.Conversations,
            new DesktopTurnFinalizer(context.Conversations, NullLogger<DesktopTurnFinalizer>.Instance), context.Recorder,
            runtime, sessions, activity, context.Approvals,
            SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(new DesktopSettingsJsonStore(StoragePathDefaults.Create("unused", "unused", "unused"))),
            new SilentCompletionNotifier(), NullLogger<ConversationTurnEngine>.Instance);
        var executor = new SubagentContinuationExecutor(store, runtime, context.Recorder, context.Approvals,
            new SubagentTaskSnapshotSerializer(), new SubagentCompletionBatchSerializer(), engine, notifications,
            NullLogger<SubagentContinuationExecutor>.Instance);
        using var dispatcher = new SubagentDeliveryDispatcher(store, context.Conversations, engine, executor, notifications,
            new ScanTimeProvider(now.AddSeconds(3)), NullLogger<SubagentDeliveryDispatcher>.Instance);

        Func<Task> start = () => dispatcher.TryStartContinuationAsync(cancellation.Token);
        if (outcome is "cancel" or "cancel-after-lease") await start.Should().ThrowAsync<OperationCanceledException>();
        else await start();

        if (outcome is "success" or "busy-parent" or "failed-first-parent" or "cancel-during-execution")
        {
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sessions.IsRunning(targetParent.Id).Should().BeTrue();
            runtime.Request!.ConversationId.Should().Be(targetParent.Id);
            if (outcome == "cancel-during-execution") cancellation.Cancel();
            else await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "continued"));
            await WaitForReleaseAsync();
            store.Resolutions.Should().Be(1);
        }
        else
        {
            runtime.Started.Task.IsCompleted.Should().BeFalse();
            store.Resolutions.Should().Be(outcome == "cancel-after-lease" ? 1 : 0);
        }

        sessions.IsRunning(targetParent.Id).Should().BeFalse();
        if (busy is not null)
        {
            sessions.IsRunning(parent.Id).Should().BeTrue("another parent's continuation must not release this interactive turn");
            sessions.AbandonTurn(busy);
        }
        var next = await engine.TryAdmitContinuationAsync(targetParent, CancellationToken.None);
        next.Should().NotBeNull();
        if (next is not null) await engine.CompleteContinuationAsync(next, false, CancellationToken.None);

        async Task WaitForReleaseAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (sessions.IsRunning(targetParent.Id)) await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SilentTranscriptSink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }

    private sealed class ScanTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SilentCompletionNotifier : IConversationCompletionNotifier
    {
        public void Notify(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages) { }
    }
}
