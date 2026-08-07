using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record RecordedTurnCommit(
    TurnFinalization Finalization,
    TurnFinalizationKind Kind,
    string? FinalText,
    string? ErrorMessage);
