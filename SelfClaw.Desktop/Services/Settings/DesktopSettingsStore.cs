using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public enum AppThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2
}

public sealed record ThemeOption(
    AppThemePreference Value,
    string Label);

public sealed record FeishuDesktopChannelSettings
{
    public bool Enabled { get; init; }

    public string DisplayName { get; init; } = "我的飞书";

    public string AppId { get; init; } = string.Empty;

    public string SecretRef { get; init; } = string.Empty;

    public string BotDisplayName { get; init; } = string.Empty;

    public Guid? ProfileId { get; init; }

    public static FeishuDesktopChannelSettings Default { get; } = new();
}

public sealed record DesktopChannelSettings
{
    public FeishuDesktopChannelSettings Feishu { get; init; } = FeishuDesktopChannelSettings.Default;

    public static DesktopChannelSettings Default { get; } = new();
}

public sealed record DesktopSettings
{
    public AppThemePreference ThemePreference { get; init; } = AppThemePreference.System;

    public DesktopChannelSettings Channels { get; init; } = DesktopChannelSettings.Default;

    public static DesktopSettings Default { get; } = new();
}

public sealed class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private readonly object _syncRoot = new();

    public DesktopSettingsStore(StoragePaths storagePaths)
    {
        _settingsPath = Path.Combine(storagePaths.AppDataDirectory, "desktop-settings.json");
    }

    public DesktopSettings Load()
    {
        lock (_syncRoot)
        {
            return LoadCore();
        }
    }

    public void Save(DesktopSettings settings)
    {
        lock (_syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
    }

    private DesktopSettings LoadCore()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return DesktopSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            return Normalize(JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions));
        }
        catch
        {
            return DesktopSettings.Default;
        }
    }

    private static DesktopSettings Normalize(DesktopSettings? settings)
    {
        if (settings is null)
        {
            return DesktopSettings.Default;
        }

        var channels = settings.Channels ?? DesktopChannelSettings.Default;
        var feishu = channels.Feishu ?? FeishuDesktopChannelSettings.Default;

        return settings with
        {
            Channels = channels with
            {
                Feishu = feishu with
                {
                    DisplayName = string.IsNullOrWhiteSpace(feishu.DisplayName)
                        ? FeishuDesktopChannelSettings.Default.DisplayName
                        : feishu.DisplayName.Trim(),
                    AppId = feishu.AppId?.Trim() ?? string.Empty,
                    SecretRef = feishu.SecretRef?.Trim() ?? string.Empty,
                    BotDisplayName = feishu.BotDisplayName?.Trim() ?? string.Empty
                }
            }
        };
    }
}

internal static class SystemThemeReader
{
    private const string PersonalizePath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize";
    private const string AppsUseLightThemeValueName = "AppsUseLightTheme";

    public static bool IsDarkModeEnabled()
    {
        using var personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizePath);
        if (personalizeKey?.GetValue(AppsUseLightThemeValueName) is int appsUseLightTheme)
        {
            return appsUseLightTheme == 0;
        }

        return false;
    }
}
