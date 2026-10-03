namespace SelfClaw.Core.Models;

public enum ConversationInputUpdateStatus
{
    Applied = 0,
    Missing = 1,
    Conflict = 2,
    Invalid = 3
}

public sealed record ConversationInputUpdateResult(
    ConversationInputUpdateStatus Status,
    ConversationInputRecord? Input,
    long QueueRevision);
