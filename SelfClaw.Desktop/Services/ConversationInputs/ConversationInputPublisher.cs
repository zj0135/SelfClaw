using SelfClaw.Desktop.Services.ConversationInputs.Views;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.WebView;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Publishes the bounded queue snapshot for the single active subscription. Notifications are
/// invalidations only; every push re-reads the durable store, so an old revision can never roll a
/// newer selection back.
/// </summary>
internal sealed class ConversationInputPublisher : IDisposable
{
    internal const int MaximumSnapshotBytes = 256 * 1024;

    private readonly IConversationInputStore _store;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly WebViewHostChannel _channel;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ConversationInputPublisher> _logger;
    private Guid? _subscriptionId;
    private Guid? _conversationId;
    private bool _disposed;

    public ConversationInputPublisher(IConversationInputStore store, ConversationInputChangeNotifier changes,
        WebViewHostChannel channel, Dispatcher dispatcher, ILogger<ConversationInputPublisher> logger)
    {
        _store = store;
        _changes = changes;
        _channel = channel;
        _dispatcher = dispatcher;
        _logger = logger;
        _changes.Changed += OnChanged;
        _channel.ReadyChanged += OnReadyChanged;
    }

    internal Task SubscribeAsync(Guid subscriptionId, Guid conversationId, string? requestId, CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (subscriptionId == Guid.Empty || conversationId == Guid.Empty)
            throw new ArgumentException("A queue subscription requires both identities.");
        CurrentState = null;
        _subscriptionId = subscriptionId;
        _conversationId = conversationId;
        return PushAsync(requestId, cancellationToken);
    }

    internal Task GetStateAsync(Guid subscriptionId, string? requestId, CancellationToken cancellationToken)
    {
        _dispatcher.VerifyAccess();
        RequireSubscription(subscriptionId);
        return PushAsync(requestId, cancellationToken);
    }

    internal void Unsubscribe(Guid subscriptionId)
    {
        _dispatcher.VerifyAccess();
        if (_subscriptionId == subscriptionId)
        {
            _subscriptionId = null;
            _conversationId = null;
        }
    }

    internal ConversationInputQueueState? CurrentState { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changes.Changed -= OnChanged;
        _channel.ReadyChanged -= OnReadyChanged;
        _subscriptionId = null;
        _conversationId = null;
    }

    private void RequireSubscription(Guid subscriptionId)
    {
        if (_subscriptionId != subscriptionId) throw new InvalidOperationException("conversation-input-subscription-invalid");
    }

    private void OnChanged(Guid conversationId)
    {
        if (_disposed || _conversationId != conversationId) return;
        if (!_channel.IsReady || _dispatcher.HasShutdownStarted) return;
        SchedulePush(conversationId);
    }

    private void OnReadyChanged(bool ready)
    {
        if (!ready || _disposed || _conversationId is null) return;
        SchedulePush(null);
    }

    // Marshals onto the dispatcher and observes the async push so a failure can never surface as an
    // unobserved exception or an async-void continuation.
    private void SchedulePush(Guid? expectedConversation)
    {
        _ = _dispatcher.InvokeAsync(() => PushSafelyAsync(expectedConversation)).Task.Unwrap();
    }

    private async Task PushSafelyAsync(Guid? expectedConversation)
    {
        try
        {
            if (expectedConversation is { } id && _conversationId != id) return;
            await PushAsync(null, CancellationToken.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to push the queue state for {ConversationId}.", expectedConversation);
        }
    }

    private async Task PushAsync(string? requestId, CancellationToken cancellationToken)
    {
        if (_conversationId is not { } conversationId || _subscriptionId is not { } subscriptionId) return;
        var state = await Task.Run(() => _store.GetQueueStateAsync(conversationId, cancellationToken), cancellationToken).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            if (_disposed || _conversationId != conversationId || _subscriptionId != subscriptionId) return;
            if (CurrentState is { } newer && newer.QueueRevision > state.QueueRevision) state = newer;
            CurrentState = state;
            var payload = ConversationQueueStateFormatter.Create(subscriptionId, state, requestId);

            _channel.PostResponse(payload);
        });
    }

}
