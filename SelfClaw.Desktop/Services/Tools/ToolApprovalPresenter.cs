using SelfClaw.Desktop.Services.Notifications;
using System.Windows.Threading;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Tools;

internal sealed class ToolApprovalPresenter : IDisposable
{
    private readonly DesktopToolApprovalHandler _handler;
    private readonly AgentActivityCoordinator _activity;
    private readonly DesktopNotificationService _notifications;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private Guid? _publishedId;
    private bool _disposed;
    private int _queued;
    private long _revision;

    public ToolApprovalPresenter(DesktopToolApprovalHandler handler, AgentActivityCoordinator activity,
        DesktopNotificationService notifications, WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _handler = handler;
        _activity = activity;
        _notifications = notifications;
        _channel = channel;
        _dispatcher = dispatcher;
        _handler.ApprovalRequested += OnRequested;
        _handler.ApprovalExpired += OnExpired;
        _activity.SnapshotChanged += OnActivityChanged;
        _channel.ReadyChanged += OnReadyChanged;
    }

    public object Capture(string? requestId = null)
    {
        var snapshot = _activity.CurrentSnapshot;
        return new { type = "tool-approval/state", requestId, revision = Interlocked.Increment(ref _revision),
            approval = snapshot.Approval, conversationTitle = snapshot.ConversationTitle };
    }

    public bool Resolve(Guid id, bool approved) => _handler.TryResolve(id, approved);

    public void Dispose()
    {
        _disposed = true;
        _handler.ApprovalRequested -= OnRequested;
        _handler.ApprovalExpired -= OnExpired;
        _activity.SnapshotChanged -= OnActivityChanged;
        _channel.ReadyChanged -= OnReadyChanged;
    }

    private void OnRequested(ToolApprovalRequest request)
    {
        var arguments = request.ArgumentsJson;
        if (arguments.Length > 2000) arguments = arguments[..2000] + "…";
        _notifications.ShowToolApproval(request.ToolExecutionId, request.ConversationId, request.DisplayName,
            $"{request.Description}{Environment.NewLine}{arguments}");
    }

    private void OnExpired(ToolApprovalRequest request) => _notifications.ShowToolApprovalExpired(request.DisplayName);
    private void OnReadyChanged(bool ready) { if (ready) Publish(force: true); }
    private void OnActivityChanged(object? sender, AgentActivitySnapshot snapshot)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _ = _dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _queued, 0); Publish(force: false); });
    }

    private void Publish(bool force)
    {
        if (_disposed) return;
        var current = _activity.CurrentSnapshot.Approval?.ToolExecutionId;
        if (!force && current == _publishedId) return;
        if (_channel.PostPush(Capture())) _publishedId = current;
    }
}
