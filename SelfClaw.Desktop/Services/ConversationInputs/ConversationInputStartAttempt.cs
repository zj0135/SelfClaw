namespace SelfClaw.Desktop.Services.ConversationInputs;

internal readonly record struct ConversationInputStartAttempt(
    ConversationInputStartStatus Status,
    Guid? InputId = null,
    string? Reason = null);
