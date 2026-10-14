using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Settles Automation runs and turns left by a previous process. The scheduler must complete it before any
/// ticker may start a run. Returns the runs that were marked Interrupted so the shell can notify about them.
/// </summary>
public interface IAutomationStartupRecovery
{
    Task<IReadOnlyList<AutomationRunRecord>> RecoverAsync(CancellationToken cancellationToken = default);
}
