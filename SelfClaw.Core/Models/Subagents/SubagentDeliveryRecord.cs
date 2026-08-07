namespace SelfClaw.Core.Models;

public sealed record SubagentDeliveryRecord(
    Guid Id,
    Guid TaskId,
    Guid ParentConversationId,
    Guid ParentTurnId,
    SubagentDeliveryStatus Status,
    string EnvelopeJson,
    int EnvelopeBytes,
    Guid? LeaseToken,
    DateTimeOffset? LeasedUntilUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc,
    Guid? ContinuationTurnId,
    string? LastError,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset? DeadLetteredAtUtc);
