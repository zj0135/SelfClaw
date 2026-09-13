using SelfClaw.Core.Models;
using System.Text.Json;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

public sealed partial class SqliteAiProviderRepository
{
    public async Task<IReadOnlyList<AiModelConfiguration>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_json FROM ai_model_configurations ORDER BY model;";
        var results = new List<AiModelConfiguration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(JsonSerializer.Deserialize<AiModelConfiguration>(reader.GetString(0))
                ?? throw new InvalidDataException("Invalid model configuration."));
        }

        return results;
    }

    public async Task SaveAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_model_configurations(model, configuration_json) VALUES($model, $configuration)
            ON CONFLICT(model) DO UPDATE SET configuration_json = excluded.configuration_json;
            """;
        command.Parameters.AddWithValue("$model", configuration.Model);
        command.Parameters.AddWithValue("$configuration", JsonSerializer.Serialize(configuration));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_model_configurations WHERE model = $model;";
        command.Parameters.AddWithValue("$model", model);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
