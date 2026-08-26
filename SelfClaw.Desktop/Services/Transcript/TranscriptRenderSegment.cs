namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderSegment(
    string Kind,
    string Markdown,
    bool IsPending,
    string? Text = null,
    string? Status = null,
    string? SegmentId = null,
    string? DurationText = null,
    string? DetailTitle = null,
    string? DetailText = null,
    string? ToolName = null,
    string? SourceKind = null,
    string? SourceId = null,
    string? DisplayName = null);
