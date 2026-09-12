namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityPanelQuery(
    Guid SubscriptionId,
    Guid? ParentConversationId,
    long Generation,
    string? Cursor = null,
    Guid? DetailSelectionId = null,
    Guid? TaskId = null,
    int? BlockOffset = null,
    string? ContentVersion = null);
