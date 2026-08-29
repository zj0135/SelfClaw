using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;

namespace SelfClaw.Infrastructure.Extensions.Plugins;

internal sealed class PluginContributionService
{
    private readonly IMcpServerRepository _mcpServerRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly IMcpClientManager _mcpClientManager;
    private readonly PluginManifestReader _pluginManifestReader;

    public PluginContributionService(
        IMcpServerRepository mcpServerRepository,
        ISecretProtector secretProtector,
        IMcpClientManager mcpClientManager,
        PluginManifestReader pluginManifestReader)
    {
        _mcpServerRepository = mcpServerRepository;
        _secretProtector = secretProtector;
        _mcpClientManager = mcpClientManager;
        _pluginManifestReader = pluginManifestReader;
    }

    public async Task SynchronizeMcpServersAsync(
        ExtensionPackageRecord plugin,
        CancellationToken cancellationToken)
    {
        var manifest = await _pluginManifestReader.ReadAsync(
                Path.Combine(plugin.InstallPath, "plugin.json"),
                cancellationToken)
            .ConfigureAwait(false);
        var existingServers = (await _mcpServerRepository.ListMcpServersAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(server => string.Equals(server.SourcePluginId, plugin.Id, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(server => server.Id, StringComparer.OrdinalIgnoreCase);
        var expectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var permissionsAcknowledged = ArePermissionsAcknowledged(plugin);
        foreach (var contribution in manifest.Contributions.McpServers)
        {
            var id = $"{plugin.Id}/{contribution.Id}";
            expectedIds.Add(id);
            existingServers.TryGetValue(id, out var existing);
            var requiredFields = contribution.RequiredSettings
                .Select(setting => McpSettingPath.ForManifestTarget(setting.Target, setting.Key))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var secretFields = contribution.RequiredSettings
                .Where(setting => setting.Secret)
                .Select(setting => McpSettingPath.ForManifestTarget(setting.Target, setting.Key))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var previousSettings = TryDeserializeSettings(existing?.SettingsJson);
            var environment = McpSettingPath.CreateEnvironment(contribution.RequiredSettings
                .Where(setting => setting.Target == "env" && !setting.Secret)
                .Select(setting => KeyValuePair.Create(
                    setting.Key,
                    previousSettings?.Environment.GetValueOrDefault(setting.Key) ?? string.Empty)));
            var headers = McpSettingPath.CreateHeaders(contribution.RequiredSettings
                .Where(setting => setting.Target == "header" && !setting.Secret)
                .Select(setting => KeyValuePair.Create(
                    setting.Key,
                    previousSettings?.Headers.GetValueOrDefault(setting.Key) ?? string.Empty)));
            var transport = contribution.Transport == "stdio"
                ? McpTransportKind.Stdio
                : McpTransportKind.Http;
            var credentialRefs = FilterCredentialRefs(existing?.CredentialRefs, secretFields);
            var settings = transport == McpTransportKind.Stdio
                ? new McpServerSettings(
                    contribution.Command,
                    contribution.Arguments,
                    "plugin",
                    contribution.RequiresWorkspace,
                    environment,
                    null,
                    null,
                    null,
                    McpSettingPath.CreateHeaders(),
                    secretFields,
                    requiredFields)
                : new McpServerSettings(
                    null,
                    [],
                    null,
                    contribution.RequiresWorkspace,
                    McpSettingPath.CreateEnvironment(),
                    contribution.Endpoint,
                    contribution.TransportMode ?? "auto",
                    contribution.ConnectionTimeoutSeconds ?? 30,
                    headers,
                    secretFields,
                    requiredFields);
            var now = DateTimeOffset.UtcNow;
            var record = new McpServerConfigRecord(
                id,
                contribution.Name,
                transport,
                ExtensionCatalog.SerializeSettings(settings),
                credentialRefs,
                plugin.Id,
                plugin.IsEnabled && permissionsAcknowledged,
                existing?.ConfigRevision ?? 1,
                existing?.DiscoveredTools ?? [],
                existing?.LastStatus ?? McpServerHealthStatus.Unknown,
                existing?.LastError,
                existing?.LastCheckedAtUtc,
                existing?.CreatedAtUtc ?? now,
                now);
            _ = await _mcpServerRepository.UpsertMcpServerAsync(record, cancellationToken).ConfigureAwait(false);
            foreach (var staleSecretRef in (existing?.CredentialRefs.Values ?? [])
                         .Except(credentialRefs.Values, StringComparer.Ordinal))
            {
                await _secretProtector.DeleteSecretAsync(staleSecretRef, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var stale in existingServers.Values.Where(server => !expectedIds.Contains(server.Id)))
        {
            await DeleteMcpServerRecordAsync(stale, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetMcpServersEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var server in servers.Where(server =>
                     string.Equals(server.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            await _mcpServerRepository.SetMcpServerEnabledAsync(server.Id, enabled, cancellationToken)
                .ConfigureAwait(false);
            if (!enabled)
            {
                await _mcpClientManager.DrainAsync(server.Id, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task DeleteMcpServersAsync(string pluginId, CancellationToken cancellationToken)
    {
        var servers = await _mcpServerRepository.ListMcpServersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var server in servers.Where(server =>
                     string.Equals(server.SourcePluginId, pluginId, StringComparison.OrdinalIgnoreCase)))
        {
            await DeleteMcpServerRecordAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    public void EnsurePermissionsAcknowledged(ExtensionPackageRecord plugin)
    {
        var missing = ReadPermissions(plugin.ManifestJson)
            .Except(ExtensionCatalog.ReadAcknowledgedPermissions(plugin.AcknowledgedPermissionsJson), StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Plugin '{plugin.Id}' requires permission confirmation: {string.Join(", ", missing)}.");
        }
    }

    public IReadOnlyList<string> ReadPermissions(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (!document.RootElement.TryGetProperty("permissions", out var element) ||
                element.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var permissions = element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .ToArray();
            // Same validator the manifest reader uses. If the two normalized differently, an updated
            // Plugin could never satisfy "acknowledged ⊇ declared" and would be stuck unenablable.
            return PluginPermissions.Validate(permissions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Plugin manifest is invalid.", exception);
        }
    }

    public void DeletePluginDirectory(ExtensionPackageRecord plugin)
    {
        var pluginDirectory = GetPluginDirectory(plugin);

        if (pluginDirectory.Exists)
        {
            pluginDirectory.Delete(recursive: true);
        }
    }

    public IReadOnlyList<string> ListVersionDirectories(ExtensionPackageRecord plugin)
    {
        var pluginDirectory = GetPluginDirectory(plugin);
        var versionsDirectory = Path.Combine(pluginDirectory.FullName, "versions");
        return Directory.Exists(versionsDirectory)
            ? Directory.EnumerateDirectories(versionsDirectory).Select(Path.GetFullPath).ToArray()
            : [Path.GetFullPath(plugin.InstallPath)];
    }

    private async Task DeleteMcpServerRecordAsync(
        McpServerConfigRecord server,
        CancellationToken cancellationToken)
    {
        await _mcpClientManager.DrainAsync(server.Id, cancellationToken).ConfigureAwait(false);
        await _mcpServerRepository.DeleteMcpServerAsync(server.Id, cancellationToken).ConfigureAwait(false);
        foreach (var secretRef in server.CredentialRefs.Values.Distinct(StringComparer.Ordinal))
        {
            await _secretProtector.DeleteSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ArePermissionsAcknowledged(ExtensionPackageRecord plugin)
    {
        var acknowledged = ExtensionCatalog.ReadAcknowledgedPermissions(plugin.AcknowledgedPermissionsJson);
        return ReadPermissions(plugin.ManifestJson)
            .All(permission => acknowledged.Contains(permission, StringComparer.Ordinal));
    }

    private static McpServerSettings? TryDeserializeSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            return ExtensionCatalog.DeserializeSettings(settingsJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> FilterCredentialRefs(
        IReadOnlyDictionary<string, string>? credentialRefs,
        IReadOnlyList<string> secretFields)
        => credentialRefs is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : credentialRefs
                .Where(pair => secretFields.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static DirectoryInfo GetPluginDirectory(ExtensionPackageRecord plugin)
    {
        var versionDirectory = new DirectoryInfo(Path.GetFullPath(plugin.InstallPath));
        var versionsDirectory = versionDirectory.Parent;
        var pluginDirectory = versionsDirectory?.Parent;
        if (versionsDirectory is null || pluginDirectory is null ||
            !string.Equals(versionsDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pluginDirectory.Name, plugin.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Plugin '{plugin.Id}' install path is invalid.");
        }

        return pluginDirectory;
    }
}
