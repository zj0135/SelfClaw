using System.Net;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Infrastructure.Extensions;

internal sealed class ExtensionSettingsService : IExtensionSettingsService
{
    private readonly IExtensionPackageRepository _packageRepository;
    private readonly IMcpServerRepository _mcpServerRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly ExtensionCatalog _catalog;
    private readonly ExtensionPackageInstaller _packageInstaller;
    private readonly McpConfigurationResolver _mcpConfigurationResolver;
    private readonly IMcpClientManager _mcpClientManager;
    private readonly PluginContributionService _pluginContributionService;
    private readonly IExtensionStateChangeNotifier _stateChangeNotifier;
    private readonly IPluginVersionLeaseManager? _pluginVersionLeaseManager;

    public ExtensionSettingsService(
        IExtensionPackageRepository packageRepository,
        IMcpServerRepository mcpServerRepository,
        ISecretProtector secretProtector,
        ExtensionCatalog catalog,
        ExtensionPackageInstaller packageInstaller,
        McpConfigurationResolver mcpConfigurationResolver,
        IMcpClientManager mcpClientManager,
        PluginContributionService pluginContributionService,
        IExtensionStateChangeNotifier stateChangeNotifier,
        IPluginVersionLeaseManager? pluginVersionLeaseManager = null)
    {
        _packageRepository = packageRepository;
        _mcpServerRepository = mcpServerRepository;
        _secretProtector = secretProtector;
        _catalog = catalog;
        _packageInstaller = packageInstaller;
        _mcpConfigurationResolver = mcpConfigurationResolver;
        _mcpClientManager = mcpClientManager;
        _pluginContributionService = pluginContributionService;
        _stateChangeNotifier = stateChangeNotifier;
        _pluginVersionLeaseManager = pluginVersionLeaseManager;
    }

    public async Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var pluginsTask = _catalog.ListPackageViewsAsync(ExtensionKind.Plugin, cancellationToken);
        var skillsTask = _catalog.ListPackageViewsAsync(ExtensionKind.Skill, cancellationToken);
        var mcpServersTask = _catalog.ListMcpServerViewsAsync(cancellationToken);
        await Task.WhenAll(pluginsTask, skillsTask, mcpServersTask).ConfigureAwait(false);
        return new ExtensionSettingsState(
            _stateChangeNotifier.CurrentRevision,
            null,
            [],
            await pluginsTask.ConfigureAwait(false),
            await skillsTask.ConfigureAwait(false),
            await mcpServersTask.ConfigureAwait(false));
    }

    public async Task<ExtensionPackageView> ImportPackageAsync(
        ExtensionKind kind,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        var result = await _packageInstaller.InstallAsync(kind, selectedPath, cancellationToken)
            .ConfigureAwait(false);
        if (kind == ExtensionKind.Plugin)
        {
            await _pluginContributionService.SynchronizeMcpServersAsync(result.Package, cancellationToken)
                .ConfigureAwait(false);
        }

        _stateChangeNotifier.Advance();
        return ExtensionCatalog.CreatePackageView(result.Package);
    }

    public async Task SetEnabledAsync(
        ExtensionItemKey key,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Id);
        switch (key.Kind)
        {
            case ExtensionKind.Plugin:
            {
                var plugin = await GetRequiredPackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                if (enabled)
                {
                    _pluginContributionService.EnsurePermissionsAcknowledged(plugin);
                }

                await _packageRepository.SetPackageEnabledAsync(key.Kind, key.Id, enabled, cancellationToken)
                    .ConfigureAwait(false);
                await _pluginContributionService.SetMcpServersEnabledAsync(key.Id, enabled, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            case ExtensionKind.Skill:
                _ = await GetRequiredPackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                await _packageRepository.SetPackageEnabledAsync(key.Kind, key.Id, enabled, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ExtensionKind.McpServer:
            {
                var server = await GetRequiredMcpServerAsync(key.Id, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(server.SourcePluginId))
                {
                    throw new InvalidOperationException(
                        $"MCP server '{key.Id}' is managed by Plugin '{server.SourcePluginId}'.");
                }

                await _mcpServerRepository.SetMcpServerEnabledAsync(key.Id, enabled, cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unsupported extension kind.");
        }

        _stateChangeNotifier.Advance();
    }

    public async Task AcknowledgePluginPermissionsAsync(
        string id,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(permissions);
        var plugin = await GetRequiredPackageAsync(ExtensionKind.Plugin, id, cancellationToken)
            .ConfigureAwait(false);
        var currentPermissions = _pluginContributionService.ReadPermissions(plugin.ManifestJson);
        if (!permissions.OrderBy(item => item, StringComparer.Ordinal).SequenceEqual(currentPermissions))
        {
            throw new InvalidOperationException(
                "Plugin permissions changed before confirmation. Review the current permissions and try again.");
        }

        var updated = plugin with
        {
            AcknowledgedPermissionsJson = JsonSerializer.Serialize(currentPermissions),
            AcknowledgedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _ = await _packageRepository.UpsertPackageAsync(updated, cancellationToken).ConfigureAwait(false);
        _stateChangeNotifier.Advance();
    }

    public async Task DeleteAsync(
        ExtensionItemKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Id);
        switch (key.Kind)
        {
            case ExtensionKind.Plugin:
            {
                var plugin = await GetRequiredPackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                var versionPaths = _pluginContributionService.ListVersionDirectories(plugin);
                await _packageRepository.SetPackageEnabledAsync(key.Kind, key.Id, false, cancellationToken)
                    .ConfigureAwait(false);
                await _pluginContributionService.DeleteMcpServersAsync(key.Id, cancellationToken).ConfigureAwait(false);
                await using var versionDrain = _pluginVersionLeaseManager is null
                    ? null
                    : await _pluginVersionLeaseManager.AcquireDrainsAsync(versionPaths, cancellationToken)
                        .ConfigureAwait(false);
                await _packageRepository.DeletePackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                _pluginContributionService.DeletePluginDirectory(plugin);
                break;
            }
            case ExtensionKind.Skill:
                _ = await GetRequiredPackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                await _packageRepository.DeletePackageAsync(key.Kind, key.Id, cancellationToken).ConfigureAwait(false);
                break;
            case ExtensionKind.McpServer:
            {
                var server = await GetRequiredMcpServerAsync(key.Id, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(server.SourcePluginId))
                {
                    throw new InvalidOperationException(
                        $"MCP server '{key.Id}' is managed by Plugin '{server.SourcePluginId}'.");
                }

                await _mcpClientManager.DrainAsync(key.Id, cancellationToken).ConfigureAwait(false);
                foreach (var secretRef in server.CredentialRefs.Values.Distinct(StringComparer.Ordinal))
                {
                    await _secretProtector.DeleteSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
                }

                await _mcpServerRepository.DeleteMcpServerAsync(key.Id, cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unsupported extension kind.");
        }

        _stateChangeNotifier.Advance();
    }

    public async Task<McpServerView> SaveMcpServerAsync(
        SaveMcpServerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var isNew = string.IsNullOrWhiteSpace(command.Id);
        if (isNew)
        {
            ValidateCommand(command);
        }

        var id = isNew ? CreateId(command.DisplayName) : NormalizeId(command.Id!);
        var existing = await _mcpServerRepository.GetMcpServerAsync(id, cancellationToken).ConfigureAwait(false);
        if (isNew && existing is not null)
        {
            throw new ArgumentException($"An MCP server with id '{id}' already exists.", nameof(command));
        }

        if (!isNew && existing is null)
        {
            throw new KeyNotFoundException($"MCP server '{id}' was not found.");
        }

        if (id.Contains('/') && existing?.SourcePluginId is null)
        {
            throw new ArgumentException("Namespaced MCP server ids are reserved for Plugin contributions.", nameof(command));
        }

        McpServerSettings? existingSettings = null;
        if (existing is not null)
        {
            existingSettings = ExtensionCatalog.DeserializeSettings(existing.SettingsJson);
            if (existing.SourcePluginId is not null)
            {
                ValidateManagedCommand(command, existing, existingSettings);
            }
            else
            {
                ValidateCommand(command);
            }
        }
        var nextCredentialRefs = existing?.CredentialRefs.ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var refsToDelete = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var secretFields = new HashSet<string>(StringComparer.Ordinal);

        await ApplyEntriesAsync(
            command.Environment,
            "environment",
            nextCredentialRefs,
            environment,
            secretFields,
            seenPaths,
            refsToDelete,
            cancellationToken).ConfigureAwait(false);
        await ApplyEntriesAsync(
            command.Headers,
            "headers",
            nextCredentialRefs,
            headers,
            secretFields,
            seenPaths,
            refsToDelete,
            cancellationToken).ConfigureAwait(false);
        foreach (var staleCredential in nextCredentialRefs.Where(pair => !seenPaths.Contains(pair.Key)).ToArray())
        {
            nextCredentialRefs.Remove(staleCredential.Key);
            refsToDelete.Add(staleCredential.Value);
        }

        var settings = existing?.SourcePluginId is not null
            ? CreateManagedSettings(existingSettings!, environment, headers, secretFields)
            : CreateSettings(command, environment, headers, secretFields);
        var now = DateTimeOffset.UtcNow;
        var record = new McpServerConfigRecord(
            id,
            existing?.SourcePluginId is null ? command.DisplayName.Trim() : existing.DisplayName,
            existing?.SourcePluginId is null ? command.Transport : existing.Transport,
            ExtensionCatalog.SerializeSettings(settings),
            nextCredentialRefs,
            existing?.SourcePluginId,
            existing?.SourcePluginId is null ? command.Enabled ?? existing?.IsEnabled ?? false : existing.IsEnabled,
            existing?.ConfigRevision ?? 1,
            existing?.DiscoveredTools ?? [],
            existing?.LastStatus ?? McpServerHealthStatus.Unknown,
            existing?.LastError,
            existing?.LastCheckedAtUtc,
            existing?.CreatedAtUtc ?? now,
            now);
        var stored = await _mcpServerRepository.UpsertMcpServerAsync(record, cancellationToken).ConfigureAwait(false);
        foreach (var secretRef in refsToDelete.Where(secretRef => !nextCredentialRefs.Values.Contains(secretRef, StringComparer.Ordinal)))
        {
            await _secretProtector.DeleteSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
        }

        _stateChangeNotifier.Advance();
        return _catalog.CreateMcpServerView(stored);
    }

    public async Task<McpHealthResult> TestMcpServerAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var server = await GetRequiredMcpServerAsync(id, cancellationToken).ConfigureAwait(false);
        string? pluginRoot = null;
        if (!string.IsNullOrWhiteSpace(server.SourcePluginId))
        {
            pluginRoot = (await _packageRepository.GetPackageAsync(
                    ExtensionKind.Plugin,
                    server.SourcePluginId,
                    cancellationToken)
                .ConfigureAwait(false))?.InstallPath;
        }

        var configuration = await _mcpConfigurationResolver.ResolveAsync(
                server,
                workspacePath: null,
                pluginRoot,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _mcpClientManager.TestAsync(configuration, cancellationToken).ConfigureAwait(false);
        var updated = server with
        {
            DiscoveredTools = result.Tools,
            LastStatus = result.Status,
            LastError = result.Error,
            LastCheckedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        _ = await _mcpServerRepository.UpsertMcpServerAsync(updated, cancellationToken).ConfigureAwait(false);
        _stateChangeNotifier.Advance();
        return result;
    }

    private async Task ApplyEntriesAsync(
        IReadOnlyList<McpKeyValueCommand> entries,
        string prefix,
        IDictionary<string, string> credentialRefs,
        IDictionary<string, string> plainValues,
        ISet<string> secretFields,
        ISet<string> seenPaths,
        ISet<string> refsToDelete,
        CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            var key = entry.Key.Trim();
            var path = $"{prefix}.{key}";
            if (!seenPaths.Add(path))
            {
                throw new ArgumentException($"Duplicate MCP setting key '{key}'.", nameof(entries));
            }

            credentialRefs.TryGetValue(path, out var existingSecretRef);
            if (!entry.IsSecret)
            {
                if (!string.IsNullOrWhiteSpace(existingSecretRef))
                {
                    refsToDelete.Add(existingSecretRef);
                    credentialRefs.Remove(path);
                }

                plainValues[key] = entry.Value ?? string.Empty;
                continue;
            }

            secretFields.Add(path);
            if (entry.ClearSecret)
            {
                if (!string.IsNullOrWhiteSpace(existingSecretRef))
                {
                    refsToDelete.Add(existingSecretRef);
                    credentialRefs.Remove(path);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(entry.Value))
            {
                credentialRefs[path] = await _secretProtector.StoreSecretAsync(
                    entry.Value,
                    existingSecretRef,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ExtensionPackageRecord> GetRequiredPackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken)
        => await _packageRepository.GetPackageAsync(kind, id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"{kind} extension '{id}' was not found.");

    private async Task<McpServerConfigRecord> GetRequiredMcpServerAsync(
        string id,
        CancellationToken cancellationToken)
        => await _mcpServerRepository.GetMcpServerAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"MCP server '{id}' was not found.");

    private static void ValidateCommand(SaveMcpServerCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);
        ArgumentNullException.ThrowIfNull(command.Arguments);
        ArgumentNullException.ThrowIfNull(command.Environment);
        ArgumentNullException.ThrowIfNull(command.Headers);
        foreach (var entry in command.Environment)
        {
            if (!IsValidEnvironmentKey(entry.Key))
            {
                throw new ArgumentException($"Environment key '{entry.Key}' is invalid.", nameof(command));
            }
        }

        foreach (var entry in command.Headers)
        {
            if (!IsValidHeaderName(entry.Key))
            {
                throw new ArgumentException($"HTTP header name '{entry.Key}' is invalid.", nameof(command));
            }
        }

        switch (command.Transport)
        {
            case McpTransportKind.Stdio:
                ArgumentException.ThrowIfNullOrWhiteSpace(command.Command);
                if (command.WorkingDirectoryMode is not ("workspace" or "plugin" or "appData"))
                {
                    throw new ArgumentException("Stdio working directory mode is invalid.", nameof(command));
                }

                break;
            case McpTransportKind.Http:
                ValidateHttpEndpoint(command.Endpoint);
                if (command.TransportMode is not ("auto" or "streamableHttp" or "sse"))
                {
                    throw new ArgumentException("HTTP transport mode is invalid.", nameof(command));
                }

                if (command.ConnectionTimeoutSeconds is null or <= 0 or > 300)
                {
                    throw new ArgumentException("HTTP connection timeout must be between 1 and 300 seconds.", nameof(command));
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(command), command.Transport, "Unsupported MCP transport.");
        }
    }

    private static void ValidateManagedCommand(
        SaveMcpServerCommand command,
        McpServerConfigRecord existing,
        McpServerSettings settings)
    {
        if (!string.Equals(command.DisplayName.Trim(), existing.DisplayName, StringComparison.Ordinal) ||
            command.Transport != existing.Transport ||
            command.Enabled is not null && command.Enabled != existing.IsEnabled)
        {
            throw new InvalidOperationException("Plugin-managed MCP structure cannot be changed independently.");
        }

        var structureMatches = existing.Transport switch
        {
            McpTransportKind.Stdio =>
                string.Equals(command.Command, settings.Command, StringComparison.Ordinal) &&
                command.Arguments.SequenceEqual(settings.Arguments, StringComparer.Ordinal) &&
                string.Equals(command.WorkingDirectoryMode, settings.WorkingDirectoryMode, StringComparison.Ordinal) &&
                command.RequiresWorkspace == settings.RequiresWorkspace,
            McpTransportKind.Http =>
                string.Equals(command.Endpoint, settings.Endpoint, StringComparison.Ordinal) &&
                string.Equals(command.TransportMode, settings.TransportMode, StringComparison.Ordinal) &&
                command.ConnectionTimeoutSeconds == settings.ConnectionTimeoutSeconds,
            _ => false
        };
        if (!structureMatches)
        {
            throw new InvalidOperationException("Plugin-managed MCP structure cannot be changed independently.");
        }

        ValidateEntries(command.Environment, "environment", settings);
        ValidateEntries(command.Headers, "headers", settings);
        var expectedPaths = settings.RequiredFieldNames?.OrderBy(path => path, StringComparer.Ordinal).ToArray() ?? [];
        var actualPaths = command.Environment.Select(entry => $"environment.{entry.Key.Trim()}")
            .Concat(command.Headers.Select(entry => $"headers.{entry.Key.Trim()}"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!actualPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Plugin-managed MCP accepts only its declared required settings.");
        }
    }

    private static void ValidateEntries(
        IReadOnlyList<McpKeyValueCommand> entries,
        string prefix,
        McpServerSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var key = entry.Key.Trim();
            var validKey = prefix == "environment" ? IsValidEnvironmentKey(key) : IsValidHeaderName(key);
            var path = $"{prefix}.{key}";
            if (!validKey || !seen.Add(key) ||
                entry.IsSecret != settings.SecretFieldNames.Contains(path, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Plugin-managed MCP required settings do not match its manifest.");
            }
        }
    }

    private static void ValidateHttpEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("MCP endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("MCP endpoint must not contain embedded credentials.", nameof(endpoint));
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (!uri.IsLoopback &&
            (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address)))
        {
            throw new ArgumentException("Remote MCP endpoints must use HTTPS.", nameof(endpoint));
        }
    }

    private static McpServerSettings CreateSettings(
        SaveMcpServerCommand command,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlySet<string> secretFields)
        => command.Transport == McpTransportKind.Stdio
            ? new McpServerSettings(
                command.Command!.Trim(),
                command.Arguments.ToArray(),
                command.WorkingDirectoryMode,
                command.RequiresWorkspace,
                environment,
                null,
                null,
                null,
                new Dictionary<string, string>(),
                secretFields.OrderBy(item => item, StringComparer.Ordinal).ToArray())
            : new McpServerSettings(
                null,
                [],
                null,
                false,
                new Dictionary<string, string>(),
                command.Endpoint!.Trim(),
                command.TransportMode,
                command.ConnectionTimeoutSeconds,
                headers,
                secretFields.OrderBy(item => item, StringComparer.Ordinal).ToArray());

    private static McpServerSettings CreateManagedSettings(
        McpServerSettings existing,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyDictionary<string, string> headers,
        IReadOnlySet<string> secretFields)
        => existing with
        {
            Environment = environment,
            Headers = headers,
            SecretFieldNames = secretFields.OrderBy(item => item, StringComparer.Ordinal).ToArray()
        };

    private static string CreateId(string displayName)
    {
        var id = new string(displayName.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"mcp-{Guid.NewGuid():N}";
        }

        return id;
    }

    private static string NormalizeId(string id)
    {
        var normalized = id.Trim().ToLowerInvariant();
        var segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            segments.Length > 2 ||
            segments.Any(segment => string.IsNullOrWhiteSpace(segment) ||
                                    segment.Any(character =>
                                        !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            throw new ArgumentException("MCP server id is invalid.", nameof(id));
        }

        return normalized;
    }

    private static bool IsValidEnvironmentKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
        {
            return false;
        }

        return key.Skip(1).All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static bool IsValidHeaderName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');
}
