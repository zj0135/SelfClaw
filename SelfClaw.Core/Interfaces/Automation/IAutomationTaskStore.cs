using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAutomationTaskStore
{
    Task<AutomationTaskRecord?> GetAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationTaskRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<AutomationTaskRecord> SaveAsync(AutomationTaskRecord task, CancellationToken cancellationToken = default);

    Task<AutomationTaskRecord?> SetEnabledAsync(
        Guid taskId,
        bool isEnabled,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken = default);
}
