using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopChannelSettingsStore
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _settingsPath;
    private readonly object _syncRoot = new();

    public DesktopChannelSettingsStore(StoragePaths storagePaths)
    {
        _settingsPath = Path.Combine(storagePaths.AppDataDirectory, "desktop-channel-settings.json");
    }

    public DesktopChannelSettings Load()
    {
        lock (_syncRoot)
        {
            return LoadCore();
        }
    }

    public void Save(DesktopChannelSettings settings)
    {
        lock (_syncRoot)
        {
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            WriteSettingsJson(json);
        }
    }

    private DesktopChannelSettings LoadCore()
    {
        try
        {
            var shouldWrite = false;
            string? originalJson = null;
            DesktopChannelSettings settings;
            if (!File.Exists(_settingsPath))
            {
                settings = DesktopChannelSettings.Default;
                shouldWrite = true;
            }
            else
            {
                originalJson = File.ReadAllText(_settingsPath);
                settings = JsonSerializer.Deserialize<DesktopChannelSettings>(originalJson, JsonOptions)
                           ?? DesktopChannelSettings.Default;
            }

            var normalized = Normalize(settings);
            var normalizedJson = JsonSerializer.Serialize(normalized, JsonOptions);
            if (shouldWrite || !string.Equals(originalJson, normalizedJson, StringComparison.Ordinal))
            {
                WriteSettingsJson(normalizedJson);
            }

            return normalized;
        }
        catch
        {
            return DesktopChannelSettings.Default;
        }
    }

    private static DesktopChannelSettings Normalize(DesktopChannelSettings? settings)
    {
        if (settings is null)
        {
            return DesktopChannelSettings.Default;
        }

        var items = new Dictionary<string, DesktopChannelConfiguration>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channelId, configuration) in settings.Items ?? new Dictionary<string, DesktopChannelConfiguration>())
        {
            var normalizedId = channelId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                continue;
            }

            items[normalizedId] = NormalizeChannelConfiguration(configuration);
        }

        return settings with
        {
            Items = items
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

    private void WriteSettingsJson(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, json, Utf8WithoutBom);
    }
}
