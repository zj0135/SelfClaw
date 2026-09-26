namespace SelfClaw.Core.Models;

public enum ToolExecutionStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    AwaitingApproval = 3,
    Cancelled = 4,

    /// <summary>A plugin hook blocked the call before it executed.</summary>
    Blocked = 5
}
