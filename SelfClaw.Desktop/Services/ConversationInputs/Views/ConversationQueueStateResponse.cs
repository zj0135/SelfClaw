namespace SelfClaw.Desktop.Services.ConversationInputs.Views;

internal sealed record ConversationQueueStateResponse(string Type, Guid SubscriptionId, Guid ConversationId,
    long QueueRevision, bool Paused, string? PauseReason, bool CanSteer,
    IReadOnlyList<ConversationQueueItemView> Items, bool Truncated, string? RequestId);

