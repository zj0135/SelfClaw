namespace SelfClaw.Core.Models;

public sealed record ConversationInputQueueState(
    Guid ConversationId,
    long QueueRevision,
    bool Paused,
    string? PauseReason,
    bool CanSteer,
    IReadOnlyList<ConversationInputQueueItem> Items);
