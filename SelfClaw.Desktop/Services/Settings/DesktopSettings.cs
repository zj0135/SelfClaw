namespace SelfClaw.Desktop.Services;

public sealed record DesktopSettings
{
    public AppThemePreference ThemePreference { get; init; } = AppThemePreference.System;

    public DesktopChannelSettings Channels { get; init; } = DesktopChannelSettings.Default;

    public static DesktopSettings Default { get; } = new();
}
