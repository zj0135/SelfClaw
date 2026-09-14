using SelfClaw.Desktop.Services.Transcript.Views;
using System.Windows.Threading;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Transcript;

internal sealed class TranscriptDelivery : IDisposable
{
    private readonly WebViewHostChannel _channel;
    private readonly DispatcherTimer _retryTimer;
    private TranscriptRenderState? _latest;
    private TranscriptRenderState? _inFlight;
    private TranscriptRenderState? _acknowledged;
    private long _revision;
    private long _inFlightRevision;
    private long _acknowledgedRevision;
    private int _retries;
    private bool _exhausted;
    private bool _disposed;

    public TranscriptDelivery(WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _channel = channel;
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _retryTimer.Tick += OnRetry;
        _channel.ReadyChanged += OnReadyChanged;
    }

    public void Publish(TranscriptRenderState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _latest = state;
        if (!_disposed && _channel.IsReady && _inFlight is null && !_exhausted) Send(state);
    }

    public bool Acknowledge(long revision)
    {
        if (_inFlight is null || revision != _inFlightRevision) return false;
        _acknowledged = _inFlight;
        _acknowledgedRevision = revision;
        _inFlight = null;
        _retryTimer.Stop();
        _retries = 0;
        _exhausted = false;
        if (_latest is { } latest && !ReferenceEquals(latest, _acknowledged)) Send(latest);
        return true;
    }

    public void Resynchronize()
    {
        Reset();
        if (!_disposed && _channel.IsReady && _latest is { } latest) Send(latest);
    }

    public void Reject(long revision)
    {
        if (revision != _inFlightRevision && revision != _acknowledgedRevision) return;
        Retry();
    }

    internal void Retry()
    {
        _retryTimer.Stop();
        if (_disposed || !_channel.IsReady || _latest is null || _exhausted) return;
        if (++_retries > 3)
        {
            _exhausted = true;
            _channel.PostPush(new { type = "transcript-unavailable", error = "会话显示同步失败，请重新加载。" });
            return;
        }
        _acknowledged = null;
        Send(_latest);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetry;
        _channel.ReadyChanged -= OnReadyChanged;
        _latest = _inFlight = _acknowledged = null;
    }

    private void OnReadyChanged(bool ready)
    {
        Reset();
        if (ready && _latest is { } latest) Send(latest);
    }

    private void OnRetry(object? sender, EventArgs e) => Retry();

    private void Reset()
    {
        _retryTimer.Stop();
        _inFlight = _acknowledged = null;
        _inFlightRevision = _acknowledgedRevision = 0;
        _retries = 0;
        _exhausted = false;
    }

    private void Send(TranscriptRenderState state)
    {
        if (!_channel.IsReady) return;
        var revision = checked(++_revision);
        var payload = _acknowledged is null ? CreateTranscriptPayload(state, revision) :
            CreateTranscriptPatch(_acknowledged, state, revision, _acknowledgedRevision);
        _inFlight = state;
        _inFlightRevision = revision;
        _retryTimer.Start();
        if (!_channel.PostPush(payload)) _retryTimer.Stop();
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
        long revision,
        long baseRevision)
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
            baseRevision,
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
