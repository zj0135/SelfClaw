using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Activities;

internal sealed class ActivityPanelDelivery : IDisposable
{
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private ActivityPanelWireState? _inFlight;
    private ActivityPanelWireState? _pending;
    private ITimer? _timer;
    private int _retries;
    private bool _stalled;
    private bool _disposed;

    internal ActivityPanelDelivery(WebViewHostChannel channel, Dispatcher dispatcher, TimeProvider time, ILogger logger)
    {
        _channel = channel;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;
    }

    internal void Offer(ActivityPanelWireState state)
    {
        VerifyAvailable();
        ArgumentNullException.ThrowIfNull(state);
        if (_inFlight is not null)
        {
            if (_pending is null || state.Revision > _pending.Revision) _pending = state with { RequestId = null };
            if (state.RequestId is not null)
            {
                _channel.PostResponse(new { type = "activity-panel/queued", state.RequestId, state.SubscriptionId });
            }
            return;
        }

        Send(state);
    }

    internal bool Acknowledge(Guid subscriptionId, long revision)
    {
        VerifyAvailable();
        if (_inFlight?.SubscriptionId != subscriptionId || _inFlight.Revision != revision) return false;
        _timer?.Dispose();
        _timer = null;
        _inFlight = null;
        _stalled = false;
        FlushPending();
        return true;
    }

    internal void Resume()
    {
        VerifyAvailable();
        if (!_stalled) return;
        ResetState();
    }

    internal void Ready()
    {
        VerifyAvailable();
        FlushPending();
    }

    internal void Reset()
    {
        VerifyAvailable();
        ResetState();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        ResetState();
    }

    private void ResetState()
    {
        _timer?.Dispose();
        _timer = null;
        _inFlight = null;
        _pending = null;
        _stalled = false;
        _retries = 0;
    }

    private void VerifyAvailable()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void FlushPending()
    {
        if (_inFlight is not null || _pending is null) return;
        var pending = _pending;
        _pending = null;
        Send(pending);
    }

    private void Send(ActivityPanelWireState state)
    {
        var flight = state with { RequestId = null };
        _inFlight = flight;
        _retries = 0;
        try
        {
            if (!(state.RequestId is null ? _channel.PostPush(state) : _channel.PostResponse(state)))
            {
                if (ReferenceEquals(_inFlight, flight))
                {
                    _inFlight = null;
                    _pending = flight;
                }
                return;
            }
        }
        catch
        {
            if (ReferenceEquals(_inFlight, flight))
            {
                _inFlight = null;
                _pending = flight;
            }
            throw;
        }

        if (ReferenceEquals(_inFlight, flight)) ScheduleTimeout(flight);
    }

    private void ScheduleTimeout(ActivityPanelWireState state)
    {
        _timer?.Dispose();
        _timer = _time.CreateTimer(_ =>
        {
            if (!_dispatcher.HasShutdownStarted)
            {
                _ = _dispatcher.InvokeAsync(() => Retry(state));
            }
        }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void Retry(ActivityPanelWireState state)
    {
        if (_disposed || !ReferenceEquals(_inFlight, state)) return;
        if (_retries >= 3)
        {
            _stalled = true;
            _timer?.Dispose();
            _timer = null;
            return;
        }

        try
        {
            _retries++;
            if (!_channel.PostPush(state))
            {
                _stalled = true;
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Activity retransmission failed. SubscriptionId={SubscriptionId} Revision={Revision}",
                state.SubscriptionId, state.Revision);
            _stalled = true;
            return;
        }
        if (ReferenceEquals(_inFlight, state)) ScheduleTimeout(state);
    }
}
