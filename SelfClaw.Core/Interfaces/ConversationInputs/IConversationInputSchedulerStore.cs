using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The scheduling-facing store surface: candidate discovery, durable claim, hold and pause.
/// </summary>
public interface IConversationInputSchedulerStore
{
    Task<ConversationInputQueueState> PauseQueueAsync(Guid conversationId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards every queued item that has not started (Pending or Held) and lifts the pause in one
    /// transaction. A user stop uses this so cancelling a turn also drops the backlog and leaves the
    /// queue idle: the next send starts normally instead of waiting behind a manual Resume.
    /// </summary>
    Task<int> DiscardQueuedAsync(Guid conversationId, string reasonCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the FIFO head when it is a Pending FollowUp. A paused queue yields nothing, except
    /// for <paramref name="immediateInputId"/>: an input submitted while the conversation was idle
    /// starts when nothing waits ahead of it, and lifts the pause because no earlier item is held back.
    /// </summary>
    Task<ConversationInputRecord?> TryClaimNextFollowUpAsync(Guid conversationId, Guid ownerRunId, Guid claimId,
        Guid? immediateInputId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Conversations that can actually be claimed right now: not paused and with a Pending FollowUp
    /// at the FIFO head. Paused or Held-head parents are intentionally absent so the scanner never
    /// churns admission (and republishes the transcript) for work it cannot start.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListDispatchableConversationIdsAsync(CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> HoldAsync(Guid inputId, int expectedRevision, string reasonCode,
        CancellationToken cancellationToken = default);
}
