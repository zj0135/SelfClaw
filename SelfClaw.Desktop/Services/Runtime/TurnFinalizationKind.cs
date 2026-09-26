namespace SelfClaw.Desktop.Services.Runtime;

internal enum TurnFinalizationKind
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,

    /// <summary>
    /// The answer stopped at the output-token cap. Persisted with its partial content
    /// intact so the user can decide whether to continue.
    /// </summary>
    Truncated = 3,

    /// <summary>
    /// A plugin hook blocked the turn before any model call. Interactive turns only; delegated
    /// turns rewrite it to <see cref="Failed"/> so durable task state stays consistent.
    /// </summary>
    Blocked = 4
}
