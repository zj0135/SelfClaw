namespace SelfClaw.Core.Models;

/// <summary>
/// Which slice of run history a reclamation request targets. Reclamation only ever removes terminal runs,
/// so an active run must be stopped first.
/// </summary>
public enum AutomationHistoryReclaimMode
{
    Run = 0,
    TaskHistory = 1,
    AllHistory = 2
}
