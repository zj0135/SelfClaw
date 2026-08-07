using SelfClaw.Infrastructure.Agents.Subagents.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Abstractions;

internal interface ISubagentTaskRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord> CreateAsync(
        SubagentTaskCreation creation,
        CancellationToken cancellationToken = default);

    Task<SubagentTaskRecord?> GetAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubagentTaskRecord>> ListAsync(
        Guid parentConversationId,
        CancellationToken cancellationToken = default);

    Task<SubagentDeliveryRecord?> GetDeliveryAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default);
}
