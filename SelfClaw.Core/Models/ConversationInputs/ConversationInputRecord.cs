namespace SelfClaw.Core.Models;

public sealed record ConversationInputRecord(
    Guid Id,
    Guid ConversationId,
    string ClientRequestId,
    long Sequence,
    ConversationInputKind Kind,
    Guid? TargetTurnId,
    ConversationInputStatus Status,
    int Revision,
    string Prompt,
    ConversationInputExecutionSnapshot? ExecutionSnapshot,
    Guid? ClaimId,
    Guid? OwnerRunId,
    Guid? ConsumedTurnId,
    Guid? MessageId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    string? ReasonCode,
    string? ErrorMessage,
    string? RequestPrompt = null,
    string? RequestFingerprint = null);
