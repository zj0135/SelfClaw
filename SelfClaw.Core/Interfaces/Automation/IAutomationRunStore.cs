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
}
