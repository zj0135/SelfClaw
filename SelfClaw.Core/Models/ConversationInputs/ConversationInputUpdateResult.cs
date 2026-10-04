namespace SelfClaw.Core.Models;

public sealed record ConversationInputUpdateResult(
    ConversationInputUpdateStatus Status,
    ConversationInputRecord? Input,
    long QueueRevision,
    string? Reason = null);
