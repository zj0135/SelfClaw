namespace SelfClaw.Desktop.Services;

public sealed record TranscriptChannelItem(
    string Id,
    string Name,
    string Description,
    bool IsEnabled,
    bool IsConfigured,
    string Status,
    string StatusLabel,
    string? StatusDetail,
    string DisplayName,
    string? ProfileId,
    string? ProfileLabel,
    IReadOnlyList<TranscriptChannelSummaryItem> SummaryItems,
    IReadOnlyList<TranscriptChannelFieldItem> Fields);
