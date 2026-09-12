namespace SelfClaw.Desktop.Services.Activities.Models;

internal sealed record ActivityContentReference(
    string ContentId,
    string? SegmentId,
    string Field,
    int TotalCharacters,
    int PreviewCharacters,
    bool IsTruncated);
