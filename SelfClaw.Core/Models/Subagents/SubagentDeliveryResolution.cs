namespace SelfClaw.Core.Models;

public sealed record SubagentDeliveryResolution(
    SubagentDeliveryResolutionKind Kind,
    ConversationTurnCommit? TurnFinalization,
    string? Error,
    DateTimeOffset OccurredAtUtc);
