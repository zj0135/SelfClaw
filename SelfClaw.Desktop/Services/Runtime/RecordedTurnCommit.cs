using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed record RecordedTurnCommit(
    ConversationTurnCommit Finalization,
    TurnFinalizationKind Kind,
    string? FinalText,
    string? ErrorMessage);
