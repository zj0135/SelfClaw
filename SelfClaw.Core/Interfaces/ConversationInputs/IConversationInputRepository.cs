using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IConversationInputRepository
{
    Task<ConversationInputBatch?> ReadClaimedBatchAsync(Guid conversationId, Guid turnId, Guid ownerRunId, CancellationToken cancellationToken = default);

    Task<ConversationInputConsumption?> ReadConsumptionAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default);

    Task<ConversationInputConsumption> CommitBoundaryAsync(ConversationInputBoundaryCommit commit, CancellationToken cancellationToken = default);

    Task<bool> HasUnprocessedInputsAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
