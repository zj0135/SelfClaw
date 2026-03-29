namespace SelfClaw.Desktop.Services;

public sealed record TranscriptRenderItem(
    string Id,
    string Kind,
    string Role,
    string Status,
    string Title,
    string Html,
    string? ThinkingHtml,
    bool IsThinking,
    string? ArgumentsJson,
    string? Summary,
    double? DurationMs,
    string Timestamp);
