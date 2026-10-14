namespace SelfClaw.Core.Models;

public enum AutomationRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Interrupted = 5,
    Skipped = 6
}
