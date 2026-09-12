using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelBridgeTests
{
    [Fact]
    public Task Foreign_task_and_invalid_subscription_cannot_cancel_and_accepted_cancel_survives_navigation()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var activity = new SubagentActivityTestContext();
            var owned = await activity.CreateTaskAsync(claim: false);
            var foreign = await activity.CreateTaskAsync(claim: false);
            using var panel = new ActivityPanelTestContext(activity, owned.ParentConversationId);
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
    public Task Content_page_checks_scope_selection_version_and_serialized_budget()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var activity = new SubagentActivityTestContext();
            var task = await activity.CreateTaskAsync();
            var runtime = new ControlledSubagentRuntime();
            var execution = activity.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
            await runtime.Started.Task;
            var longText = string.Concat(Enumerable.Repeat("\u4e2d\ud83d\ude42\n", 6000));
            await runtime.EmitAsync(new AssistantTextDeltaEvent("text", longText));
            using var panel = new ActivityPanelTestContext(activity, task.ParentConversationId);
            var bridge = new ActivityPanelBridge(panel.Publisher, activity.Service, new ActivityTaskCoordinator(), panel.Channel, Dispatcher.CurrentDispatcher);
            var subscription = Guid.NewGuid();
            var selection = Guid.NewGuid();
            await panel.Publisher.SubscribeAsync(subscription, task.ParentConversationId, "subscribe", CancellationToken.None);
            panel.AcknowledgeLatest();
            await panel.Publisher.SelectDetailAsync(subscription, selection, task.Id, null, null, "detail", CancellationToken.None);
            var detail = await activity.Service.GetDetailAsync(task.ParentConversationId, task.Id) ?? throw new InvalidOperationException();
            await HandleAsync(bridge, "read-content", new { subscriptionId = subscription, detailSelectionId = selection, taskId = task.Id,
                requestId = "content", contentId = "segment/0", contentVersion = detail.Detail.ContentVersion, offset = 0 });
            var response = panel.Messages.Last();
            response.GetProperty("text").GetString().Should().NotEndWith("\ud83d");
            System.Text.Encoding.UTF8.GetByteCount(response.GetRawText()).Should().BeLessThanOrEqualTo(64 * 1024);
            response.GetProperty("isTruncated").GetBoolean().Should().BeTrue();
            await HandleAsync(bridge, "read-content", new { subscriptionId = subscription, detailSelectionId = Guid.NewGuid(), taskId = task.Id,
                contentId = "segment/0", contentVersion = detail.Detail.ContentVersion });
            panel.Messages.Last().GetProperty("error").GetString().Should().Be("activity-detail-selection-invalid");
            await runtime.EmitAsync(new AssistantTextDeltaEvent("text", "updated"));
            await HandleAsync(bridge, "read-content", new { subscriptionId = subscription, detailSelectionId = selection, taskId = task.Id,
                contentId = "segment/0", contentVersion = detail.Detail.ContentVersion });
            panel.Messages.Last().GetProperty("error").GetString().Should().Be("content-changed");
            activity.Service.SetScopeClosed(task.ParentConversationId, true);
            await HandleAsync(bridge, "read-content", new { subscriptionId = subscription, detailSelectionId = selection, taskId = task.Id });
            panel.Messages.Last().GetProperty("error").GetString().Should().Be("activity-scope-closed");
            activity.Service.SetScopeClosed(task.ParentConversationId, false);
            await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, longText + "updated"));
            await execution;
        });

    private static async Task HandleAsync(ActivityPanelBridge bridge, string operation, object payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await bridge.HandleAsync($"activity-panel/{operation}", document.RootElement, CancellationToken.None);
    }
}
