using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Task-level seam for the automation module: it owns validation, persistence side effects (schedule
/// rebuild + change notification) and history reclamation. The bridge and the scheduler talk to it instead
/// of re-implementing those rules against the stores.
/// </summary>
public interface IAutomationTaskService
{
    Task<AutomationTaskSaveResult> SaveAsync(AutomationTaskRecord task, CancellationToken cancellationToken = default);

    Task<AutomationTaskRecord?> SetEnabledAsync(Guid taskId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels any active run, waits up to <c>AutomationDefaults.DeleteWait</c> for it to settle, then
    /// deletes the task, its runs and their conversations. A timeout aborts the deletion untouched.
    /// </summary>
    Task<AutomationTaskDeleteResult> DeleteAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<AutomationHistoryReclaimResult> ReclaimHistoryAsync(
        AutomationHistoryReclaimMode mode,
        Guid? taskId,
        Guid? runId,
        CancellationToken cancellationToken = default);

    Task<AutomationHistoryUsage> GetHistoryUsageAsync(CancellationToken cancellationToken = default);
}
