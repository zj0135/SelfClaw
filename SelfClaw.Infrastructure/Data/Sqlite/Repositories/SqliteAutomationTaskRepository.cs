using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteAutomationTaskRepository : IAutomationTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteDatabase _database;

    public SqliteAutomationTaskRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<AutomationTaskRecord?> GetAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSelectCommand(connection);
        command.CommandText += " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<AutomationTaskRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateSelectCommand(connection);
        command.CommandText += " ORDER BY created_at_utc, id;";
        var tasks = new List<AutomationTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public async Task<AutomationTaskRecord> SaveAsync(AutomationTaskRecord task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        task = Normalize(task);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO automation_tasks(
                id, title, notes, kind, schedule_json, next_run_at_utc, prompt, instructions, tool_policy,
                plugin_ids_json, skill_ids_json, mcp_server_ids_json, model_profile_id, workspace_root_id,
                tool_permission_mode, status, completed_at_utc, due_at_utc, is_enabled, last_run_at_utc,
                last_run_status, created_at_utc, updated_at_utc)
            VALUES(
                $id, $title, $notes, $kind, $scheduleJson, $nextRunAt, $prompt, $instructions, $toolPolicy,
                $pluginIds, $skillIds, $mcpServerIds, $modelProfileId, $workspaceRootId,
                $toolPermissionMode, $status, $completedAt, $dueAt, $isEnabled, $lastRunAt,
                $lastRunStatus, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                notes = excluded.notes,
                kind = excluded.kind,
                schedule_json = excluded.schedule_json,
                next_run_at_utc = excluded.next_run_at_utc,
                prompt = excluded.prompt,
                instructions = excluded.instructions,
                tool_policy = excluded.tool_policy,
                plugin_ids_json = excluded.plugin_ids_json,
                skill_ids_json = excluded.skill_ids_json,
                mcp_server_ids_json = excluded.mcp_server_ids_json,
                model_profile_id = excluded.model_profile_id,
                workspace_root_id = excluded.workspace_root_id,
                tool_permission_mode = excluded.tool_permission_mode,
                status = excluded.status,
                completed_at_utc = excluded.completed_at_utc,
                due_at_utc = excluded.due_at_utc,
                is_enabled = excluded.is_enabled,
                last_run_at_utc = excluded.last_run_at_utc,
                last_run_status = excluded.last_run_status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task<AutomationTaskRecord?> SetEnabledAsync(
        Guid taskId,
        bool isEnabled,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE automation_tasks SET is_enabled = $enabled, updated_at_utc = $at WHERE id = $id;";
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$at", updatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using var select = CreateSelectCommand(connection, transaction);
        select.CommandText += " WHERE id = $id;";
        select.Parameters.AddWithValue("$id", taskId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        AutomationTaskRecord? task = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task<bool> DeleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM automation_tasks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", taskId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static AutomationTaskRecord Normalize(AutomationTaskRecord task)
        // A task that will run an unattended Direct turn may not gate its tools behind an approval no one
        // can answer: the single explicit confirmation happens when the prompt is saved. Pure todos keep
        // whatever mode they carry because they never invoke a tool.
        => task.Prompt is null ? task : task with { ToolPermissionMode = ToolPermissionMode.FullAccess };

    private static SqliteCommand CreateSelectCommand(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, notes, kind, schedule_json, next_run_at_utc, prompt, instructions, tool_policy,
                   plugin_ids_json, skill_ids_json, mcp_server_ids_json, model_profile_id, workspace_root_id,
                   tool_permission_mode, status, completed_at_utc, due_at_utc, is_enabled, last_run_at_utc,
                   last_run_status, created_at_utc, updated_at_utc
            FROM automation_tasks
            """;
        return command;
    }

    private static void AddTaskParameters(SqliteCommand command, AutomationTaskRecord task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$notes", task.Notes);
        command.Parameters.AddWithValue("$kind", (int)task.Kind);
        command.Parameters.AddWithValue(
            "$scheduleJson",
            task.Schedule is null ? DBNull.Value : JsonSerializer.Serialize(task.Schedule, JsonOptions));
        command.Parameters.AddWithValue("$nextRunAt", ToDbValue(task.NextRunAtUtc));
        command.Parameters.AddWithValue("$prompt", task.Prompt ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$instructions", task.Instructions);
        command.Parameters.AddWithValue("$toolPolicy", task.ToolPolicy);
        command.Parameters.AddWithValue("$pluginIds", SerializeList(task.PluginIds));
        command.Parameters.AddWithValue("$skillIds", SerializeList(task.SkillIds));
        command.Parameters.AddWithValue("$mcpServerIds", SerializeList(task.McpServerIds));
        command.Parameters.AddWithValue("$modelProfileId", task.ModelProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspaceRootId", task.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$toolPermissionMode", (int)task.ToolPermissionMode);
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$completedAt", ToDbValue(task.CompletedAtUtc));
        command.Parameters.AddWithValue("$dueAt", ToDbValue(task.DueAtUtc));
        command.Parameters.AddWithValue("$isEnabled", task.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$lastRunAt", ToDbValue(task.LastRunAtUtc));
        command.Parameters.AddWithValue(
            "$lastRunStatus",
            task.LastRunStatus is AutomationRunStatus status ? (int)status : DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAtUtc.ToString("O"));
    }

    private static AutomationTaskRecord ReadTask(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.GetString(2),
            (AutomationTaskKind)reader.GetInt32(3),
            ReadSchedule(reader, 4),
            ReadNullableDateTimeOffset(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ReadStringList(reader, 9),
            ReadStringList(reader, 10),
            ReadStringList(reader, 11),
            ReadNullableGuid(reader, 12),
            ReadNullableGuid(reader, 13),
            (ToolPermissionMode)reader.GetInt32(14),
            (AutomationTaskStatus)reader.GetInt32(15),
            ReadNullableDateTimeOffset(reader, 16),
            ReadNullableDateTimeOffset(reader, 17),
            reader.GetInt32(18) != 0,
            ReadNullableDateTimeOffset(reader, 19),
            reader.IsDBNull(20) ? null : (AutomationRunStatus)reader.GetInt32(20),
            ReadDateTimeOffset(reader, 21),
            ReadDateTimeOffset(reader, 22));

    private static AutomationScheduleSpec? ReadSchedule(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : JsonSerializer.Deserialize<AutomationScheduleSpec>(reader.GetString(ordinal), JsonOptions);

    private static IReadOnlyList<string> ReadStringList(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? []
            : JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal), JsonOptions) ?? [];

    private static string SerializeList(IReadOnlyList<string> values)
        => JsonSerializer.Serialize(values, JsonOptions);

    private static object ToDbValue(DateTimeOffset? value)
        => value?.ToString("O") ?? (object)DBNull.Value;

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader, ordinal);
}
