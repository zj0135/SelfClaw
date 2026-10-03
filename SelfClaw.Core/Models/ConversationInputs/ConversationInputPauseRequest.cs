namespace SelfClaw.Core.Models;

public sealed record ConversationInputPauseRequest(
    Guid ConversationId,
    long ExpectedQueueRevision,
    bool Paused,
    string? Reason);
