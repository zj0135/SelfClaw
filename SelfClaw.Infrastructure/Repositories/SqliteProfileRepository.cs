using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data;

namespace SelfClaw.Infrastructure.Repositories;

public sealed class SqliteProfileRepository : IProfileRepository
{
    private readonly SqliteDatabase _database;

    public SqliteProfileRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<ProviderProfile>> ListProfilesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name, endpoint, model, temperature, top_p, api_style, secret_ref, created_at_utc, updated_at_utc
FROM profiles
ORDER BY updated_at_utc DESC;";

        var results = new List<ProviderProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadProfile(reader));
        }

        return results;
    }

    public async Task<ProviderProfile?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name, endpoint, model, temperature, top_p, api_style, secret_ref, created_at_utc, updated_at_utc
FROM profiles
WHERE id = $id
LIMIT 1;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteMappings.ReadProfile(reader)
            : null;
    }

    public async Task<ProviderProfile> UpsertProfileAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO profiles(id, name, endpoint, model, temperature, top_p, api_style, secret_ref, created_at_utc, updated_at_utc)
VALUES($id, $name, $endpoint, $model, $temperature, $topP, $apiStyle, $secretRef, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    endpoint = excluded.endpoint,
    model = excluded.model,
    temperature = excluded.temperature,
    top_p = excluded.top_p,
    api_style = excluded.api_style,
    secret_ref = excluded.secret_ref,
    updated_at_utc = excluded.updated_at_utc;";

        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$endpoint", profile.Endpoint);
        command.Parameters.AddWithValue("$model", profile.Model);
        command.Parameters.AddWithValue("$temperature", profile.Temperature);
        command.Parameters.AddWithValue("$topP", profile.TopP);
        command.Parameters.AddWithValue("$apiStyle", (int)profile.ApiStyle);
        command.Parameters.AddWithValue("$secretRef", profile.SecretRef);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", profile.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return profile;
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
