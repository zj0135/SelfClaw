namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Captures everything the detached title generation needs from the first user message.
/// <paramref name="ExpectedTitle"/> is the title at kick-off and acts as the compare-and-swap base:
/// the generated title only lands while the conversation still carries it.
/// </summary>
internal sealed record ConversationTitleStart(
    Guid ConversationId,
    string ExpectedTitle,
    string Prompt,
    Guid? ModelProfileId);
