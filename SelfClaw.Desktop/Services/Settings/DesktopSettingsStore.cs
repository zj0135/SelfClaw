using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services;

public sealed class DesktopSettingsStore
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
            var json = JsonSerializer.Serialize(Normalize(settings), JsonOptions);
            WriteSettingsJson(json);
        }
    }

    private DesktopSettings LoadCore()
    {
        try
        {
            var shouldWrite = false;
            string? originalJson = null;
            DesktopSettings settings;
            if (!File.Exists(_settingsPath))
            {
                settings = DesktopSettings.Default;
                shouldWrite = true;
            }
            else
            {
                originalJson = File.ReadAllText(_settingsPath);
                settings = JsonSerializer.Deserialize<DesktopSettings>(originalJson, JsonOptions) ?? DesktopSettings.Default;
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
        var mcpServers = new Dictionary<string, DesktopMcpServerConfiguration>(StringComparer.OrdinalIgnoreCase);
        var disabledSkills = (settings.DisabledSkills ?? [])
            .Select(NormalizeSkillId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

        var contextWindow = settings.ModelContextWindow > 0
            ? settings.ModelContextWindow
            : DesktopSettings.DefaultModelContextWindow;
        var autoCompactTokenLimit = settings.ModelAutoCompactTokenLimit >= 0
            ? settings.ModelAutoCompactTokenLimit
            : DesktopSettings.DefaultModelAutoCompactTokenLimit;
        if (autoCompactTokenLimit > contextWindow)
        {
            autoCompactTokenLimit = contextWindow;
        }

        foreach (var (serverId, configuration) in settings.McpServers ?? new Dictionary<string, DesktopMcpServerConfiguration>())
        {
            var normalizedId = serverId?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                continue;
            }

            mcpServers[normalizedId] = NormalizeMcpServerConfiguration(configuration);
        }

        return settings with
        {
            ModelContextWindow = contextWindow,
            ModelAutoCompactTokenLimit = autoCompactTokenLimit,
            Channels = new DesktopChannelSettings
            {
                Items = items
            },
            McpServers = mcpServers,
            DisabledSkills = disabledSkills
        };
    }

    private static string NormalizeSkillId(string? skillId)
    {
        var normalized = (skillId ?? string.Empty).Replace('\\', '/').Trim('/');
        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item is not "." and not "..")
            .ToArray();

        return string.Join("/", segments);
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

    private static DesktopMcpServerConfiguration NormalizeMcpServerConfiguration(DesktopMcpServerConfiguration? configuration)
    {
        if (configuration is null)
        {
            return DesktopMcpServerConfiguration.Default;
        }

        var args = (configuration.Args ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        var env = (configuration.Env ?? new Dictionary<string, string>())
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(
                item => item.Key.Trim(),
                item => item.Value?.Trim() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        return configuration with
        {
            DisplayName = configuration.DisplayName?.Trim() ?? string.Empty,
            Command = configuration.Command?.Trim() ?? string.Empty,
            Args = args,
            Env = env
        };
    }

    private void WriteSettingsJson(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, json, Utf8WithoutBom);
    }
}
