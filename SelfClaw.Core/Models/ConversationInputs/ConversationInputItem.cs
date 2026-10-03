namespace SelfClaw.Core.Models;

public sealed record ConversationInputItem(
    Guid InputId,
    long Sequence,
    int Revision,
    Guid MessageId,
    string Prompt);
