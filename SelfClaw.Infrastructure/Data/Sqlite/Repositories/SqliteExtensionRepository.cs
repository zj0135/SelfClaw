using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteExtensionRepository : IExtensionPackageRepository, IMcpServerRepository
{
    private const string PackageSelect = @"
SELECT kind, id, display_name, version, description, install_path, content_hash, manifest_json,
       source_plugin_id, is_enabled, acknowledged_permissions_json, acknowledged_at_utc,
       installed_at_utc, updated_at_utc
FROM extension_packages";

    private const string McpSelect = @"
SELECT id, display_name, transport, settings_json, credential_refs_json, source_plugin_id, is_enabled,
       config_revision, discovered_tools_json, last_status, last_error, last_checked_at_utc,
       created_at_utc, updated_at_utc
FROM mcp_server_configs";

    private readonly SqliteDatabase _database;

    public SqliteExtensionRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PackageSelect + " ORDER BY updated_at_utc DESC;";
        var results = new List<ExtensionPackageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteMappings.ReadExtensionPackage(reader));
        }

        return results;
    }

    public async Task<ExtensionPackageRecord?> GetPackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PackageSelect + " WHERE kind = $kind AND id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteMappings.ReadExtensionPackage(reader)
            : null;
    }

    public async Task<ExtensionPackageRecord> UpsertPackageAsync(
        ExtensionPackageRecord package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO extension_packages(
    kind, id, display_name, version, description, install_path, content_hash, manifest_json,
    source_plugin_id, is_enabled, acknowledged_permissions_json, acknowledged_at_utc,
    installed_at_utc, updated_at_utc)
VALUES(
    $kind, $id, $displayName, $version, $description, $installPath, $contentHash, $manifestJson,
    $sourcePluginId, $isEnabled, $acknowledgedPermissions, $acknowledgedAt, $installedAt, $updatedAt)
ON CONFLICT(kind, id) DO UPDATE SET
    display_name = excluded.display_name,
    version = excluded.version,
    description = excluded.description,
    install_path = excluded.install_path,
    content_hash = excluded.content_hash,
    manifest_json = excluded.manifest_json,
    source_plugin_id = excluded.source_plugin_id,
    is_enabled = excluded.is_enabled,
    acknowledged_permissions_json = excluded.acknowledged_permissions_json,
    acknowledged_at_utc = excluded.acknowledged_at_utc,
    installed_at_utc = excluded.installed_at_utc,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$kind", (int)package.Kind);
        command.Parameters.AddWithValue("$id", package.Id);
        command.Parameters.AddWithValue("$displayName", package.DisplayName);
        command.Parameters.AddWithValue("$version", package.Version);
        command.Parameters.AddWithValue("$description", package.Description);
        command.Parameters.AddWithValue("$installPath", package.InstallPath);
        command.Parameters.AddWithValue("$contentHash", package.ContentHash);
        command.Parameters.AddWithValue("$manifestJson", package.ManifestJson);
        command.Parameters.AddWithValue("$sourcePluginId", package.SourcePluginId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$isEnabled", package.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$acknowledgedPermissions", package.AcknowledgedPermissionsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$acknowledgedAt", package.AcknowledgedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$installedAt", package.InstalledAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", package.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return package;
    }

    public async Task SetPackageEnabledAsync(
        ExtensionKind kind,
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE extension_packages SET is_enabled = $enabled, updated_at_utc = $updatedAt WHERE kind = $kind AND id = $id;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePackageAsync(
        ExtensionKind kind,
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM extension_packages WHERE kind = $kind AND id = $id;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpServerConfigRecord>> ListMcpServersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = McpSelect + " ORDER BY updated_at_utc DESC;";
        var results = new List<McpServerConfigRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteMappings.ReadMcpServerConfig(reader));
        }

        return results;
    }

    public async Task<McpServerConfigRecord?> GetMcpServerAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = McpSelect + " WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteMappings.ReadMcpServerConfig(reader)
            : null;
    }

    public async Task<McpServerConfigRecord> UpsertMcpServerAsync(
        McpServerConfigRecord server,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetMcpServerAsync(connection, server.Id, cancellationToken).ConfigureAwait(false);
        var revision = existing is null
            ? 1
            : HasConfigurationChanged(existing, server) ? existing.ConfigRevision + 1 : existing.ConfigRevision;
        var stored = server with
        {
            ConfigRevision = revision,
            CreatedAtUtc = existing?.CreatedAtUtc ?? server.CreatedAtUtc
        };

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO mcp_server_configs(
    id, display_name, transport, settings_json, credential_refs_json, source_plugin_id, is_enabled,
    config_revision, discovered_tools_json, last_status, last_error, last_checked_at_utc,
    created_at_utc, updated_at_utc)
VALUES(
    $id, $displayName, $transport, $settingsJson, $credentialRefsJson, $sourcePluginId, $isEnabled,
    $revision, $discoveredToolsJson, $lastStatus, $lastError, $lastCheckedAt, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    display_name = excluded.display_name,
    transport = excluded.transport,
    settings_json = excluded.settings_json,
    credential_refs_json = excluded.credential_refs_json,
    source_plugin_id = excluded.source_plugin_id,
    is_enabled = excluded.is_enabled,
    config_revision = excluded.config_revision,
    discovered_tools_json = excluded.discovered_tools_json,
    last_status = excluded.last_status,
    last_error = excluded.last_error,
    last_checked_at_utc = excluded.last_checked_at_utc,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", stored.Id);
        command.Parameters.AddWithValue("$displayName", stored.DisplayName);
        command.Parameters.AddWithValue("$transport", (int)stored.Transport);
        command.Parameters.AddWithValue("$settingsJson", stored.SettingsJson);
        command.Parameters.AddWithValue("$credentialRefsJson", JsonSerializer.Serialize(stored.CredentialRefs));
        command.Parameters.AddWithValue("$sourcePluginId", stored.SourcePluginId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$isEnabled", stored.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$revision", stored.ConfigRevision);
        command.Parameters.AddWithValue("$discoveredToolsJson", JsonSerializer.Serialize(stored.DiscoveredTools));
        command.Parameters.AddWithValue("$lastStatus", (int)stored.LastStatus);
        command.Parameters.AddWithValue("$lastError", stored.LastError ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastCheckedAt", stored.LastCheckedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", stored.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", stored.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task SetMcpServerEnabledAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mcp_server_configs SET is_enabled = $enabled, updated_at_utc = $updatedAt WHERE id = $id;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteMcpServerAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mcp_server_configs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool HasConfigurationChanged(McpServerConfigRecord existing, McpServerConfigRecord next)
        => existing.Transport != next.Transport
            || !string.Equals(existing.SettingsJson, next.SettingsJson, StringComparison.Ordinal)
            || !existing.CredentialRefs.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(
                next.CredentialRefs.OrderBy(item => item.Key, StringComparer.Ordinal));

    private static async Task<McpServerConfigRecord?> GetMcpServerAsync(
        SqliteConnection connection,
        string id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = McpSelect + " WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteMappings.ReadMcpServerConfig(reader)
            : null;
    }

}
