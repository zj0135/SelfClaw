using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite;

public sealed class AutomationSchemaTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Fresh_database_creates_automation_tables_and_indexes()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();

        (await ReadNamesAsync(connection, "table", "automation_%"))
            .Should().BeEquivalentTo("automation_tasks", "automation_runs");
        (await ReadNamesAsync(connection, "index", "%automation_%"))
            .Should().BeEquivalentTo(
                "ux_automation_runs_scheduled",
                "ix_automation_runs_task_created",
                "ix_automation_runs_status",
                "ix_automation_tasks_next_run");
    }

    [Fact]
    public async Task Conversation_kind_two_is_accepted_without_a_parent_and_rejected_with_one()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();

        await InsertConversationAsync(connection, Guid.NewGuid(), kind: 2, parentConversationId: null);

        await using var invalid = CreateConversationCommand(connection, Guid.NewGuid(), kind: 2, parentConversationId: Guid.NewGuid());
        var action = () => invalid.ExecuteNonQueryAsync();

        await action.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task Conversation_turn_origin_three_is_accepted_and_four_is_rejected()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        await using var connection = await database.OpenConnectionAsync();
        var conversationId = Guid.NewGuid();
        await InsertConversationAsync(connection, conversationId, kind: 0, parentConversationId: null);

        await ExecuteAsync(connection, """
            INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc, completed_at_utc)
            VALUES($id, $conversationId, 0, 3, 1, $at, $at);
            """, ("$id", Guid.NewGuid().ToString("D")), ("$conversationId", conversationId.ToString("D")), ("$at", DateTimeOffset.UtcNow.ToString("O")));

        await using var invalid = connection.CreateCommand();
        invalid.CommandText = """
            INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc, completed_at_utc)
            VALUES($id, $conversationId, 0, 4, 1, $at, $at);
            """;
        invalid.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        invalid.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        invalid.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        var action = () => invalid.ExecuteNonQueryAsync();

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
        => new(StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets")));

    private static async Task InsertConversationAsync(
        SqliteConnection connection,
        Guid conversationId,
        int kind,
        Guid? parentConversationId)
    {
        await using var command = CreateConversationCommand(connection, conversationId, kind, parentConversationId);
        await command.ExecuteNonQueryAsync();
    }

    private static SqliteCommand CreateConversationCommand(
        SqliteConnection connection,
        Guid conversationId,
        int kind,
        Guid? parentConversationId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(
                id, title, mode, tool_permission_mode, agent_id, created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($id, 'Automation', 0, 1, 'build', $at, $at, $kind, $parentId);
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$parentId", parentConversationId?.ToString("D") ?? (object)DBNull.Value);
        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(
        SqliteConnection connection,
        string type,
        string namePattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern AND name NOT LIKE 'sqlite_%';";
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
}
