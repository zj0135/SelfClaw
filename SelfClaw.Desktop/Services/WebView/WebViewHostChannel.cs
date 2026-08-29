using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services.WebView;

public sealed class WebViewHostChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private Action<string>? _postJson;
    private TranscriptRenderState? _latestTranscript;
    private TranscriptRenderState? _pendingTranscript;
    private TranscriptRenderState? _inFlightTranscript;
    private TranscriptRenderState? _acknowledgedTranscript;
    private long? _inFlightTranscriptRevision;
    private long _nextTranscriptRevision;
    private bool _isReady;

    public void Attach(Action<string> postJson)
    {
        ArgumentNullException.ThrowIfNull(postJson);
        _postJson = postJson;
        ResetTranscriptDelivery();
    }

    public void Detach()
    {
        _postJson = null;
        _isReady = false;
        ResetTranscriptDelivery();
    }

    public void MarkReady()
    {
        _isReady = true;
        if (_latestTranscript is not null && _inFlightTranscriptRevision is null)
        {
            SendTranscript(_latestTranscript);
        }
    }

    public void MarkNotReady()
    {
        _isReady = false;
        ResetTranscriptDelivery();
    }

    /// <summary>
    /// Raised whenever new shell state arrives, delivered or not. It is the one funnel every conversation,
    /// agent and busy change already flows through, which is what the plugin context publisher listens to
    /// rather than trying to observe each of those changes separately.
    /// </summary>
    public event Action? TranscriptPublished;

    public void PublishTranscript(TranscriptRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _latestTranscript = state;
        TranscriptPublished?.Invoke();
        if (!_isReady)
        {
            return;
        }

        if (_inFlightTranscriptRevision is not null)
        {
            _pendingTranscript = state;
            return;
        }

        SendTranscript(state);
    }

    public bool AcknowledgeTranscript(long revision)
    {
        if (_inFlightTranscriptRevision != revision || _inFlightTranscript is null)
        {
            return false;
        }

        _acknowledgedTranscript = _inFlightTranscript;
        _inFlightTranscript = null;
        _inFlightTranscriptRevision = null;

        var pending = _pendingTranscript;
        _pendingTranscript = null;
        if (_isReady && pending is not null && !ReferenceEquals(pending, _acknowledgedTranscript))
        {
            SendTranscript(pending);
        }

        return true;
    }

    public bool PostPush(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _isReady && Post(payload);
    }

    public bool PostResponse(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Post(payload);
    }

    private bool Post(object payload)
    {
        if (_postJson is null)
        {
            return false;
        }

        _postJson(JsonSerializer.Serialize(payload, JsonOptions));
        return true;
    }

    private void SendTranscript(TranscriptRenderState state)
    {
        var revision = checked(++_nextTranscriptRevision);
        var payload = _acknowledgedTranscript is null
            ? CreateTranscriptPayload(state, revision)
            : CreateTranscriptPatch(_acknowledgedTranscript, state, revision);
        if (!PostPush(payload))
        {
            return;
        }

        _inFlightTranscript = state;
        _inFlightTranscriptRevision = revision;
    }

    private void ResetTranscriptDelivery()
    {
        _pendingTranscript = null;
        _inFlightTranscript = null;
        _acknowledgedTranscript = null;
        _inFlightTranscriptRevision = null;
    }

    private static object CreateTranscriptPayload(TranscriptRenderState state, long revision)
        => new
        {
            type = "replaceState",
            revision,
            state.AutoScroll,
            state.Items,
            state.Conversations,
            state.SelectedConversationId,
            state.IsBusy,
            state.ActivityText,
            state.AgentMode,
            state.SelectedAgentId,
            state.SelectedAgentName,
            state.CapabilityRevision,
            state.ToolPermissionMode
        };

    private static object CreateTranscriptPatch(
        TranscriptRenderState previous,
        TranscriptRenderState current,
        long revision)
    {
        var previousItems = previous.Items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var currentItemIds = current.Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var upsertItems = current.Items
            .Where(item => !previousItems.TryGetValue(item.Id, out var oldItem) || !ReferenceEquals(oldItem, item))
            .ToArray();
        var removedItemIds = previous.Items
            .Where(item => !currentItemIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToArray();
        var itemOrder = HaveSameItemOrder(previous.Items, current.Items)
            ? null
            : current.Items.Select(item => item.Id).ToArray();
        var conversations = previous.Conversations.SequenceEqual(current.Conversations)
            ? null
            : current.Conversations;

        return new
        {
            type = "patchState",
            revision,
            current.AutoScroll,
            UpsertItems = upsertItems,
            RemovedItemIds = removedItemIds,
            ItemOrder = itemOrder,
            Conversations = conversations,
            current.SelectedConversationId,
            current.IsBusy,
            current.ActivityText,
            current.AgentMode,
            current.SelectedAgentId,
            current.SelectedAgentName,
            current.CapabilityRevision,
            current.ToolPermissionMode
        };
    }

    private static bool HaveSameItemOrder(
        IReadOnlyList<TranscriptRenderItem> previous,
        IReadOnlyList<TranscriptRenderItem> current)
        => previous.Count == current.Count &&
           previous.Select(item => item.Id).SequenceEqual(current.Select(item => item.Id), StringComparer.Ordinal);
}
