namespace SelfClaw.Core.Models;

public sealed record SubagentDeliveryLease(
    Guid LeaseToken,
    Guid ContinuationTurnId,
    Guid ParentConversationId,
    Guid ParentTurnId,
    string ParentExecutionSnapshotJson,
    IReadOnlyList<SubagentDeliveryRecord> Deliveries,
    DateTimeOffset LeasedUntilUtc);
