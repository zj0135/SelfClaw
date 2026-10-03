using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyConversationInputRepository : IConversationInputRepository
{
    public Func<Guid, CancellationToken, Task<bool>>? HasUnprocessed { get; set; }

    public Task<ConversationInputBatch?> ReadClaimedBatchAsync(Guid conversationId, Guid turnId, Guid ownerRunId, CancellationToken cancellationToken = default)
        => Task.FromResult<ConversationInputBatch?>(null);

    public Task<ConversationInputConsumption?> ReadConsumptionAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default)
        => Task.FromResult<ConversationInputConsumption?>(null);

    public Task<ConversationInputConsumption> CommitBoundaryAsync(ConversationInputBoundaryCommit commit, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This fixture has no input batches.");

    public Task<bool> HasUnprocessedInputsAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => HasUnprocessed?.Invoke(conversationId, cancellationToken) ?? Task.FromResult(false);
}
