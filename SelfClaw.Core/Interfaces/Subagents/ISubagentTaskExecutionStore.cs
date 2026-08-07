using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentTaskExecutionStore
{
    Task<IReadOnlyList<SubagentTaskRecord>> ListByStatusAsync(
        SubagentTaskStatus status,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> TryClaimNextAsync(
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> TryCompleteAsync(
        Guid taskId,
        SubagentTaskStatus expectedStatus,
        SubagentTaskCompletion completion,
        CancellationToken cancellationToken = default);
}
