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
    string AppId,
    string BotDisplayName,
    bool HasSecret,
    string? ProfileId,
    string? ProfileLabel);
