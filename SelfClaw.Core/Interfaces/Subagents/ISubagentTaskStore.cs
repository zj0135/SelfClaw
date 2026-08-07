using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentTaskStore
{
    Task<SubagentTaskRecord> CreateAsync(
        SubagentTaskCreation creation,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> GetAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> RequestCancellationAsync(
        Guid parentConversationId,
        Guid taskId,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> TryCompleteAsync(
        Guid taskId,
        SubagentTaskStatus expectedStatus,
        SubagentTaskCompletion completion,
        CancellationToken cancellationToken = default);
}
