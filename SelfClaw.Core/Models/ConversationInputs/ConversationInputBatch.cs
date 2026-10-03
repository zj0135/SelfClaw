namespace SelfClaw.Core.Models;

public sealed record ConversationInputBatch(
    Guid ConversationId,
    Guid TurnId,
    Guid OwnerRunId,
    Guid ClaimId,
    IReadOnlyList<ConversationInputItem> Inputs);
