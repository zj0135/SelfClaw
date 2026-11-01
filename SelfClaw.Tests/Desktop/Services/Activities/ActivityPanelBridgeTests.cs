using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelBridgeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _trace;
    public ActivityPanelBridgeTests(Xunit.Abstractions.ITestOutputHelper trace) => _trace = trace;

    [Fact]
    public Task Foreign_task_and_invalid_subscription_cannot_cancel_and_accepted_cancel_survives_navigation()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var activity = new SubagentActivityTestContext();
            var owned = await activity.CreateTaskAsync(claim: false);
            var foreign = await activity.CreateTaskAsync(claim: false);
            using var panel = new ActivityPanelTestContext(activity, owned.ParentConversationId, _trace);
            var coordinator = new ActivityTaskCoordinator();
            var bridge = new ActivityPanelBridge(panel.Publisher, activity.Service, coordinator, panel.Channel, Dispatcher.CurrentDispatcher);
            var subscription = Guid.NewGuid();
            await panel.Publisher.SubscribeAsync(subscription, owned.ParentConversationId, "subscribe", CancellationToken.None);
            await HandleAsync(bridge, "cancel-task", new { subscriptionId = subscription, taskId = foreign.Id, requestId = "foreign" });
            panel.Messages.Last().GetProperty("error").GetString().Should().Be("task-not-found");
            await HandleAsync(bridge, "cancel-task", new { subscriptionId = Guid.NewGuid(), taskId = owned.Id });
            coordinator.Commands.Should().BeEmpty();
            var cancellation = HandleAsync(bridge, "cancel-task", new { subscriptionId = subscription, taskId = owned.Id, requestId = "cancel" });
            while (coordinator.Commands.Count == 0) await Task.Delay(10);
            panel.Source.Select(foreign.ParentConversationId);
            coordinator.Completion.SetResult(new SubagentTaskView(owned.Id, owned.ParentConversationId, owned.ParentTurnId,
                owned.ChildConversationId, owned.ChildTurnId, owned.SubagentId, owned.SubagentName, owned.TaskText,
                SubagentTaskStatus.Cancelled, 1, null, null, null, null, null, null, null, owned.QueuedAtUtc, null, null, owned.CreatedAtUtc, owned.UpdatedAtUtc));
            await cancellation;
            panel.Messages.Last().GetProperty("accepted").GetBoolean().Should().BeTrue();
            coordinator.Commands.Should().ContainSingle().Which.ParentConversationId.Should().Be(owned.ParentConversationId);
        });

    [Fact]
    public Task Unknown_operations_are_rejected_with_a_correlated_error()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var activity = new SubagentActivityTestContext();
            var task = await activity.CreateTaskAsync(claim: false);
            using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId, _trace);
            var bridge = new ActivityPanelBridge(panel.Publisher, activity.Service, new ActivityTaskCoordinator(), panel.Channel, Dispatcher.CurrentDispatcher);
            var subscription = Guid.NewGuid();
            await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "subscribe", CancellationToken.None);
            // 读内容协议已删除：旧客户端再发 read-content 只会收到关联的错误回包，不会碰宿主状态。
            await HandleAsync(bridge, "read-content", new { subscriptionId = subscription, requestId = "legacy", taskId = task.Id });
            var response = panel.Messages.Single(message => message.TryGetProperty("requestId", out var id) && id.GetString() == "legacy");
            response.GetProperty("type").GetString().Should().Be("activity-panel/error");
            response.GetProperty("error").GetString().Should().Be("unsupported-activity-operation");
        });

    private static async Task HandleAsync(ActivityPanelBridge bridge, string operation, object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await bridge.HandleAsync($"activity-panel/{operation}", document.RootElement, CancellationToken.None);
    }
}
