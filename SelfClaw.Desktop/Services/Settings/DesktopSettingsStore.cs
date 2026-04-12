using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public IReadOnlyDictionary<string, DesktopChannelConfiguration> Items { get; init; }
        = new Dictionary<string, DesktopChannelConfiguration>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FeishuDesktopChannelSettings? Feishu { get; init; }

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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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
        var items = new Dictionary<string, DesktopChannelConfiguration>(StringComparer.OrdinalIgnoreCase);

        foreach (var (channelId, configuration) in channels.Items ?? new Dictionary<string, DesktopChannelConfiguration>())
        {
            var normalizedId = channelId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                continue;
            }

            items[normalizedId] = NormalizeChannelConfiguration(configuration);
        }

        if (channels.Feishu is not null && !items.ContainsKey("feishu"))
        {
            items["feishu"] = new DesktopChannelConfiguration
            {
                Enabled = channels.Feishu.Enabled,
                DisplayName = channels.Feishu.DisplayName,
                ProfileId = channels.Feishu.ProfileId,
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["appId"] = channels.Feishu.AppId,
                    ["botDisplayName"] = channels.Feishu.BotDisplayName
                },
                SecretRefs = string.IsNullOrWhiteSpace(channels.Feishu.SecretRef)
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["appSecret"] = channels.Feishu.SecretRef
                    }
            };
        }

        return settings with
        {
            Channels = new DesktopChannelSettings
            {
                Items = items
            }
        };
    }

    private static DesktopChannelConfiguration NormalizeChannelConfiguration(DesktopChannelConfiguration? configuration)
    {
        if (configuration is null)
        {
            return DesktopChannelConfiguration.Default;
        }

        var values = (configuration.Values ?? new Dictionary<string, string>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(
                item => item.Key.Trim(),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        var secretRefs = (configuration.SecretRefs ?? new Dictionary<string, string>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
            .ToDictionary(
                item => item.Key.Trim(),
                item => item.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

        return configuration with
        {
            DisplayName = configuration.DisplayName?.Trim() ?? string.Empty,
            Values = values,
            SecretRefs = secretRefs
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
