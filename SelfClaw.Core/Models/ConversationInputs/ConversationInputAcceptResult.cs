namespace SelfClaw.Core.Models;

public sealed record ConversationInputAcceptResult(
    ConversationInputAcceptStatus Status,
    ConversationInputRecord? Input,
    long QueueRevision,
    string? Reason = null);
