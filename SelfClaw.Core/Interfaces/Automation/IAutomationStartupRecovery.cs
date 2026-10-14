namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Settles Automation runs and turns left by a previous process. The scheduler must complete it before any
/// ticker may start a run.
/// </summary>
public interface IAutomationStartupRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken = default);
}
