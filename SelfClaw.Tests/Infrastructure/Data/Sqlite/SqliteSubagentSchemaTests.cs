using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite;

public sealed class SqliteSubagentSchemaTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));
    private Guid _taskId;

    [Fact]
    public async Task Fresh_database_creates_schema_v25_with_subagent_tables_and_indexes()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();

        (await ExecuteScalarAsync<long>(connection, "SELECT MAX(version) FROM schema_versions;"))
            .Should().Be(25);
        (await ReadNamesAsync(connection, "table", "subagent_%"))
            .Should().BeEquivalentTo("subagent_tasks", "subagent_deliveries");
        (await ReadNamesAsync(connection, "index", "ix_subagent_%"))
            .Should().BeEquivalentTo(
                "ix_subagent_tasks_queue",
                "ix_subagent_tasks_parent_status",
                "ix_subagent_tasks_parent_turn",
                "ix_subagent_deliveries_ready",
                "ix_subagent_deliveries_parent_turn",
                "ix_subagent_deliveries_lease");
        (await ReadColumnNamesAsync(connection, "conversations"))
            .Should().Contain(["kind", "parent_conversation_id"]);
    }

    [Theory]
    [InlineData(-1, 0, 2)]
    [InlineData(4, 0, 2)]
    [InlineData(0, -1, 2)]
    [InlineData(0, 4, 2)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 32769)]
    public async Task Delivery_checks_reject_invalid_status_attempt_or_envelope_size(
        int status,
        int attemptCount,
        int envelopeBytes)
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();
        var parentId = await InsertTaskGraphAsync(connection);
        await using var command = CreateDeliveryCommand(
            connection,
            Guid.NewGuid(),
            _taskId,
            parentId,
            status,
            attemptCount,
            envelopeBytes);

        var action = () => command.ExecuteNonQueryAsync();

        await action.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Delivery_allows_only_one_row_per_task()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();
        var parentId = await InsertTaskGraphAsync(connection);
        await using (var first = CreateDeliveryCommand(
                         connection,
                         Guid.NewGuid(),
                         _taskId,
                         parentId,
                         status: 0,
                         attemptCount: 0,
                         envelopeBytes: 2))
        {
            await first.ExecuteNonQueryAsync();
        }

        await using var duplicate = CreateDeliveryCommand(
            connection,
            Guid.NewGuid(),
            _taskId,
            parentId,
            status: 0,
            attemptCount: 0,
            envelopeBytes: 2);
        var action = () => duplicate.ExecuteNonQueryAsync();

        await action.Should().ThrowAsync<SqliteException>();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteDatabase CreateDatabase()
        => new(new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));

    private async Task<Guid> InsertTaskGraphAsync(SqliteConnection connection)
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _taskId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(
                id, title, mode, tool_permission_mode, agent_id,
                created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($parentId, 'Parent', 0, 0, 'build', $now, $now, 0, NULL);

            INSERT INTO conversations(
                id, title, mode, tool_permission_mode, agent_id,
                created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($childId, 'Child', 0, 0, 'build', $now, $now, 1, $parentId);

            INSERT INTO subagent_tasks(
                id, parent_conversation_id, parent_turn_id, child_conversation_id, child_turn_id,
                subagent_id, subagent_name, task_text, status, attempt,
                definition_snapshot_json, parent_execution_snapshot_json, max_run_seconds,
                queued_at_utc, created_at_utc, updated_at_utc)
            VALUES(
                $taskId, $parentId, $parentTurnId, $childId, $childTurnId,
                'reviewer', 'Reviewer', 'Review', 0, 1,
                '{}', '{}', 900, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$parentId", parentId.ToString("D"));
        command.Parameters.AddWithValue("$childId", childId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", _taskId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$childTurnId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync();
        return parentId;
    }

    private static SqliteCommand CreateDeliveryCommand(
        SqliteConnection connection,
        Guid deliveryId,
        Guid taskId,
        Guid parentId,
        int status,
        int attemptCount,
        int envelopeBytes)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO subagent_deliveries(
                id, task_id, parent_conversation_id, parent_turn_id, status,
                envelope_json, envelope_bytes, attempt_count, next_attempt_at_utc,
                created_at_utc, updated_at_utc)
            VALUES(
                $id, $taskId, $parentId, $parentTurnId, $status,
                '{}', $envelopeBytes, $attemptCount, $now, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", deliveryId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$parentId", parentId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$envelopeBytes", envelopeBytes);
        command.Parameters.AddWithValue("$attemptCount", attemptCount);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return command;
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(
        SqliteConnection connection,
        string type,
        string namePattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$pattern", namePattern);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("No value returned."));
    }
}
