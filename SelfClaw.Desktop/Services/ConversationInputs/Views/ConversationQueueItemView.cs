namespace SelfClaw.Desktop.Services.ConversationInputs.Views;

internal sealed record ConversationQueueItemView(Guid InputId, long Sequence, string Kind, string Status,
    int Revision, string Preview, string? ReasonCode, string? ErrorMessage);

