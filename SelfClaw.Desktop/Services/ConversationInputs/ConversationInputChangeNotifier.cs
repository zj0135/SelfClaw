namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// A process-local invalidation signal. It never carries queue state, so a lost notification is
/// always recoverable by re-reading the durable store.
/// </summary>
internal sealed class ConversationInputChangeNotifier
{
    public event Action<Guid>? Changed;

    public void Notify(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return;
        Changed?.Invoke(conversationId);
    }
}
