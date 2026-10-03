using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Maps <c>conversation-input/*</c> operations onto the input service. Submission stays on the
/// existing <c>send-prompt</c> entry; this bridge owns only the queue's list, edit, cancel,
/// pause/resume and detail operations. Steer is not enabled in P3 and is rejected explicitly.
/// </summary>
internal sealed class ConversationInputBridge
{
    private readonly ConversationInputService _inputs;
    private readonly ConversationInputPublisher _publisher;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;

    public ConversationInputBridge(ConversationInputService inputs, ConversationInputPublisher publisher,
        WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _inputs = inputs;
        _publisher = publisher;
        _channel = channel;
        _dispatcher = dispatcher;
    }

    internal async Task HandleAsync(string type, JsonElement payload, CancellationToken cancellationToken)
    {
        string? requestId = null;
        try
        {
            requestId = ReadString(payload, "requestId", 128);
            switch (type)
            {
                case "conversation-input/subscribe":
                    await _publisher.SubscribeAsync(ReadGuid(payload, "subscriptionId"),
                        ReadGuid(payload, "conversationId"), requestId, cancellationToken);
                    break;
                case "conversation-input/get-state":
                    await _publisher.GetStateAsync(ReadGuid(payload, "subscriptionId"), requestId, cancellationToken);
                    break;
                case "conversation-input/unsubscribe":
                    _publisher.Unsubscribe(ReadGuid(payload, "subscriptionId"));
                    Reply(new { type = "conversation-input/result", requestId, operation = "unsubscribe", ok = true });
                    break;
                case "conversation-input/edit":
                    await ReplyUpdateAsync("edit", requestId,
                        await _inputs.EditAsync(ReadGuid(payload, "inputId"), ReadInteger(payload, "expectedRevision"),
                            ReadString(payload, "prompt", 64 * 1024) ?? string.Empty, cancellationToken));
                    break;
                case "conversation-input/cancel":
                    await ReplyUpdateAsync("cancel", requestId,
                        await _inputs.CancelAsync(ReadGuid(payload, "inputId"), ReadInteger(payload, "expectedRevision"), cancellationToken));
                    break;
                case "conversation-input/pause":
                    await ReplyStateAsync("pause", requestId,
                        await _inputs.PauseAsync(ReadGuid(payload, "conversationId"), ReadLong(payload, "expectedQueueRevision"),
                            ConversationInputReason.QueuePaused, CancellationToken.None));
                    break;
                case "conversation-input/resume":
                    if (!_inputs.QueueEnabled) throw new InvalidOperationException(ConversationInputReason.QueueDisabled);
                    await ReplyStateAsync("resume", requestId,
                        await _inputs.ResumeAsync(ReadGuid(payload, "conversationId"), ReadLong(payload, "expectedQueueRevision"), CancellationToken.None));
                    break;
                case "conversation-input/retry":
                    await ReplyUpdateAsync("retry", requestId,
                        await _inputs.RetryAsync(ReadGuid(payload, "inputId"), ReadInteger(payload, "expectedRevision"), cancellationToken));
                    break;
                case "conversation-input/detail":
                    await ReplyDetailAsync(requestId, ReadGuid(payload, "inputId"), cancellationToken);
                    break;
                case "conversation-input/promote":
                case "conversation-input/steer":
                    throw new InvalidOperationException("steer-not-enabled");
                default:
                    throw new ArgumentException("unsupported-conversation-input-operation");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => Reply(new
            {
                type = "conversation-input/result", requestId, operation = "error",
                ok = false, errorCode = exception.Message, error = Describe(exception)
            }));
        }
    }

    private Task ReplyUpdateAsync(string operation, string? requestId, ConversationInputUpdateResult result)
    {
        var conflict = result.Status == ConversationInputUpdateStatus.Conflict;
        return _dispatcher.InvokeAsync(() => Reply(new
        {
            type = "conversation-input/result", requestId, operation, ok = result.Status == ConversationInputUpdateStatus.Applied,
            status = result.Status.ToString().ToLowerInvariant(), conflict,
            inputId = result.Input?.Id, revision = result.Input?.Revision, queueRevision = result.QueueRevision
        })).Task;
    }

    private Task ReplyStateAsync(string operation, string? requestId, ConversationInputQueueState state)
        => _dispatcher.InvokeAsync(() => Reply(new
        {
            type = "conversation-input/result", requestId, operation, ok = true,
            conversationId = state.ConversationId, queueRevision = state.QueueRevision,
            paused = state.Paused, pauseReason = state.PauseReason
        })).Task;

    private async Task ReplyDetailAsync(string? requestId, Guid inputId, CancellationToken cancellationToken)
    {
        var detail = await _inputs.GetDetailAsync(inputId, cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() => Reply(new
        {
            type = "conversation-input/result", requestId, operation = "detail", ok = detail is not null,
            inputId, revision = detail?.Revision, prompt = detail?.Prompt, status = detail?.Status.ToString().ToLowerInvariant()
        }));
    }

    private void Reply(object response)
    {
        _dispatcher.VerifyAccess();
        _channel.PostResponse(response);
    }

    private static string Describe(Exception exception) => exception.Message switch
    {
        "steer-not-enabled" => "当前版本未开放 Steer。",
        "conversation-input-subscription-invalid" => "队列订阅已失效，请重新打开。",
        "queue-revision-conflict" => "队列已变化，请根据最新列表重试。",
        "conversation-unavailable" => "该会话正在删除或应用正在退出。",
        "queue-disabled" => "队列功能未启用，待发送内容仍会保留。",
        "model-disabled" => "所选模型不可用，请启用后重试。",
        "workspace-missing" => "原工作目录不可用，请恢复目录后重试。",
        "agent-missing" => "原代理不可用，请恢复定义后重试。",
        _ => "队列操作失败，请重试。"
    };

    private static string? ReadString(JsonElement payload, string name, int maximum)
    {
        if (!payload.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException($"invalid-{name}");
        var value = element.GetString();
        if (value is null || value.Length > maximum) throw new ArgumentException($"invalid-{name}");
        return value;
    }

    private static Guid ReadGuid(JsonElement payload, string name)
        => Guid.TryParse(ReadString(payload, name, 36), out var value) && value != Guid.Empty
            ? value : throw new ArgumentException($"invalid-{name}");

    private static int ReadInteger(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new ArgumentException($"invalid-{name}");

    private static long ReadLong(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : throw new ArgumentException($"invalid-{name}");
}
