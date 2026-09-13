using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelLifecycleTests
{
    [Fact]
    public Task Queued_and_failed_tasks_update_without_a_provider_event() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync(claim: false);
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        panel.LatestState.GetProperty("sections")[0].GetProperty("counts").GetProperty("queued").GetInt32().Should().Be(1);
        panel.AcknowledgeLatest();
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(task.ChildTurnId, task.ChildConversationId, MessageRole.Assistant,
            string.Empty, MessageStatus.Failed, now, now, ErrorMessage: "Model unavailable");
        await activity.Tasks.TryCompleteAsync(task.Id, SubagentTaskStatus.Queued,
            new SubagentTaskCompletion(SubagentTaskStatus.Failed, new TurnFinalization(message, []), null,
                SubagentErrorCodes.ModelUnavailable, "Model unavailable", now));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (panel.LatestState.GetProperty("sections")[0].GetProperty("counts").GetProperty("failed").GetInt32() != 1)
        {
            panel.AcknowledgeLatest();
            await Task.Delay(10, timeout.Token);
        }
        var row = panel.LatestState.GetProperty("sections")[0].GetProperty("tasks")[0];
        row.GetProperty("status").GetString().Should().Be("failed");
        row.GetProperty("errorCode").GetString().Should().Be(SubagentErrorCodes.ModelUnavailable);
        row.GetProperty("deliveryStatus").GetString().Should().Be("pending");
        activity.Registry.GetActivity(task.Id).Should().BeNull();
    });

    [Fact]
    public Task Scheduler_limits_remain_visible_in_each_parent_scope() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var first = await activity.CreateTaskAsync(claim: false);
        var parent = await activity.Conversations.GetConversationAsync(first.ParentConversationId) ?? throw new InvalidOperationException();
        for (var index = 0; index < 3; index++) await activity.CreateTaskAsync(parent, claim: false);
        var other = await activity.CreateTaskAsync(claim: false);
        for (var index = 0; index < 4; index++)
            (await activity.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)).Should().NotBeNull();
        (await activity.Tasks.TryClaimNextAsync(DateTimeOffset.UtcNow)).Should().BeNull();
        using var panel = new ActivityPanelTestContext(activity, parent.Id);
        await panel.Publisher.SubscribeAsync(Guid.NewGuid(), parent.Id, "first", CancellationToken.None);
        var counts = panel.LatestState.GetProperty("sections")[0].GetProperty("counts");
        counts.GetProperty("total").GetInt32().Should().Be(4);
        counts.GetProperty("running").GetInt32().Should().Be(3);
        counts.GetProperty("queued").GetInt32().Should().Be(1);
        panel.Source.Select(other.ParentConversationId);
        await panel.Publisher.SubscribeAsync(Guid.NewGuid(), other.ParentConversationId, "other", CancellationToken.None);
        counts = panel.LatestState.GetProperty("sections")[0].GetProperty("counts");
        counts.GetProperty("total").GetInt32().Should().Be(1);
        counts.GetProperty("running").GetInt32().Should().Be(1);
    });

    [Fact]
    public Task Timeout_keeps_partial_content_and_terminalizes_the_task_and_open_tool() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var clock = new TriggeredTimeoutTimeProvider();
        var execution = activity.CreateExecutor(task, runtime, clock).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "partial result"));
        await runtime.EmitAsync(new ToolCallStartedEvent("open-tool", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
        clock.DueTime.Should().Be(TimeSpan.FromSeconds(task.MaxRunSeconds));
        clock.TriggerTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        var detail = await activity.Service.GetDetailAsync(task.ParentConversationId, task.Id) ?? throw new InvalidOperationException();
        detail.Activity.Task.Status.Should().Be(SubagentTaskStatus.Failed);
        detail.Activity.Task.ErrorCode.Should().Be(SubagentErrorCodes.TimedOut);
        detail.Activity.Task.DeliveryStatus.Should().Be(SubagentDeliveryStatus.Pending);
        detail.Content.Message?.MarkdownContent.Should().Be("partial result");
        detail.Content.Message?.Segments.Should().NotContain(segment => segment.Kind == MessageSegmentKind.Thinking);
        detail.Content.ToolRuns.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Failed);
        detail.Activity.Task.InputTokens.Should().BeNull();
        detail.Activity.Task.OutputTokens.Should().BeNull();
        activity.Registry.GetActivity(task.Id).Should().BeNull();
        using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
        var subscription = Guid.NewGuid();
        await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "initial", CancellationToken.None);
        panel.AcknowledgeLatest();
        await panel.Publisher.SelectDetailAsync(subscription, Guid.NewGuid(), task.Id, null, null, "detail", CancellationToken.None);
        var wire = panel.LatestState.GetProperty("sections")[0].GetProperty("detail");
        wire.GetProperty("contentOrigin").GetString().Should().Be("persisted");
        wire.GetProperty("task").GetProperty("errorCode").GetString().Should().Be(SubagentErrorCodes.TimedOut);
        wire.GetProperty("message").GetProperty("segments")[1].GetProperty("status").GetString().Should().Be("failed");
    });
}
