namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Terminal outcome of an agent run, derived from process exit code, cancellation
/// state and stream completion markers.
/// </summary>
public enum RunCompletionStatus
{
    Succeeded = 0,
    Failed = 1
}
