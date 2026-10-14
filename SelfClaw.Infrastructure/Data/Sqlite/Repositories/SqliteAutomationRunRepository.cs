using System.Globalization;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteAutomationRunRepository : IAutomationRunStore, IAutomationRunAttemptStore
{
    private const string SelectColumns = """
        id, task_id, conversation_id, trigger, scheduled_for_utc, status, skip_reason, attempt,
        model_profile_id, capability_snapshot_json, workspace_root_id, workspace_path_snapshot,
        started_at_utc, completed_at_utc, error_code, error_message, created_at_utc
        """;
    private readonly SqliteDatabase _database;

    public SqliteAutomationRunRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<AutomationRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRunAsync(connection, null, runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutomationRunRecord>> ListAsync(
        Guid? taskId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filter = taskId is Guid id ? "WHERE task_id = $taskId" : string.Empty;
        command.CommandText = $"SELECT {SelectColumns} FROM automation_runs {filter} ORDER BY created_at_utc DESC, id DESC LIMIT $limit;";
        if (taskId is Guid task)
        {
            command.Parameters.AddWithValue("$taskId", task.ToString("D"));
        }

        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var runs = new List<AutomationRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadRun(reader));
        }

        return runs;
    }

    public async Task<AutomationRunRecord?> TryInsertAsync(
        AutomationRunRecord run,
        bool skipWhenTaskActive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            if (skipWhenTaskActive && await HasActiveRunAsync(connection, transaction, run.TaskId, cancellationToken).ConfigureAwait(false))
            {
                var overlap = run with
                {
                    Id = Guid.NewGuid(),
                    Status = AutomationRunStatus.Skipped,
                    SkipReason = AutomationRunSkipReason.Overlap,
                    ErrorCode = null,
                    ErrorMessage = null,
                    CompletedAtUtc = run.CreatedAtUtc
                };
                await InsertRunAsync(connection, transaction, overlap, ignoreConflict: true, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return overlap;
            }

            if (run.ScheduledForUtc is DateTimeOffset scheduledFor &&
                await HasScheduledOccurrenceAsync(connection, transaction, run.TaskId, scheduledFor, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            await InsertRunAsync(connection, transaction, run, ignoreConflict: false, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return run;
        }
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode is 2067 or 1555)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AutomationRunRecord?> TryClaimAsync(
        Guid runId,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE automation_runs SET status = $running, started_at_utc = $at WHERE id = $id AND status = $queued;";
        command.Parameters.AddWithValue("$running", (int)AutomationRunStatus.Running);
        command.Parameters.AddWithValue("$at", startedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$queued", (int)AutomationRunStatus.Queued);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var claimed = await ReadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task<AutomationRunRecord?> RecordAttemptAsync(
        AutomationRunAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE automation_runs SET
                conversation_id = $conversationId,
                attempt = $attempt,
                model_profile_id = $modelProfileId,
                capability_snapshot_json = $capabilitySnapshot,
                workspace_root_id = $workspaceRootId,
                workspace_path_snapshot = $workspacePath,
                started_at_utc = COALESCE(started_at_utc, $startedAt)
            WHERE id = $id AND status = $running;
            """;
        command.Parameters.AddWithValue("$conversationId", attempt.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$attempt", attempt.Attempt);
        command.Parameters.AddWithValue("$modelProfileId", attempt.ModelProfileId.ToString("D"));
        command.Parameters.AddWithValue("$capabilitySnapshot", attempt.CapabilitySnapshotJson);
        command.Parameters.AddWithValue("$workspaceRootId", attempt.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspacePath", attempt.WorkspacePathSnapshot ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", attempt.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$id", attempt.RunId.ToString("D"));
        command.Parameters.AddWithValue("$running", (int)AutomationRunStatus.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var recorded = await ReadRunAsync(connection, transaction, attempt.RunId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recorded;
    }

    public async Task<AutomationRunRecord?> TryCompleteAsync(
        Guid runId,
        AutomationRunRecord terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE automation_runs SET
                status = $status,
                skip_reason = $skipReason,
                conversation_id = $conversationId,
                attempt = $attempt,
                model_profile_id = $modelProfileId,
                capability_snapshot_json = $capabilitySnapshot,
                workspace_root_id = $workspaceRootId,
                workspace_path_snapshot = $workspacePath,
                started_at_utc = COALESCE($startedAt, started_at_utc),
                completed_at_utc = $completedAt,
                error_code = $errorCode,
                error_message = $errorMessage
            WHERE id = $id AND status IN ($queued, $running);
            """;
        command.Parameters.AddWithValue("$status", (int)terminal.Status);
        command.Parameters.AddWithValue("$skipReason", terminal.SkipReason is AutomationRunSkipReason skip ? (int)skip : DBNull.Value);
        command.Parameters.AddWithValue("$conversationId", terminal.ConversationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$attempt", terminal.Attempt);
        command.Parameters.AddWithValue("$modelProfileId", terminal.ModelProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$capabilitySnapshot", terminal.CapabilitySnapshotJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspaceRootId", terminal.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspacePath", terminal.WorkspacePathSnapshot ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", terminal.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", terminal.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", terminal.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", terminal.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        command.Parameters.AddWithValue("$queued", (int)AutomationRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)AutomationRunStatus.Running);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var completed = await ReadRunAsync(connection, transaction, runId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return completed;
    }

    public async Task<int> DeleteTerminalRunsAsync(
        Guid? taskId,
        Guid? runId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var targets = new List<(Guid RunId, Guid? ConversationId)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            var filters = new List<string> { "status NOT IN ($queued, $running)" };
            if (runId is Guid id)
            {
                filters.Add("id = $runId");
                select.Parameters.AddWithValue("$runId", id.ToString("D"));
            }

            if (taskId is Guid task)
            {
                filters.Add("task_id = $taskId");
                select.Parameters.AddWithValue("$taskId", task.ToString("D"));
            }

            select.Parameters.AddWithValue("$queued", (int)AutomationRunStatus.Queued);
            select.Parameters.AddWithValue("$running", (int)AutomationRunStatus.Running);
            select.CommandText = $"SELECT id, conversation_id FROM automation_runs WHERE {string.Join(" AND ", filters)};";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targets.Add((ReadGuid(reader, 0), ReadNullableGuid(reader, 1)));
            }
        }

        foreach (var conversationId in targets.Where(item => item.ConversationId is not null).Select(item => item.ConversationId!.Value).Distinct())
        {
            await using var deleteConversation = connection.CreateCommand();
            deleteConversation.Transaction = transaction;
            // The run row cascades with its conversation, so only conversationless runs need an explicit delete.
            deleteConversation.CommandText = "DELETE FROM conversations WHERE id = $id;";
            deleteConversation.Parameters.AddWithValue("$id", conversationId.ToString("D"));
            await deleteConversation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var target in targets.Where(item => item.ConversationId is null))
        {
            await using var deleteRun = connection.CreateCommand();
            deleteRun.Transaction = transaction;
            deleteRun.CommandText = "DELETE FROM automation_runs WHERE id = $id;";
            deleteRun.Parameters.AddWithValue("$id", target.RunId.ToString("D"));
            await deleteRun.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return targets.Count;
    }

    public async Task<AutomationHistoryUsage> GetHistoryUsageAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var bytesByTask = new Dictionary<Guid, long>();
        await AccumulateAsync(
            connection,
            """
            SELECT r.task_id, COALESCE(SUM(LENGTH(m.markdown_content)), 0)
            FROM automation_runs r JOIN messages m ON m.conversation_id = r.conversation_id
            GROUP BY r.task_id;
            """,
            bytesByTask,
            cancellationToken).ConfigureAwait(false);
        await AccumulateAsync(
            connection,
            """
            SELECT r.task_id,
                   COALESCE(SUM(LENGTH(t.arguments_json) + LENGTH(COALESCE(t.result_summary, '')) + LENGTH(COALESCE(t.result_content, ''))), 0)
            FROM automation_runs r JOIN tool_runs t ON t.conversation_id = r.conversation_id
            GROUP BY r.task_id;
            """,
            bytesByTask,
            cancellationToken).ConfigureAwait(false);
        var tasks = bytesByTask
            .Select(pair => new AutomationTaskHistoryUsage(pair.Key, pair.Value))
            .OrderByDescending(item => item.Bytes)
            .ToArray();
        return new AutomationHistoryUsage(tasks, tasks.Sum(item => item.Bytes));
    }

    private static async Task AccumulateAsync(
        SqliteConnection connection,
        string sql,
        Dictionary<Guid, long> bytesByTask,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var taskId = ReadGuid(reader, 0);
            var bytes = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
            bytesByTask[taskId] = bytesByTask.GetValueOrDefault(taskId) + bytes;
        }
    }

    private static async Task<bool> HasActiveRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM automation_runs WHERE task_id = $taskId AND status IN ($queued, $running);";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$queued", (int)AutomationRunStatus.Queued);
        command.Parameters.AddWithValue("$running", (int)AutomationRunStatus.Running);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) > 0;
    }

    private static async Task<bool> HasScheduledOccurrenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid taskId,
        DateTimeOffset scheduledForUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM automation_runs WHERE task_id = $taskId AND scheduled_for_utc = $scheduledFor;";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$scheduledFor", scheduledForUtc.ToString("O"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) > 0;
    }

    private static async Task InsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AutomationRunRecord run,
        bool ignoreConflict,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT {(ignoreConflict ? "OR IGNORE " : string.Empty)}INTO automation_runs(
                id, task_id, conversation_id, trigger, scheduled_for_utc, status, skip_reason, attempt,
                model_profile_id, capability_snapshot_json, workspace_root_id, workspace_path_snapshot,
                started_at_utc, completed_at_utc, error_code, error_message, created_at_utc)
            VALUES(
                $id, $taskId, $conversationId, $trigger, $scheduledFor, $status, $skipReason, $attempt,
                $modelProfileId, $capabilitySnapshot, $workspaceRootId, $workspacePath,
                $startedAt, $completedAt, $errorCode, $errorMessage, $createdAt);
            """;
        AddRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AutomationRunRecord?> ReadRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {SelectColumns} FROM automation_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    private static void AddRunParameters(SqliteCommand command, AutomationRunRecord run)
    {
        command.Parameters.AddWithValue("$id", run.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", run.TaskId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", run.ConversationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$trigger", (int)run.Trigger);
        command.Parameters.AddWithValue("$scheduledFor", run.ScheduledForUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)run.Status);
        command.Parameters.AddWithValue("$skipReason", run.SkipReason is AutomationRunSkipReason skip ? (int)skip : DBNull.Value);
        command.Parameters.AddWithValue("$attempt", run.Attempt);
        command.Parameters.AddWithValue("$modelProfileId", run.ModelProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$capabilitySnapshot", run.CapabilitySnapshotJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspaceRootId", run.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$workspacePath", run.WorkspacePathSnapshot ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", run.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", run.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", run.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", run.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", run.CreatedAtUtc.ToString("O"));
    }

    private static AutomationRunRecord ReadRun(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            ReadNullableGuid(reader, 2),
            (AutomationRunTrigger)reader.GetInt32(3),
            ReadNullableDateTimeOffset(reader, 4),
            (AutomationRunStatus)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : (AutomationRunSkipReason)reader.GetInt32(6),
            reader.GetInt32(7),
            ReadNullableGuid(reader, 8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            ReadNullableGuid(reader, 10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            ReadNullableDateTimeOffset(reader, 12),
            ReadNullableDateTimeOffset(reader, 13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            ReadDateTimeOffset(reader, 16));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader, ordinal);
}
