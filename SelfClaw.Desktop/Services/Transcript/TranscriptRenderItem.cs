namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderItem(
    string Id,
    string Kind,
    string Role,
    string Status,
    IReadOnlyList<TranscriptRenderSegment> Segments,
    bool IsThinking,
    string Timestamp,
    IReadOnlyList<TranscriptImageAttachment>? Attachments = null);
