namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityContentResponse(
    string? RequestId,
    Guid SubscriptionId,
    Guid DetailSelectionId,
    Guid TaskId,
    string ContentVersion,
    string ContentId,
    string Text,
    int Offset,
    int? NextOffset,
    int TotalCharacters,
    bool IsTruncated,
    string Type = "activity-panel/content");
