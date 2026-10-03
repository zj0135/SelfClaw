namespace SelfClaw.Core.Models;

public enum ConversationInputSubmitOutcome
{
    Started = 0,
    Queued = 1,
    SteerPending = 2,
    Rejected = 3
}

public sealed record ConversationInputSubmitResult(
    bool Accepted,
    Guid? InputId,
    int Revision,
    long QueueRevision,
    ConversationInputSubmitOutcome Outcome,
    string? Reason = null);
