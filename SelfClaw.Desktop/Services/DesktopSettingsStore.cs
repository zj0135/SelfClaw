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

public sealed record DesktopSettings(AppThemePreference ThemePreference)
{
    public static DesktopSettings Default { get; } = new(AppThemePreference.System);
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

    public DesktopSettingsStore(StoragePaths storagePaths)
    {
        _settingsPath = Path.Combine(storagePaths.AppDataDirectory, "desktop-settings.json");
    }

    public DesktopSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return DesktopSettings.Default;
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<DesktopSettings>(json, JsonOptions) ?? DesktopSettings.Default;
        }
        catch
        {
            return DesktopSettings.Default;
        }
    }

    public void Save(DesktopSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
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
