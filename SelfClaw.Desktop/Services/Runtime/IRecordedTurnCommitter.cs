using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal interface IRecordedTurnCommitter
{
    Task<bool> TryCommitAsync(TurnFinalization finalization);
}
