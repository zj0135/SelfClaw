using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteAiProviderRepository : IAiProviderRepository
{
    private readonly SqliteDatabase _database;

    public SqliteAiProviderRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<AiProviderConnection>> ListProviderConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, catalog_id, name, provider_kind, endpoint, auth_kind, credential_refs_json, connection_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_provider_connections
WHERE is_enabled != 0
ORDER BY updated_at_utc DESC;";

        var results = new List<AiProviderConnection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadAiProviderConnection(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<AiProviderConnection>> ListAllProviderConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, catalog_id, name, provider_kind, endpoint, auth_kind, credential_refs_json, connection_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_provider_connections
ORDER BY is_enabled DESC, updated_at_utc DESC;";

        var results = new List<AiProviderConnection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadAiProviderConnection(reader));
        }

        return results;
    }

    public async Task<AiProviderConnection?> GetProviderConnectionAsync(
        Guid providerConnectionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, catalog_id, name, provider_kind, endpoint, auth_kind, credential_refs_json, connection_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_provider_connections
WHERE id = $id
LIMIT 1;";
        command.Parameters.AddWithValue("$id", providerConnectionId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteMappings.ReadAiProviderConnection(reader)
            : null;
    }

    public async Task<AiProviderConnection> UpsertProviderConnectionAsync(
        AiProviderConnection providerConnection,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO ai_provider_connections(
    id, catalog_id, name, provider_kind, endpoint, auth_kind, credential_refs_json, connection_options_json,
    is_enabled, created_at_utc, updated_at_utc)
VALUES(
    $id, $catalogId, $name, $providerKind, $endpoint, $authKind, $credentialRefsJson, $connectionOptionsJson,
    $isEnabled, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    catalog_id = excluded.catalog_id,
    name = excluded.name,
    provider_kind = excluded.provider_kind,
    endpoint = excluded.endpoint,
    auth_kind = excluded.auth_kind,
    credential_refs_json = excluded.credential_refs_json,
    connection_options_json = excluded.connection_options_json,
    is_enabled = excluded.is_enabled,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", providerConnection.Id.ToString("D"));
        command.Parameters.AddWithValue("$catalogId", providerConnection.CatalogId);
        command.Parameters.AddWithValue("$name", providerConnection.Name);
        command.Parameters.AddWithValue("$providerKind", (int)providerConnection.ProviderKind);
        command.Parameters.AddWithValue("$endpoint", providerConnection.Endpoint.AbsoluteUri);
        command.Parameters.AddWithValue("$authKind", (int)providerConnection.AuthKind);
        command.Parameters.AddWithValue("$credentialRefsJson", Serialize(providerConnection.CredentialRefs));
        command.Parameters.AddWithValue("$connectionOptionsJson", Serialize(providerConnection.ConnectionOptions));
        command.Parameters.AddWithValue("$isEnabled", providerConnection.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", providerConnection.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", providerConnection.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return providerConnection;
    }

    public async Task SetProviderConnectionEnabledAsync(
        Guid providerConnectionId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE ai_provider_connections
SET is_enabled = $isEnabled,
    updated_at_utc = $updatedAt
WHERE id = $id;";
        command.Parameters.AddWithValue("$id", providerConnectionId.ToString("D"));
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteProviderConnectionAsync(Guid providerConnectionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_provider_connections WHERE id = $id;";
        command.Parameters.AddWithValue("$id", providerConnectionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiModelProfile>> ListModelProfilesAsync(
        Guid? providerConnectionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (providerConnectionId.HasValue)
        {
            command.CommandText = @"
SELECT id, provider_connection_id, name, api_format, model, temperature_enabled, temperature, top_p_enabled, top_p,
       model_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_model_profiles
WHERE provider_connection_id = $providerConnectionId
ORDER BY updated_at_utc DESC;";
            command.Parameters.AddWithValue("$providerConnectionId", providerConnectionId.Value.ToString("D"));
        }
        else
        {
            command.CommandText = @"
SELECT id, provider_connection_id, name, api_format, model, temperature_enabled, temperature, top_p_enabled, top_p,
       model_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_model_profiles
ORDER BY updated_at_utc DESC;";
        }

        var results = new List<AiModelProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadAiModelProfile(reader));
        }

        return results;
    }

    public async Task<AiModelProfile?> GetModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, provider_connection_id, name, api_format, model, temperature_enabled, temperature, top_p_enabled, top_p,
       model_options_json, created_at_utc, updated_at_utc, is_enabled
FROM ai_model_profiles
WHERE id = $id
LIMIT 1;";
        command.Parameters.AddWithValue("$id", modelProfileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteMappings.ReadAiModelProfile(reader)
            : null;
    }

    public async Task<AiModelProfile> UpsertModelProfileAsync(
        AiModelProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO ai_model_profiles(
    id, provider_connection_id, name, api_format, model, temperature_enabled, temperature, top_p_enabled, top_p,
    model_options_json, is_enabled, created_at_utc, updated_at_utc)
VALUES(
    $id, $providerConnectionId, $name, $apiFormat, $model, $temperatureEnabled, $temperature, $topPEnabled, $topP,
    $modelOptionsJson, $isEnabled, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    provider_connection_id = excluded.provider_connection_id,
    name = excluded.name,
    api_format = excluded.api_format,
    model = excluded.model,
    temperature_enabled = excluded.temperature_enabled,
    temperature = excluded.temperature,
    top_p_enabled = excluded.top_p_enabled,
    top_p = excluded.top_p,
    model_options_json = excluded.model_options_json,
    is_enabled = excluded.is_enabled,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$providerConnectionId", profile.ProviderConnectionId.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$apiFormat", (int)profile.ApiFormat);
        command.Parameters.AddWithValue("$model", profile.Model);
        command.Parameters.AddWithValue("$temperatureEnabled", profile.Sampling.TemperatureEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$temperature", profile.Sampling.Temperature);
        command.Parameters.AddWithValue("$topPEnabled", profile.Sampling.TopPEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$topP", profile.Sampling.TopP);
        command.Parameters.AddWithValue("$modelOptionsJson", Serialize(profile.ModelOptions));
        command.Parameters.AddWithValue("$isEnabled", profile.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", profile.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return profile;
    }

    public async Task SetModelProfileEnabledAsync(
        Guid modelProfileId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE ai_model_profiles
SET is_enabled = $isEnabled,
    updated_at_utc = $updatedAt
WHERE id = $id;";
        command.Parameters.AddWithValue("$id", modelProfileId.ToString("D"));
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetAllModelProfilesEnabledAsync(
        Guid providerConnectionId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE ai_model_profiles
SET is_enabled = $isEnabled,
    updated_at_utc = $updatedAt
WHERE provider_connection_id = $providerConnectionId;";
        command.Parameters.AddWithValue("$providerConnectionId", providerConnectionId.ToString("D"));
        command.Parameters.AddWithValue("$isEnabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AiModelProfile>> ListEnabledModelProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT model.id, model.provider_connection_id, model.name, model.api_format, model.model,
       model.temperature_enabled, model.temperature, model.top_p_enabled, model.top_p,
       model.model_options_json, model.created_at_utc, model.updated_at_utc, model.is_enabled
FROM ai_model_profiles AS model
INNER JOIN ai_provider_connections AS provider
    ON provider.id = model.provider_connection_id
WHERE model.is_enabled != 0 AND provider.is_enabled != 0
ORDER BY model.updated_at_utc DESC;";

        var results = new List<AiModelProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadAiModelProfile(reader));
        }

        return results;
    }

    public async Task DeleteModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_model_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", modelProfileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AiModelProfileSelection?> GetModelProfileSelectionAsync(
        string scope,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT scope, model_profile_id, updated_at_utc
FROM ai_model_profile_selections
WHERE scope = $scope
LIMIT 1;";
        command.Parameters.AddWithValue("$scope", scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteMappings.ReadAiModelProfileSelection(reader)
            : null;
    }

    public async Task<AiModelProfileSelection> SetModelProfileSelectionAsync(
        AiModelProfileSelection selection,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO ai_model_profile_selections(scope, model_profile_id, updated_at_utc)
VALUES($scope, $modelProfileId, $updatedAt)
ON CONFLICT(scope) DO UPDATE SET
    model_profile_id = excluded.model_profile_id,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$scope", selection.Scope);
        command.Parameters.AddWithValue("$modelProfileId", selection.ModelProfileId.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", selection.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return selection;
    }

    private static string Serialize<TValue>(IReadOnlyDictionary<string, TValue> values)
        => JsonSerializer.Serialize(values);
}
