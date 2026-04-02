namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderSegment(
    string Kind,
    string Html,
    bool IsPending,
    string? Text = null,
    string? Status = null);
