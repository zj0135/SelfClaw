namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Terminal state of a single tool/command call observed in the agent stream.
/// </summary>
public enum ToolCallStatus
{
    Completed = 0,
    Failed = 1,
    Canceled = 2
}
