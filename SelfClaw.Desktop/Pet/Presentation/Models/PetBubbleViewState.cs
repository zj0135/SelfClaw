namespace SelfClaw.Desktop.Pet;

public sealed record PetBubbleViewState(
    Guid? ConversationId,
    string AgentLabel,
    string Headline,
    string? Detail,
    bool IsVisible,
    bool IsPinned,
    PetWorkState WorkState);
