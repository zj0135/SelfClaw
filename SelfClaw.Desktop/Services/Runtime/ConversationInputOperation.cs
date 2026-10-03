namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationInputOperation : IDisposable
{
    private readonly ConversationRunCoordinator _owner;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    internal ConversationInputOperation(ConversationRunCoordinator owner, Guid conversationId, CancellationToken token)
    {
        _owner = owner;
        ConversationId = conversationId;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken = _cancellation.Token;
    }

    internal Guid ConversationId { get; }
    internal CancellationToken CancellationToken { get; }
    internal Task Completion => _completion.Task;

    internal void Cancel()
    {
        try { _cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.CompleteInputOperation(this);
        _cancellation.Dispose();
        _completion.TrySetResult();
    }
}
