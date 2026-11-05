namespace SelfClaw.Desktop.Services.Runtime.Abstractions;

/// <summary>
/// Fire-and-forget kick-off for first-message title generation. Implementations must never throw and
/// must never block the caller: the turn that starts the generation is on its critical path.
/// </summary>
internal interface IConversationTitleStarter
{
    void TryStart(ConversationTitleStart start);
}
