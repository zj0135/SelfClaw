using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAutomationRunStore
{
    Task<AutomationRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
        Guid? taskId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a run. When <paramref name="skipWhenTaskActive"/> is set and the task already has a
    /// Queued/Running run, a Skipped(Overlap) record is inserted instead. Returns null when the same
    /// (task, scheduled_for) occurrence already exists.
    /// </summary>
    Task<AutomationRunRecord?> TryInsertAsync(
        AutomationRunRecord run,
        bool skipWhenTaskActive,
        CancellationToken cancellationToken = default);

    Task<AutomationRunRecord?> TryClaimAsync(
        Guid runId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task<AutomationRunRecord?> TryCompleteAsync(
        Guid runId,
        AutomationRunRecord terminal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes terminal runs and the conversations they own (the transcript travels with the run).
    /// A runId targets one run, a taskId targets that task's terminal runs, and neither clears all
    /// terminal runs. Active (Queued/Running) runs are never touched. Returns the number deleted.
    /// </summary>
    Task<int> DeleteTerminalRunsAsync(Guid? taskId, Guid? runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rough retained transcript size per task plus the global total, computed from SQL text lengths.
    /// Read-only observation aid for the automation workspace.
    /// </summary>
    Task<AutomationHistoryUsage> GetHistoryUsageAsync(CancellationToken cancellationToken = default);
}
