namespace SelfClaw.Core.Models;

public enum ConversationTurnStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Cancelled = 3,
    Truncated = 4,
    Blocked = 5,
    Interrupted = 6
}
