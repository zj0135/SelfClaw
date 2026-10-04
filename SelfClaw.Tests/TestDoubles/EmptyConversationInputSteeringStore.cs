using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyConversationInputSteeringStore : IConversationInputSteeringStore
{
    public Task<ConversationInputBatch?> TryClaimPendingSteersAsync(Guid conversationId, Guid turnId, Guid ownerRunId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<ConversationInputBatch?>(null);

    public Task<int> ReleasePendingSteersToFollowUpAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<int> HoldUnconsumedSteersAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0);
}
