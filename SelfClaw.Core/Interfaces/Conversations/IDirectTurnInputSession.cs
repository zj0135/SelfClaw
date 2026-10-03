using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IDirectTurnInputSession
{
    Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default);

    Task<ConversationInputConsumption> WaitForCommitAsync(ConversationInputBatch batch, CancellationToken cancellationToken = default);
}
