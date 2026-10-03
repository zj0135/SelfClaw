using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence;
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
        await CompleteChildAsync(context, task);
        var now = DateTimeOffset.UtcNow;
        var parent = await context.Conversations.GetConversationAsync(task.ParentConversationId) ?? throw new InvalidOperationException();
        var store = new InterceptingSubagentDeliveryStore(new SqliteSubagentDeliveryRepository(context.Database));
        using var cancellation = new CancellationTokenSource();
        store.LeaseFailure = outcome switch { "io" or "failed-first-parent" => new IOException("lease failed"), "cancel" => new OperationCanceledException(), _ => null };
        store.LeaseFailureParentId = outcome == "failed-first-parent" ? parent.Id : null;
        store.ReturnEmptyLease = outcome == "empty";
        store.AfterLease = outcome == "cancel-after-lease" ? cancellation.Cancel : null;
        using var runs = new ConversationRunCoordinator(context.Inputs, NullLogger<ConversationRunCoordinator>.Instance);
        using var sessions = new ConversationSessionCoordinator(context.Conversations, context.Turns, runs, new Sink());
        var targetParent = parent;
        ConversationRunHandle? busy = null;
        if (outcome is "busy-parent" or "failed-first-parent")
        {
            var second = await context.CreateTaskAsync();
            await CompleteChildAsync(context, second);
            targetParent = await context.Conversations.GetConversationAsync(second.ParentConversationId) ?? throw new InvalidOperationException();
            if (outcome == "busy-parent") busy = runs.TryReserve(parent.Id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive);
        }
        var notifications = new DesktopNotificationService(NullLogger<DesktopNotificationService>.Instance);
        var runtime = new ControlledSubagentRuntime();
        var executor = new SubagentContinuationExecutor(store, runtime, context.Recorder, context.Approvals,
            new SubagentTaskSnapshotSerializer(), new SubagentCompletionBatchSerializer(), runs, sessions, notifications,
            NullLogger<SubagentContinuationExecutor>.Instance);
        using var dispatcher = new SubagentDeliveryDispatcher(store, context.Conversations, runs, executor, notifications,
            new ScanTimeProvider(now.AddSeconds(3)), NullLogger<SubagentDeliveryDispatcher>.Instance);
        Func<Task> start = () => dispatcher.TryStartContinuationAsync(cancellation.Token);
        if (outcome is "cancel" or "cancel-after-lease") await start.Should().ThrowAsync<OperationCanceledException>();
        else await start();
        if (outcome is "success" or "busy-parent" or "failed-first-parent" or "cancel-during-execution")
        {
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var handle = runs.GetActiveRun(targetParent.Id) ?? throw new InvalidOperationException("Missing owner.");
            runtime.Request?.ConversationId.Should().Be(targetParent.Id);
            runtime.Request?.TurnId.Should().Be(handle.TurnId);
            if (outcome == "cancel-during-execution") cancellation.Cancel();
            else await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "continued"));
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            store.Resolutions.Should().Be(1);
        }
        else
        {
            runtime.Started.Task.IsCompleted.Should().BeFalse();
            store.Resolutions.Should().Be(outcome == "cancel-after-lease" ? 1 : 0);
        }
        runs.IsRunning(targetParent.Id).Should().BeFalse();
        if (busy is not null)
        {
            runs.GetActiveRun(parent.Id).Should().BeSameAs(busy, "another parent's continuation cannot release this run");
            runs.Complete(busy, false);
        }
        var next = await runs.TryReserveContinuationAsync(targetParent) ?? throw new InvalidOperationException("Reservation leaked.");
        runs.Complete(next, false);
    }

    private static async Task CompleteChildAsync(SubagentActivityTestContext context, SubagentTaskRecord task)
    {
        var turn = (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Single(turn => turn.Id == task.ChildTurnId);
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(Guid.NewGuid(), task.ChildConversationId, turn.Id,
            await context.Turns.ReserveMessageSequenceAsync(task.ChildConversationId), MessageRole.Assistant,
            "child result", MessageStatus.Sealed, now, now);
        await context.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Running, new SubagentTaskCompletion(SubagentTaskStatus.Succeeded,
            new(turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = now }, [message], []), "child result", null, null, now));
    }

    private sealed class Sink : ITranscriptChangeSink
    {
        public void RequestStreamingPublish(bool autoScroll) { }
        public void PublishNow(bool autoScroll) { }
    }

    private sealed class ScanTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
