using System.ComponentModel;
using System.Windows.Threading;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Plugins;

/// <summary>
/// Owns the panel context end to end: it captures the context that answers <c>getContext()</c> and it
/// pushes <c>plugin-host/context</c> whenever that context changes. Keeping both on one object is the
/// point. The shell used to build its own context out of the transcript payload, which is how the
/// pushed shape ended up carrying different fields than the pulled one, and how a panel could read a
/// workspace root that was not the root <c>workspace.*</c> resolved against.
/// </summary>
internal sealed class PluginPanelContextPublisher : IDisposable
{
    private const string MessageType = "plugin-host/context";

    private readonly IPluginPanelContextSource _source;
    private readonly WebViewHostChannel _hostChannel;
    private readonly PluginPanelHostController _hostController;
    private readonly Dispatcher _dispatcher;
    private PluginPanelContext? _published;
    private bool _disposed;

    public PluginPanelContextPublisher(
        IPluginPanelContextSource source,
        WebViewHostChannel hostChannel,
        PluginPanelHostController hostController,
        Dispatcher dispatcher)
    {
        _source = source;
        _hostChannel = hostChannel;
        _hostController = hostController;
        _dispatcher = dispatcher;

        // Three signals, one publish path. Transcript publishes cover the conversation, agent and busy
        // fields; the view model covers workspace selection, which moves on its own without one. Every
        // path deduplicates by value, so the overlap between them costs nothing.
        _hostChannel.TranscriptPublished += OnStateChanged;
        _hostController.PanelOpened += OnPanelOpened;
        _source.PropertyChanged += OnSourcePropertyChanged;
    }

    /// <summary>
    /// The context as of right now. This is the pull path, so it is always fresh regardless of what the
    /// shell has or has not received.
    /// </summary>
    public PluginPanelContext Capture() => _source.CaptureContext();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hostChannel.TranscriptPublished -= OnStateChanged;
        _hostController.PanelOpened -= OnPanelOpened;
        _source.PropertyChanged -= OnSourcePropertyChanged;
    }

    // A panel that just opened has no history to replay, and after a shell reload the current context is
    // usually byte-identical to the last one published — deduplication would swallow exactly the push
    // the new panel needs. So this one ignores it.
    private void OnPanelOpened() => Publish(force: true);

    private void OnStateChanged() => Publish(force: false);

    // Deliberately unfiltered by property name: the deduplication below already makes an irrelevant
    // change free, whereas a name filter would silently stop covering any field added to the context.
    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) => Publish(force: false);

    private void Publish(bool force)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.InvokeAsync(() => Publish(force));
            return;
        }

        var context = _source.CaptureContext();
        if (!force && context == _published)
        {
            return;
        }

        // PostPush is a no-op until the shell reports ready. Recording the context only on a successful
        // post means the first signal after the shell comes up still delivers, instead of being
        // deduplicated against something that was never actually sent.
        if (_hostChannel.PostPush(new { type = MessageType, context }))
        {
            _published = context;
        }
    }
}
