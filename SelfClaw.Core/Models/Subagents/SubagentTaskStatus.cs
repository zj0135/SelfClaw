namespace SelfClaw.Core.Models;

public enum SubagentTaskStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    Interrupted = 5
}
