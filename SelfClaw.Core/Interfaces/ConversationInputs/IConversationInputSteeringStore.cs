using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The durable steer boundary surface: claim the pending high-water batch, release it to the
/// follow-up queue when the turn cannot steer, and hold unconsumed steers on abnormal closure.
/// </summary>
public interface IConversationInputSteeringStore
{
    Task<ConversationInputBatch?> TryClaimPendingSteersAsync(Guid conversationId, Guid turnId, Guid ownerRunId,
        CancellationToken cancellationToken = default);

    Task<int> ReleasePendingSteersToFollowUpAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default);

    Task<int> HoldUnconsumedSteersAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default);
}
