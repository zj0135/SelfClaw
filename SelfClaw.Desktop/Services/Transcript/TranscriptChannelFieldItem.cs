namespace SelfClaw.Desktop.Services;

public sealed record TranscriptChannelFieldItem(
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? Description,
    string? Placeholder,
    string Value,
    bool HasValue);
