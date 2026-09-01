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
    Truncated = 3
}
