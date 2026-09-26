namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Terminal state of a single tool/command call observed in the agent stream.
/// </summary>
public enum ToolCallStatus
{
    Completed = 0,
    Failed = 1,
    Canceled = 2,

    /// <summary>A plugin hook blocked the call before it executed.</summary>
    Blocked = 3
}
