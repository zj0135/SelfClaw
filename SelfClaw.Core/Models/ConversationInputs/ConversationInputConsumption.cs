namespace SelfClaw.Core.Models;

public sealed record ConversationInputConsumption(
    Guid ClaimId,
    ConversationTurnRecord Turn,
    IReadOnlyList<MessageRecord> Messages);
