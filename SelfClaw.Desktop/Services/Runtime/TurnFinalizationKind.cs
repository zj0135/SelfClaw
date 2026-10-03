namespace SelfClaw.Desktop.Services.Runtime;

internal enum TurnFinalizationKind
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
    Truncated = 3,
    Blocked = 4,
    Interrupted = 5
}
