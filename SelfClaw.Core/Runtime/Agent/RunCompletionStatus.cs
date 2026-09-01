namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Terminal outcome of an agent run, derived from process exit code, cancellation
/// state and stream completion markers.
/// </summary>
public enum RunCompletionStatus
{
    Succeeded = 0,
    Failed = 1,

    /// <summary>
    /// The model stopped because it hit its output-token cap. The partial answer is
    /// valid and kept, and continuing is the user's call rather than the runtime's.
    /// </summary>
    Truncated = 2
}
