using System.Windows.Threading;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Transcript;

internal sealed class TranscriptPublisher : ITranscriptChangeSink, IDisposable
{
    private static readonly TimeSpan StreamingPublishInterval = TimeSpan.FromMilliseconds(75);

    private readonly TranscriptProjection _projection;
    private readonly WebViewHostChannel _hostChannel;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _streamingTimer;
    private Func<bool, TranscriptProjectionRequest>? _requestFactory;
    private bool _streamingPublishPending;
    private bool _pendingAutoScroll;
    private DateTimeOffset _lastStreamingPublishAtUtc = DateTimeOffset.MinValue;
    private int _disposeStarted;

    public TranscriptPublisher(
        TranscriptProjection projection,
        WebViewHostChannel hostChannel,
        Dispatcher dispatcher)
    {
        _projection = projection;
        _hostChannel = hostChannel;
        _dispatcher = dispatcher;
        _streamingTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = StreamingPublishInterval
        };
        _streamingTimer.Tick += OnStreamingTimerTick;
    }

    public void Attach(Func<bool, TranscriptProjectionRequest> requestFactory)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        if (_requestFactory is not null)
        {
            throw new InvalidOperationException("A transcript projection source is already attached.");
        }

        _requestFactory = requestFactory;
    }

    public void RequestStreamingPublish(bool autoScroll)
    {
        EnsureAttached();

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(
                () => RequestStreamingPublish(autoScroll),
                DispatcherPriority.Background);
            return;
        }

        _streamingPublishPending = true;
        _pendingAutoScroll |= autoScroll;

        var elapsed = DateTimeOffset.UtcNow - _lastStreamingPublishAtUtc;
        if (!_streamingTimer.IsEnabled && elapsed >= StreamingPublishInterval)
        {
            FlushStreamingPublish();
            return;
        }

        if (_streamingTimer.IsEnabled)
        {
            return;
        }

        _streamingTimer.Interval = elapsed >= StreamingPublishInterval
            ? StreamingPublishInterval
            : StreamingPublishInterval - elapsed;
        _streamingTimer.Start();
    }

    public void PublishNow(bool autoScroll)
    {
        EnsureAttached();

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(
                () => PublishNow(autoScroll),
                DispatcherPriority.Background);
            return;
        }

        if (_streamingPublishPending)
        {
            _pendingAutoScroll |= autoScroll;
            FlushStreamingPublish();
            return;
        }

        Publish(CreateRequest(autoScroll));
    }

    public void Invalidate() => _projection.Invalidate();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _streamingTimer.Stop();
        _streamingTimer.Tick -= OnStreamingTimerTick;
        _requestFactory = null;
        _streamingPublishPending = false;
    }

    private void OnStreamingTimerTick(object? sender, EventArgs e)
        => FlushStreamingPublish();

    private void FlushStreamingPublish()
    {
        _streamingTimer.Stop();
        if (!_streamingPublishPending)
        {
            return;
        }

        var autoScroll = _pendingAutoScroll;
        _streamingPublishPending = false;
        _pendingAutoScroll = false;
        _lastStreamingPublishAtUtc = DateTimeOffset.UtcNow;
        Publish(CreateRequest(autoScroll));
    }

    private TranscriptProjectionRequest CreateRequest(bool autoScroll)
        => (_requestFactory ?? throw new InvalidOperationException("No transcript projection source is attached."))(
            autoScroll);

    private void EnsureAttached()
    {
        if (_requestFactory is null)
        {
            throw new InvalidOperationException("No transcript projection source is attached.");
        }
    }

    private void Publish(TranscriptProjectionRequest request)
    {
        var state = _projection.Build(request);
        if (state is not null)
        {
            _hostChannel.PublishTranscript(state);
        }
    }
}
