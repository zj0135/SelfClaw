namespace SelfClaw.Core.Models;

public sealed record ConversationInputEditRequest(
    Guid InputId,
    int ExpectedRevision,
    string Prompt);
