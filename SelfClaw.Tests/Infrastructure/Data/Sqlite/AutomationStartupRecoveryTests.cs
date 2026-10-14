using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite;

public sealed class AutomationStartupRecoveryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Recovery_interrupts_automation_runs_and_origin_three_turns_only()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync();
        var now = DateTimeOffset.UtcNow;
        Guid taskId;
        Guid queuedRunId;
        Guid runningRunId;
        Guid automationTurnId;
        Guid assistantMessageId;
        Guid toolRunId;
        Guid interactiveTurnId;
        await using (var connection = await database.OpenConnectionAsync())
        {
            var automationConversationId = Guid.NewGuid();
            var interactiveConversationId = Guid.NewGuid();
            taskId = Guid.NewGuid();
            queuedRunId = Guid.NewGuid();
            runningRunId = Guid.NewGuid();
            automationTurnId = Guid.NewGuid();
            assistantMessageId = Guid.NewGuid();
            toolRunId = Guid.NewGuid();
            interactiveTurnId = Guid.NewGuid();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO conversations(id, title, mode, tool_permission_mode, agent_id, created_at_utc, updated_at_utc, kind)
                VALUES($automationConversationId, 'Automation', 0, 1, 'build', $at, $at, 2);
                INSERT INTO conversations(id, title, mode, tool_permission_mode, agent_id, created_at_utc, updated_at_utc, kind)
                VALUES($interactiveConversationId, 'Chat', 0, 0, 'build', $at, $at, 0);

                INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc)
                VALUES($automationTurnId, $automationConversationId, 0, 3, 0, $at);
                INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc)
                VALUES($interactiveTurnId, $interactiveConversationId, 0, 0, 0, $at);

                INSERT INTO messages(id, conversation_id, turn_id, sequence, role, markdown_content, status, created_at_utc, updated_at_utc)
                VALUES($assistantMessageId, $automationConversationId, $automationTurnId, 2, 2, 'partial', 1, $at, $at);

                INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, created_at_utc, updated_at_utc, message_id)
                VALUES($toolRunId, $automationConversationId, 'read_file', '{}', 0, $at, $at, $assistantMessageId);

                INSERT INTO automation_tasks(id, title, kind, created_at_utc, updated_at_utc)
                VALUES($taskId, 'Task', 0, $at, $at);
                INSERT INTO automation_runs(id, task_id, trigger, scheduled_for_utc, status, attempt, created_at_utc)
                VALUES($queuedRunId, $taskId, 0, $queuedAt, 0, 1, $at);
                INSERT INTO automation_runs(id, task_id, trigger, scheduled_for_utc, status, attempt, created_at_utc)
                VALUES($runningRunId, $taskId, 0, $runningAt, 1, 1, $at);
                """;
            command.Parameters.AddWithValue("$automationConversationId", automationConversationId.ToString("D"));
            command.Parameters.AddWithValue("$interactiveConversationId", interactiveConversationId.ToString("D"));
            command.Parameters.AddWithValue("$automationTurnId", automationTurnId.ToString("D"));
            command.Parameters.AddWithValue("$interactiveTurnId", interactiveTurnId.ToString("D"));
            command.Parameters.AddWithValue("$assistantMessageId", assistantMessageId.ToString("D"));
            command.Parameters.AddWithValue("$toolRunId", toolRunId.ToString("D"));
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            command.Parameters.AddWithValue("$queuedRunId", queuedRunId.ToString("D"));
            command.Parameters.AddWithValue("$runningRunId", runningRunId.ToString("D"));
            command.Parameters.AddWithValue("$queuedAt", now.AddMinutes(-2).ToString("O"));
            command.Parameters.AddWithValue("$runningAt", now.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await new AutomationStartupRecovery(database).RecoverAsync();

        await using var verification = await database.OpenConnectionAsync();
        (await ReadStatusAsync(verification, "automation_runs", queuedRunId)).Should().Be(5);
        (await ReadStatusAsync(verification, "automation_runs", runningRunId)).Should().Be(5);
        (await ReadColumnAsync(verification, "automation_runs", "error_code", queuedRunId)).Should().Be(AutomationErrorCodes.ProcessInterrupted);
        (await ReadColumnAsync(verification, "automation_runs", "completed_at_utc", queuedRunId)).Should().NotBeNull();
        (await ReadStatusAsync(verification, "conversation_turns", automationTurnId)).Should().Be(6);
        (await ReadStatusAsync(verification, "messages", assistantMessageId)).Should().Be(2);
        (await ReadStatusAsync(verification, "tool_runs", toolRunId)).Should().Be(2);
        (await ReadStatusAsync(verification, "conversation_turns", interactiveTurnId)).Should().Be(0);
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

    private static async Task<long> ReadStatusAsync(SqliteConnection connection, string table, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT status FROM {table} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing row."));
    }

    private static async Task<object?> ReadColumnAsync(SqliteConnection connection, string table, string column, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }
}
