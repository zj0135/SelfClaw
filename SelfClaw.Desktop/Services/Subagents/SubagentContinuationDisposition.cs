namespace SelfClaw.Desktop.Services.Subagents;

internal enum SubagentContinuationDisposition
{
    None = 0,
    Delivered = 1,
    Retrying = 2,
    DeadLetter = 3,
    LeaseLost = 4
}
