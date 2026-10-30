using System.Windows.Threading;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.Questions;

internal sealed class UserQuestionPresenter : IDisposable
{
    private readonly DesktopUserQuestionHandler _handler;
    private readonly AgentActivityCoordinator _activity;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private Guid? _publishedId;
    private bool _disposed;
    private int _queued;
    private long _revision;

    public UserQuestionPresenter(DesktopUserQuestionHandler handler, AgentActivityCoordinator activity,
        WebViewHostChannel channel, Dispatcher dispatcher)
    {
        _handler = handler;
        _activity = activity;
        _channel = channel;
        _dispatcher = dispatcher;
        _activity.SnapshotChanged += OnActivityChanged;
        _channel.ReadyChanged += OnReadyChanged;
    }

    public object Capture(string? requestId = null)
    {
        var snapshot = _activity.CurrentSnapshot;
        return new
        {
            type = "user-question/state",
            requestId,
            revision = Interlocked.Increment(ref _revision),
            conversationTitle = snapshot.ConversationTitle,
            question = Project(snapshot.Question)
        };
    }

    public UserQuestionRequest? GetPendingRequest(Guid requestId) => _handler.GetPendingRequest(requestId);

    public bool Resolve(Guid requestId, UserQuestionAnswer answer) => _handler.TryAnswer(requestId, answer);

    public void Dispose()
    {
        _disposed = true;
        _activity.SnapshotChanged -= OnActivityChanged;
        _channel.ReadyChanged -= OnReadyChanged;
    }

    // Projected field by field instead of shipping the record: the dock reads this shape, and a turn id it
    // never uses would become part of the frontend contract.
    private static object? Project(UserQuestionRequest? question)
        => question is null
            ? null
            : new
            {
                requestId = question.RequestId,
                conversationId = question.ConversationId,
                prompts = question.Prompts.Select(prompt => new
                {
                    promptId = prompt.PromptId,
                    header = prompt.Header,
                    question = prompt.Question,
                    multiSelect = prompt.MultiSelect,
                    options = prompt.Options.Select(option => new
                    {
                        label = option.Label,
                        description = option.Description
                    })
                })
            };

    private void OnReadyChanged(bool ready) { if (ready) Publish(force: true); }

    private void OnActivityChanged(object? sender, AgentActivitySnapshot snapshot)
    {
        if (_disposed || _dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _queued, 1) != 0) return;
        _ = _dispatcher.InvokeAsync(() => { Interlocked.Exchange(ref _queued, 0); Publish(force: false); });
    }

    private void Publish(bool force)
    {
        if (_disposed) return;
        var current = _activity.CurrentSnapshot.Question?.RequestId;
        if (!force && current == _publishedId) return;
        if (_channel.PostPush(Capture())) _publishedId = current;
    }
}
