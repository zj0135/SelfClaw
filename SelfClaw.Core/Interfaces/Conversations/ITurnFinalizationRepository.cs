using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ITurnFinalizationRepository
{
    Task<bool> TryFinalizeTurnAsync(
        TurnFinalization finalization,
        CancellationToken cancellationToken = default);
}
