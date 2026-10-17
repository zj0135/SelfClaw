namespace SelfClaw.Desktop.Services.Transcript.Views;

/// <summary>
/// Payload of a <c>compression</c> divider: the summary that replaced the history up to the boundary,
/// plus the sizes needed to describe the reduction.
/// </summary>
public sealed record TranscriptCompactionNotice(
    string CompactionId,
    int MessageCount,
    int? SourceTokens,
    int? SummaryTokens,
    string? Model,
    string Summary);
