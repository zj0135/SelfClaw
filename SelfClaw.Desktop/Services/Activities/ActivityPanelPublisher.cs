using System.ComponentModel;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Activities;

internal sealed class ActivityPanelPublisher : IDisposable
{
    private readonly SubagentActivityService _activity;
    private readonly ActivityPanelSnapshotBuilder _builder;
    private readonly IActivityPanelScopeSource _source;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ActivityPanelPublisher> _logger;
    private readonly ActivityPanelDelivery _delivery;
    private ActivityPanelQuery? _query;
    private Guid? _observedParent;
    private long _generation;
    private long _revision;
    private long _lastCaptureSequence;
    private bool _pumping;
    private bool _dirty;
    private bool _disposed;

    public ActivityPanelPublisher(SubagentActivityService activity, ActivityPanelSnapshotBuilder builder,
        IActivityPanelScopeSource source, WebViewHostChannel channel, Dispatcher dispatcher,
        ILogger<ActivityPanelPublisher> logger, TimeProvider? time = null)
    {
        _activity = activity;
        _builder = builder;
        _source = source;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _delivery = new ActivityPanelDelivery(channel, dispatcher, time ?? TimeProvider.System, logger);
        _observedParent = source.CaptureActivityParent();
        _activity.Changed += OnActivityChanged;
        _source.PropertyChanged += OnSourceChanged;
        _channel.ReadyChanged += OnReadyChanged;
    }

    internal Task SubscribeAsync(Guid subscriptionId, Guid? expectedParent, string? requestId, CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var parent = _source.CaptureActivityParent();
        if (parent != expectedParent) throw new InvalidOperationException("activity-scope-mismatch");
        Reset();
        _observedParent = parent;
        _query = new ActivityPanelQuery(subscriptionId, parent, ++_generation);
        return RespondAsync(_query, requestId, cancellationToken);
    }

    internal Task GetStateAsync(Guid subscriptionId, string? cursor, string? requestId, CancellationToken cancellationToken)
    {
        var query = RequireSubscription(subscriptionId);
        _delivery.Resume();
        _query = query with { Cursor = cursor, Generation = ++_generation };
        return RespondAsync(_query, requestId, cancellationToken);
    }

    internal Task SelectDetailAsync(Guid subscriptionId, Guid selectionId, Guid? taskId, int? blockOffset,
        string? contentVersion, string? requestId, CancellationToken cancellationToken)
    {
        var query = RequireSubscription(subscriptionId);
        if (blockOffset is not null && contentVersion is null) throw new ArgumentException("content-version-required");
        _query = query with
        {
            Generation = ++_generation, DetailSelectionId = selectionId, TaskId = taskId,
            BlockOffset = blockOffset, ContentVersion = contentVersion
        };
        return RespondAsync(_query, requestId, cancellationToken);
    }

    internal ActivityPanelQuery RequireSubscription(Guid subscriptionId)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_query is not { } query || query.SubscriptionId != subscriptionId ||
            _source.CaptureActivityParent() != query.ParentConversationId)
            throw new InvalidOperationException("activity-subscription-invalid");
        if (query.ParentConversationId is Guid parent && _activity.IsScopeClosed(parent))
            throw new InvalidOperationException("activity-scope-closed");
        return query;
    }

    internal ActivityPanelQuery RequireSelection(Guid subscriptionId, Guid selectionId, Guid taskId)
    {
        var query = RequireSubscription(subscriptionId);
        if (query.DetailSelectionId != selectionId || query.TaskId != taskId)
            throw new InvalidOperationException("activity-detail-selection-invalid");
        return query;
    }

    internal void Unsubscribe(Guid subscriptionId)
    {
        _dispatcher.VerifyAccess();
        if (_query?.SubscriptionId == subscriptionId) Reset();
    }

    internal bool Acknowledge(Guid subscriptionId, long revision)
        => _delivery.Acknowledge(subscriptionId, revision);

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        Reset();
        _activity.Changed -= OnActivityChanged;
        _source.PropertyChanged -= OnSourceChanged;
        _channel.ReadyChanged -= OnReadyChanged;
    }

    private async Task RespondAsync(ActivityPanelQuery query, string? requestId, CancellationToken cancellationToken)
    {
        var state = await BuildAsync(query, cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            if (!IsCurrent(query))
            {
                _channel.PostResponse(new { type = "activity-panel/error", requestId, error = "activity-subscription-changed" });
                return;
            }
            Publish(state with { RequestId = requestId });
        });
    }

    private Task<ActivityPanelWireState> BuildAsync(ActivityPanelQuery query, CancellationToken cancellationToken)
        => Task.Run(() => _builder.BuildAsync(query, cancellationToken), cancellationToken);

    private void OnActivityChanged(Guid parent)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _query?.ParentConversationId != parent) return;
            if (_activity.IsScopeClosed(parent))
            {
                _query = _query with { Generation = ++_generation };
                _delivery.Dispose();
                Publish(new ActivityPanelWireState(_query.SubscriptionId, parent, 0, [], StateError: "activity-scope-closed"));
                return;
            }
            _dirty = true;
            if (!_pumping) Observe(PumpAsync());
        });
    }

    private async Task PumpAsync()
    {
        _pumping = true;
        try
        {
            while (_dirty && _query is { } query && !_disposed)
            {
                _dirty = false;
                var state = await BuildAsync(query, CancellationToken.None);
                // The service already coalesces text at 120ms; a second debounce would starve continuous output.
                if (IsCurrent(query)) Publish(state);
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void Publish(ActivityPanelWireState state)
    {
        _dispatcher.VerifyAccess();
        if (state.CaptureSequence > 0 && state.CaptureSequence < _lastCaptureSequence)
        {
            if (state.RequestId is not null)
                _channel.PostResponse(new { type = "activity-panel/queued", state.RequestId, state.SubscriptionId });
            return;
        }
        _lastCaptureSequence = Math.Max(_lastCaptureSequence, state.CaptureSequence);
        state = state with { Revision = checked(++_revision) };
        if (state.Sections.FirstOrDefault()?.ListReset == true && _query is not null)
            _query = _query with { Cursor = null };
        if (WebViewHostChannel.SerializeToUtf8Bytes(state).Length > ActivityPanelProjection.MaximumPayloadBytes)
            throw new InvalidOperationException("activity-payload-too-large");
        _delivery.Offer(state);
    }

    private bool IsCurrent(ActivityPanelQuery query)
        => !_disposed && _query?.Generation == query.Generation && _source.CaptureActivityParent() == query.ParentConversationId &&
           (query.ParentConversationId is not Guid parent || !_activity.IsScopeClosed(parent));

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        _dispatcher.VerifyAccess();
        var parent = _source.CaptureActivityParent();
        if (parent == _observedParent) return;
        _observedParent = parent;
        Reset();
    }

    private void OnReadyChanged(bool ready)
    {
        if (ready) _delivery.Ready();
        else Reset();
    }

    private void Reset()
    {
        _generation++;
        _query = null;
        _dirty = false;
        _delivery.Dispose();
    }

    private void Observe(Task operation)
        => _ = operation.ContinueWith(failed => _logger.LogWarning(failed.Exception, "Activity publication failed."),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
