using System.Text.Json;
using System.Windows.Threading;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Maps <c>conversation-input/*</c> operations onto the input service. Submission stays on the
/// existing <c>send-prompt</c> entry (P4 adds <c>kind=steer</c> + <c>targetTurnId</c> there); this
/// bridge owns only the queue's list, edit, cancel, pause/resume and detail operations. The UI
/// steer/promote operations still belong to P5 and are rejected here.
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
                    if (!_inputs.QueueEnabled) throw new ConversationInputRejectedException(ConversationInputReason.QueueDisabled);
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
                    throw new ConversationInputRejectedException(ConversationInputReason.SteerNotEnabled);
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

    private static string Describe(Exception exception)
        => ConversationInputReasonText.Describe(exception is ConversationInputRejectedException rejected
            ? rejected.Reason : exception.Message);

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
