using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentExecutionSessionTests
{
    [Fact]
    public async Task A_blocked_child_turn_is_persisted_as_a_failed_task_with_the_hook_error()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var session = await context.RegisterSessionAsync(task);
        await session.BeginAsync();

        await session.ApplyEventAsync(
            new RunCompletedEvent(RunCompletionStatus.Blocked, FinalText: null, ErrorMessage: "blocked by hook 'alpha/a'"),
            CancellationToken.None);

        var persisted = await context.Tasks.GetAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing completed task.");
        persisted.Status.Should().Be(SubagentTaskStatus.Failed);
        persisted.ErrorCode.Should().Be(SubagentErrorCodes.BlockedByHook);
        persisted.ErrorMessage.Should().Contain("blocked by hook 'alpha/a'");
        var turn = (await context.Turns.ListTurnsAsync(task.ChildConversationId)).Should().ContainSingle().Which;
        turn.Status.Should().Be(ConversationTurnStatus.Blocked);
        (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        var detail = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing blocked detail.");
        detail.Content.Message.Should().BeNull();
        detail.Content.Turn?.Status.Should().Be(ConversationTurnStatus.Blocked);
        detail.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Complete);
        await session.DisposeAsync();
    }

    [Theory]
    [InlineData(RunCompletionStatus.Succeeded, ConversationTurnStatus.Succeeded)]
    [InlineData(RunCompletionStatus.Failed, ConversationTurnStatus.Failed)]
    public async Task No_output_terminal_keeps_unknown_usage_without_an_assistant(
        RunCompletionStatus completion, ConversationTurnStatus status)
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var session = await context.RegisterSessionAsync(task);
        await session.BeginAsync();
        await session.ApplyEventAsync(new RunStartedEvent("child", "model", null), CancellationToken.None);
        var initial = await session.CaptureSnapshotAsync() ?? throw new InvalidOperationException("Missing live snapshot.");
        initial.Message.Should().BeNull();
        initial.Turn.Status.Should().Be(ConversationTurnStatus.Running);
        await session.ApplyEventAsync(new UsageReportedEvent(new TurnUsage(InputTokens: 3, OutputTokens: 4)), CancellationToken.None);
        await session.ApplyEventAsync(new RunCompletedEvent(completion, null), CancellationToken.None);

        var detail = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing terminal detail.");
        detail.Content.Message.Should().BeNull();
        detail.Content.Turn?.Status.Should().Be(status);
        detail.Content.Turn?.Usage?.InputTokens.Should().Be(3);
        detail.Content.Turn?.Usage?.OutputTokens.Should().Be(4);
        detail.Content.Turn?.Usage?.TotalTokens.Should().BeNull();
        detail.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Complete);
        (await context.Conversations.ListMessagesAsync(task.ChildConversationId)).Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        initial.Turn.Status.Should().Be(ConversationTurnStatus.Running);
        initial.Turn.Usage.Should().BeNull();
        await context.Registry.UnregisterAsync(session);
    }

    [Fact]
    public async Task Unregister_blocks_new_reads_and_waits_for_inflight_snapshot_and_terminal_commit()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var session = await context.RegisterSessionAsync(task);
        await session.BeginAsync();
        await session.ApplyEventAsync(new AssistantThinkingDeltaEvent("thinking", "analysis"), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Store.BeforeCompleteAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var terminal = session.ApplyEventAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inFlight = session.CaptureSnapshotAsync();
        var closing = context.Registry.UnregisterAsync(session);
        closing.IsCompleted.Should().BeFalse();
        inFlight.IsCompleted.Should().BeFalse();
        (await session.CaptureSnapshotAsync()).Should().BeNull();
        (await context.Registry.CaptureSnapshotAsync(task.Id, CancellationToken.None)).Should().BeNull();
        release.TrySetResult();
        await terminal;
        var snapshot = await inFlight ?? throw new InvalidOperationException("The admitted snapshot was lost.");
        await closing;
        snapshot.Activity.TerminalObserved.Should().BeTrue();
        snapshot.Message?.MarkdownContent.Should().Be("done");
        (await context.Tasks.GetAsync(task.ParentConversationId, task.Id))?.Status.Should().Be(SubagentTaskStatus.Succeeded);
        (await session.CaptureSnapshotAsync()).Should().BeNull();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_of_a_waiting_reader_releases_its_lifetime_lease()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var session = await context.RegisterSessionAsync(task);
        await session.BeginAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Store.BeforeCompleteAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var terminal = session.ApplyEventAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var capture = session.CaptureSnapshotAsync(cancellation.Token);
        cancellation.Cancel();
        var awaitCapture = () => capture;
        await awaitCapture.Should().ThrowAsync<OperationCanceledException>();
        var close = context.Registry.UnregisterAsync(session);
        release.TrySetResult();
        await Task.WhenAll(terminal, close).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Existing_running_task_is_synchronizing_until_worker_recovers_it_as_interrupted()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var before = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing task.");
        before.Activity.Phase.Should().Be("synchronizing");
        var runtime = new ControlledSubagentRuntime();
        await context.CreateExecutor(task, runtime).RecoverInterruptedAsync(task, CancellationToken.None);
        var after = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing recovered task.");
        after.Activity.Task.Status.Should().Be(SubagentTaskStatus.Interrupted);
        after.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Partial);
        after.ContentOrigin.Should().Be("persisted");
        runtime.Started.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_and_completion_race_uses_the_persisted_winner_and_disposed_observers_do_not_cancel_execution()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        context.Service.Dispose();
        execution.IsCompleted.Should().BeFalse();
        await context.Tasks.RequestCancellationAsync(task.ParentConversationId, task.Id, DateTimeOffset.UtcNow);
        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "finished before cancellation"));
        await execution;
        context.Executions.RequestCancellation(task.Id);
        var persisted = await context.Tasks.GetAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing completed task.");
        persisted.Status.Should().Be(SubagentTaskStatus.Succeeded);
        persisted.CancelRequestedAtUtc.Should().NotBeNull();
        context.Registry.GetActivity(task.Id).Should().BeNull();
    }
}
