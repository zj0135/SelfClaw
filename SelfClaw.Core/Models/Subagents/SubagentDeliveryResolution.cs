namespace SelfClaw.Core.Models;

public sealed record SubagentDeliveryResolution(
    SubagentDeliveryResolutionKind Kind,
    TurnFinalization? TurnFinalization,
    string? Error,
    DateTimeOffset OccurredAtUtc);
