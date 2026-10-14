namespace SelfClaw.Core.Models;

/// <summary>
/// Catch-up runs merge every missed occurrence into one; anything past the grace window is only recorded as stale.
/// </summary>
public enum AutomationMissedRunDisposition
{
    CatchUp = 0,
    Stale = 1
}
