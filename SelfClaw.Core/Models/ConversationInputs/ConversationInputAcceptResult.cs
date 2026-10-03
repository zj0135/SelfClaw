namespace SelfClaw.Core.Models;

public enum ConversationInputAcceptStatus
{
    Accepted = 0,
    Duplicate = 1,
    Conflict = 2,
    Full = 3,
    Unsupported = 4,
    Rejected = 5
}

public sealed record ConversationInputAcceptResult(
    ConversationInputAcceptStatus Status,
    ConversationInputRecord? Input,
    long QueueRevision,
    string? Reason = null);
