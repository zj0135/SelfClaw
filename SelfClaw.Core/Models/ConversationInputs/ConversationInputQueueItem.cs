namespace SelfClaw.Core.Models;

public sealed record ConversationInputQueueItem(
    Guid InputId,
    long Sequence,
    ConversationInputKind Kind,
    ConversationInputStatus Status,
    int Revision,
    string Preview,
    string? ReasonCode,
    string? ErrorMessage);
