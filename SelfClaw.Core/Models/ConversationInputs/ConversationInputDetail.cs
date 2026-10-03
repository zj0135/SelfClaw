namespace SelfClaw.Core.Models;

public sealed record ConversationInputDetail(
    Guid InputId,
    int Revision,
    string Prompt,
    ConversationInputStatus Status);
