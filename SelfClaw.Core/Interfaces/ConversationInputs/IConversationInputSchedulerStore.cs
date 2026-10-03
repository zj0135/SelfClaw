using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The scheduling-facing store surface: candidate discovery, durable claim and startup recovery.
/// </summary>
public interface IConversationInputSchedulerStore
{
    Task<ConversationInputQueueState> PauseQueueAsync(Guid conversationId, string reason, CancellationToken cancellationToken = default);

    Task<ConversationInputRecord?> TryClaimNextFollowUpAsync(Guid conversationId, Guid ownerRunId, Guid claimId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conversations that can actually be claimed right now: not paused and with a Pending FollowUp
    /// at the FIFO head. Paused or Held-head parents are intentionally absent so the scanner never
    /// churns admission (and republishes the transcript) for work it cannot start.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListDispatchableConversationIdsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationInputQueueState>> RecoverStartupAsync(CancellationToken cancellationToken = default);
}
