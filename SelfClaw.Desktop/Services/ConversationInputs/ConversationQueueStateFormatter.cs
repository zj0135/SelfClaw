using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.ConversationInputs.Views;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal static class ConversationQueueStateFormatter
{
    internal const int MaximumSnapshotBytes = 256 * 1024;

    internal static ConversationQueueStateResponse Create(Guid subscriptionId, ConversationInputQueueState state, string? requestId)
    {
        var items = state.Items.Select(item => new ConversationQueueItemView(item.InputId, item.Sequence,
            item.Kind.ToString().ToLowerInvariant(), item.Status.ToString().ToLowerInvariant(), item.Revision,
            Limit(item.Preview, 200) ?? string.Empty, Limit(item.ReasonCode, 128), Limit(item.ErrorMessage, 1024))).ToArray();
        var payload = new ConversationQueueStateResponse("conversation-input/state", subscriptionId, state.ConversationId,
            state.QueueRevision, state.Paused, Limit(state.PauseReason, 1024), false, items, false, Limit(requestId, 128));
        while (WebViewHostChannel.SerializeToUtf8Bytes(payload).Length > MaximumSnapshotBytes)
        {
            if (payload.Items.Count == 0) throw new InvalidOperationException("Queue response metadata exceeds its limit.");
            payload = payload with { Items = payload.Items.Take(payload.Items.Count - 1).ToArray(), Truncated = true };
        }
        return payload;
    }

    private static string? Limit(string? value, int length) => value?.Length > length ? value[..length] : value;
}

