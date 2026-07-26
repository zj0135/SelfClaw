using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Infrastructure.Extensions;

internal sealed class ExtensionCatalog : IExtensionCatalogReconciler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IMcpServerRepository _mcpServerRepository;
    private readonly StoragePaths _storagePaths;
    private readonly PluginManifestReader? _pluginManifestReader;

    public ExtensionCatalog(
        IExtensionPackageRepository packageRepository,
        IMcpServerRepository mcpServerRepository,
        StoragePaths storagePaths,
        PluginManifestReader? pluginManifestReader = null)
    {
        _packageRepository = packageRepository;
        _mcpServerRepository = mcpServerRepository;
        _storagePaths = storagePaths;
        _pluginManifestReader = pluginManifestReader;
    }

    public async Task<IReadOnlyList<ExtensionPackageView>> ListPackageViewsAsync(
        ExtensionKind kind,
        CancellationToken cancellationToken = default)
    {
        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var views = packages
            .Where(package => package.Kind == kind)
            .Select(CreatePackageView)
            .ToList();
        if (kind == ExtensionKind.Plugin && _pluginManifestReader is not null)
        {
            for (var index = 0; index < views.Count; index++)
            {
                var package = packages.First(item =>
                    item.Kind == ExtensionKind.Plugin &&
                    string.Equals(item.Id, views[index].Id, StringComparison.OrdinalIgnoreCase));
                try
                {
                    _ = await _pluginManifestReader.ReadAsync(
                            Path.Combine(package.InstallPath, "plugin.json"),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    views[index] = views[index] with { Status = "broken" };
                }
            }
        }

        if (kind == ExtensionKind.Skill && _pluginManifestReader is not null)
        {
            views.AddRange(await CreatePluginSkillViewsAsync(packages, cancellationToken).ConfigureAwait(false));
        }

        return views
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<ExtensionPackageView>> CreatePluginSkillViewsAsync(
        IReadOnlyList<ExtensionPackageRecord> packages,
        CancellationToken cancellationToken)
    {
        var results = new List<ExtensionPackageView>();
        foreach (var plugin in packages
                     .Where(package => package.Kind == ExtensionKind.Plugin)
                     .OrderBy(package => package.Id, StringComparer.Ordinal))
        {
            try
            {
                var manifest = await _pluginManifestReader!.ReadAsync(
                        Path.Combine(plugin.InstallPath, "plugin.json"),
                        cancellationToken)
                    .ConfigureAwait(false);
                var pluginView = CreatePackageView(plugin);
                foreach (var skill in manifest.Contributions.Skills)
                {
                    results.Add(new ExtensionPackageView(
                        ExtensionKind.Skill,
                        $"{plugin.Id}/{skill.Id}",
                        skill.Id,
                        plugin.Version,
                        $"Managed by Plugin '{plugin.DisplayName}'.",
                        plugin.IsEnabled,
                        plugin.Id,
                        [],
                        pluginView.Status,
                        [],
                        []));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<McpServerView>> ListMcpServerViewsAsync(
        CancellationToken cancellationToken = default)
    {
        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        return servers
            .Select(CreateMcpServerView)
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var packages = await _packageRepository.ListPackagesAsync(cancellationToken).ConfigureAwait(false);
        var stagingRoot = Path.Combine(_storagePaths.AppDataDirectory, "staging", "extensions");
        if (Directory.Exists(stagingRoot))
        {
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(stagingRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteFileSystemEntry(entryPath);
            }
        }

        var referencedVersions = packages
            .Where(package => package.Kind == ExtensionKind.Plugin)
            .Select(package => Path.GetFullPath(package.InstallPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pluginsRoot = Path.Combine(_storagePaths.AppDataDirectory, "plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            return;
        }

        foreach (var pluginRoot in Directory.EnumerateDirectories(pluginsRoot))
        {
            var versionsRoot = Path.Combine(pluginRoot, "versions");
            if (!Directory.Exists(versionsRoot))
            {
                continue;
            }

            foreach (var versionPath in Directory.EnumerateFileSystemEntries(versionsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!referencedVersions.Contains(Path.GetFullPath(versionPath)))
                {
                    DeleteFileSystemEntry(versionPath);
                }
            }
        }
    }

    public McpServerView CreateMcpServerView(McpServerConfigRecord server)
    {
        McpServerSettings settings;
        string status;
        try
        {
            settings = DeserializeSettings(server.SettingsJson);
            status = server.IsEnabled
                ? IsConfigurationComplete(server.Transport, settings) ? "ready" : "needs-config"
                : "disabled";
        }
        catch (JsonException)
        {
            settings = EmptySettings();
            status = "broken";
        }

        return new McpServerView(
            server.Id,
            server.DisplayName,
            server.Transport == McpTransportKind.Http ? "http" : "stdio",
            server.IsEnabled,
            server.SourcePluginId,
            [],
            status,
            server.LastError,
            server.DiscoveredTools,
            settings.Command,
            settings.Arguments,
            settings.WorkingDirectoryMode,
            settings.RequiresWorkspace,
            CreateEntryViews("environment", settings.Environment, settings.SecretFieldNames, server.CredentialRefs),
            settings.Endpoint,
            settings.TransportMode,
            settings.ConnectionTimeoutSeconds,
            CreateEntryViews("headers", settings.Headers, settings.SecretFieldNames, server.CredentialRefs),
            server.LastCheckedAtUtc);
    }

    public static string SerializeSettings(McpServerSettings settings)
        => JsonSerializer.Serialize(settings, JsonOptions);

    public static McpServerSettings DeserializeSettings(string settingsJson)
        => JsonSerializer.Deserialize<McpServerSettings>(settingsJson, JsonOptions)
            ?? throw new JsonException("MCP settings are empty.");

    internal static ExtensionPackageView CreatePackageView(ExtensionPackageRecord package)
    {
        var permissions = package.Kind == ExtensionKind.Plugin
            ? ReadStringArray(package.ManifestJson, "permissions")
            : [];
        var acknowledged = ReadAcknowledgedPermissions(package.AcknowledgedPermissionsJson);
        var unacknowledged = permissions.Except(acknowledged, StringComparer.Ordinal).ToArray();
        var status = !Directory.Exists(package.InstallPath)
            ? "broken"
            : package.IsEnabled && unacknowledged.Length > 0
                ? "needs-permission"
                : package.IsEnabled ? "ready" : "disabled";
        return new ExtensionPackageView(
            package.Kind,
            package.Id,
            package.DisplayName,
            package.Version,
            package.Description,
            package.IsEnabled,
            package.SourcePluginId,
            [],
            status,
            permissions,
            unacknowledged);
    }

    internal static IReadOnlyList<string> ReadAcknowledgedPermissions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadStringArray(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var element) ||
                element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<McpConfigurationEntryView> CreateEntryViews(
        string prefix,
        IReadOnlyDictionary<string, string> plainValues,
        IReadOnlyList<string> secretFieldNames,
        IReadOnlyDictionary<string, string> credentialRefs)
    {
        var secretKeys = secretFieldNames
            .Where(path => path.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Select(path => path[(prefix.Length + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        return plainValues.Keys
            .Concat(secretKeys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => secretKeys.Contains(key)
                ? new McpConfigurationEntryView(
                    key,
                    null,
                    true,
                    credentialRefs.ContainsKey($"{prefix}.{key}"))
                : new McpConfigurationEntryView(key, plainValues[key], false, false))
            .ToArray();
    }

    private static bool IsConfigurationComplete(McpTransportKind transport, McpServerSettings settings)
        => transport switch
        {
            McpTransportKind.Stdio => !string.IsNullOrWhiteSpace(settings.Command),
            McpTransportKind.Http => Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out _),
            _ => false
        };

    private static McpServerSettings EmptySettings()
        => new(null, [], null, false, new Dictionary<string, string>(), null, null, null,
            new Dictionary<string, string>(), []);

    private static void DeleteFileSystemEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, !attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        else
        {
            File.Delete(path);
        }
    }
}
