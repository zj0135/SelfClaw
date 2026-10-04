namespace SelfClaw.Core.Models;

public sealed record ConversationInputSubmitResult(
    bool Accepted,
    Guid? InputId,
    int Revision,
    long QueueRevision,
    ConversationInputSubmitOutcome Outcome,
    string? Reason = null);
