namespace SelfClaw.Core.Models;

public sealed record SubagentMailboxKey(
    Guid ParentConversationId,
    Guid ParentTurnId,
    string ParentExecutionSnapshotJson,
    DateTimeOffset FirstCreatedAtUtc);
