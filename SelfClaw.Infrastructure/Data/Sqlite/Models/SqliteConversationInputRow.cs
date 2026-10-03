namespace SelfClaw.Infrastructure.Data.Sqlite.Models;

internal sealed record SqliteConversationInputRow(
    Guid Id,
    Guid ConversationId,
    long Sequence,
    int Revision,
    Guid TargetTurnId,
    Guid ClaimId,
    Guid OwnerRunId,
    int Status,
    string Prompt,
    Guid? ConsumedTurnId,
    Guid? MessageId);
