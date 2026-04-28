using System.Text.Json.Serialization;

namespace SelfClaw.Desktop.Services;

public sealed record DesktopSettings
{
    public const int DefaultModelContextWindow = 128_000;
    public const int DefaultModelAutoCompactTokenLimit = 96_000;

    [JsonPropertyName("theme_preference")]
    public AppThemePreference ThemePreference { get; init; } = AppThemePreference.System;

    [JsonPropertyName("model_context_window")]
    public int ModelContextWindow { get; init; } = DefaultModelContextWindow;

    [JsonPropertyName("model_auto_compact_token_limit")]
    public int ModelAutoCompactTokenLimit { get; init; } = DefaultModelAutoCompactTokenLimit;

    public DesktopChannelSettings Channels { get; init; } = DesktopChannelSettings.Default;

    public static DesktopSettings Default { get; } = new();
}
