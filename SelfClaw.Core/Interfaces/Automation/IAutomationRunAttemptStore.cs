using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Persists the resolved facts of the attempt that is about to run. Kept separate from
/// <see cref="IAutomationRunStore"/> because it is written while the run is still Running.
/// </summary>
public interface IAutomationRunAttemptStore
{
    Task<AutomationRunRecord?> RecordAttemptAsync(
        AutomationRunAttempt attempt,
        CancellationToken cancellationToken = default);
}
