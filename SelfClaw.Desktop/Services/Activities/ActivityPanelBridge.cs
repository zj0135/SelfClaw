using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Activities;

internal sealed class ActivityPanelBridge
{
    private readonly ActivityPanelPublisher _publisher;
    private readonly SubagentActivityService _activity;
    private readonly ISubagentTaskCoordinator _tasks;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;

    public ActivityPanelBridge(ActivityPanelPublisher publisher, SubagentActivityService activity,
        ISubagentTaskCoordinator tasks, WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _publisher = publisher;
        _activity = activity;
        _tasks = tasks;
        _channel = channel;
        _dispatcher = dispatcher;
    }

    internal async Task HandleAsync(string type, JsonElement payload, CancellationToken cancellationToken)
    {
        string? requestId = null;
        try
        {
            requestId = ReadString(payload, "requestId", 128);
            var subscription = ReadGuid(payload, "subscriptionId");
            switch (type)
            {
                case "activity-panel/subscribe":
                    await _publisher.SubscribeAsync(subscription, ReadOptionalGuid(payload, "parentConversationId"), requestId, cancellationToken);
                    break;
                case "activity-panel/get-state":
                    await _publisher.GetStateAsync(subscription, ReadString(payload, "cursor", 2048), requestId, cancellationToken);
                    break;
                case "activity-panel/select-detail":
                    await _publisher.SelectDetailAsync(subscription, ReadGuid(payload, "detailSelectionId"),
                        ReadOptionalGuid(payload, "taskId"), ReadInteger(payload, "blockOffset"),
                        ReadString(payload, "contentVersion", 128), requestId, cancellationToken);
                    break;
                case "activity-panel/read-content":
                    await ReadContentAsync(subscription, payload, requestId, cancellationToken);
                    break;
                case "activity-panel/cancel-task":
                    await CancelTaskAsync(subscription, payload, requestId, cancellationToken);
                    break;
                case "activity-panel/unsubscribe":
                    _publisher.Unsubscribe(subscription);
                    Reply(new { type, requestId, subscriptionId = subscription, ok = true });
                    break;
                case "activity-panel/rendered":
                    if (payload.TryGetProperty("revision", out var revision) && revision.ValueKind == JsonValueKind.Number && revision.TryGetInt64(out var value))
                        _publisher.Acknowledge(subscription, value);
                    break;
                default:
                    throw new ArgumentException("unsupported-activity-operation");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => Reply(new { type = "activity-panel/error", requestId, error = exception.Message }));
        }
    }

    private async Task ReadContentAsync(Guid subscription, JsonElement payload, string? requestId, CancellationToken cancellationToken)
    {
        var selection = ReadGuid(payload, "detailSelectionId");
        var task = ReadGuid(payload, "taskId");
        var query = _publisher.RequireSelection(subscription, selection, task);
        var parent = query.ParentConversationId ?? throw new InvalidOperationException("activity-scope-missing");
        var version = ReadString(payload, "contentVersion", 128) ?? throw new ArgumentException("content-version-required");
        var contentId = ReadString(payload, "contentId", 128) ?? throw new ArgumentException("content-id-required");
        var maximum = ReadInteger(payload, "maximumCharacters") ?? 8192;
        var offset = ReadInteger(payload, "offset") ?? 0;
        var page = await Task.Run(() => _activity.ReadContentAsync(
            new SubagentContentQuery(parent, task, version, contentId, offset, maximum), cancellationToken), cancellationToken);
        await _dispatcher.InvokeAsync(() =>
        {
            _publisher.RequireSelection(subscription, selection, task);
            if (page is null) throw new InvalidOperationException("content-not-found");
            var response = new ActivityContentResponse(requestId, subscription, selection, task, page.ContentVersion,
                page.ContentId, page.Text, page.Offset, page.NextOffset, page.TotalCharacters, page.NextOffset is not null);
            if (WebViewHostChannel.SerializeToUtf8Bytes(response).Length > 64 * 1024)
                throw new InvalidOperationException("activity-content-too-large");
            Reply(response);
        });
    }

    private async Task CancelTaskAsync(Guid subscription, JsonElement payload, string? requestId, CancellationToken cancellationToken)
    {
        var query = _publisher.RequireSubscription(subscription);
        var parent = query.ParentConversationId ?? throw new InvalidOperationException("activity-scope-missing");
        var task = ReadGuid(payload, "taskId");
        var owned = await Task.Run(() => _activity.GetDetailAsync(parent, task, cancellationToken: cancellationToken), cancellationToken);
        // Check ownership and the selected scope immediately before accepting the command. After acceptance,
        // navigating away only ends the display subscription, never the cancellation operation.
        var operation = await _dispatcher.InvokeAsync(() =>
        {
            _publisher.RequireSubscription(subscription);
            if (owned is null) throw new InvalidOperationException("task-not-found");
            return _tasks.CancelAsync(new SubagentTaskCommand(parent, task), cancellationToken);
        });
        var result = await operation;
        await _dispatcher.InvokeAsync(() => Reply(new
        {
            type = "activity-panel/cancelled", requestId, subscriptionId = subscription,
            taskId = task, accepted = true, status = result.Status.ToString().ToLowerInvariant()
        }));
    }

    private void Reply(object response)
    {
        _dispatcher.VerifyAccess();
        _channel.PostResponse(response);
    }

    private static string? ReadString(JsonElement payload, string name, int maximum)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException($"invalid-{name}");
        var value = element.GetString();
        if (value is null || value.Length == 0 || value.Length > maximum) throw new ArgumentException($"invalid-{name}");
        return value;
    }

    private static Guid ReadGuid(JsonElement payload, string name)
        => ReadOptionalGuid(payload, name) ?? throw new ArgumentException($"missing-{name}");

    private static Guid? ReadOptionalGuid(JsonElement payload, string name)
    {
        var value = ReadString(payload, name, 36);
        if (value is null) return null;
        return Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException($"invalid-{name}");
    }

    private static int? ReadInteger(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"invalid-{name}");
    }
}
