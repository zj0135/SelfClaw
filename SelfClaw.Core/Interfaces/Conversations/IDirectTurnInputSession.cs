using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IDirectTurnInputSession
{
    Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The final boundary read: claims the pending high-water batch or, when none exists, closes
    /// steer intake in the same critical section. No steer accepted after this call may enter the
    /// current turn.
    /// </summary>
    Task<ConversationInputBatch?> ReadFinalBoundaryAsync(CancellationToken cancellationToken = default);

    Task<ConversationInputConsumption> WaitForCommitAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitions every steer this turn has not consumed (still pending or claimed) to Held. Called
    /// when the turn cannot carry the input further, so nothing is silently dropped or replayed.
    /// </summary>
    Task HoldUnconsumedAsync(string reasonCode, CancellationToken cancellationToken = default);
}
