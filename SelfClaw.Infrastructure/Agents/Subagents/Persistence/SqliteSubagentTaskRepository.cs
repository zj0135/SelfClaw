using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Abstractions;
using SelfClaw.Infrastructure.Agents.Subagents.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence;

internal sealed class SqliteSubagentTaskRepository : ISubagentTaskRepository
{
    private const int MaxTasksPerParentTurn = 8;
    private readonly SqliteDatabase _database;

    public SqliteSubagentTaskRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<SubagentTaskRecord> CreateAsync(
        SubagentTaskCreation creation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(creation);
        ValidateCreation(creation);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        try
        {
            await EnsureInteractiveParentExistsAsync(
                connection,
                sqliteTransaction,
                creation.Task.ParentConversationId,
                cancellationToken).ConfigureAwait(false);
            await EnsureParentTurnCapacityAsync(
                connection,
                sqliteTransaction,
                creation.Task.ParentConversationId,
                creation.Task.ParentTurnId,
                cancellationToken).ConfigureAwait(false);
            await InsertChildConversationAsync(
                connection,
                sqliteTransaction,
                creation.ChildConversation,
                cancellationToken).ConfigureAwait(false);
            await InsertTaskMessageAsync(
                connection,
                sqliteTransaction,
                creation.TaskMessage,
                cancellationToken).ConfigureAwait(false);
            await InsertTaskAsync(
                connection,
                sqliteTransaction,
                creation.Task,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return creation.Task;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SubagentTaskRecord?> GetAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateTaskSelectCommand(connection);
        command.CommandText += " WHERE parent_conversation_id = $parentConversationId AND id = $taskId LIMIT 1;";
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    public async Task<IReadOnlyList<SubagentTaskRecord>> ListAsync(
        Guid parentConversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateTaskSelectCommand(connection);
        command.CommandText += " WHERE parent_conversation_id = $parentConversationId ORDER BY created_at_utc, id;";
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        var tasks = new List<SubagentTaskRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks;
    }

    public async Task<SubagentDeliveryRecord?> GetDeliveryAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, task_id, parent_conversation_id, parent_turn_id, status, envelope_json, envelope_bytes,
       lease_token, leased_until_utc, attempt_count, next_attempt_at_utc, continuation_turn_id,
       last_error, created_at_utc, updated_at_utc, delivered_at_utc, dead_lettered_at_utc
FROM subagent_deliveries
WHERE parent_conversation_id = $parentConversationId AND task_id = $taskId
LIMIT 1;";
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDelivery(reader) : null;
    }

    private static void ValidateCreation(SubagentTaskCreation creation)
    {
        var task = creation.Task;
        var child = creation.ChildConversation;
        var message = creation.TaskMessage;
        var valid = child.Kind == ConversationKind.Subagent &&
                    child.ParentConversationId == task.ParentConversationId &&
                    child.Id == task.ChildConversationId &&
                    message.ConversationId == child.Id &&
                    message.Role == MessageRole.User &&
                    message.Status == MessageStatus.Completed &&
                    string.Equals(message.MarkdownContent, task.TaskText, StringComparison.Ordinal) &&
                    task.Status == SubagentTaskStatus.Queued;
        if (!valid)
        {
            throw new ArgumentException(
                "Subagent task creation requires a queued task, its owned child conversation, and a completed task message.",
                nameof(creation));
        }
    }

    private static async Task EnsureInteractiveParentExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid parentConversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM conversations WHERE id = $id AND kind = $kind;";
        command.Parameters.AddWithValue("$id", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)ConversationKind.Interactive);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (count == 0)
        {
            throw new InvalidOperationException("The parent interactive conversation does not exist.");
        }
    }

    private static async Task EnsureParentTurnCapacityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid parentConversationId,
        Guid parentTurnId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT COUNT(*)
FROM subagent_tasks
WHERE parent_conversation_id = $parentConversationId AND parent_turn_id = $parentTurnId;";
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", parentTurnId.ToString("D"));
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
        if (count >= MaxTasksPerParentTurn)
        {
            throw new InvalidOperationException("A parent turn cannot create more than 8 Subagent tasks.");
        }
    }

    private static async Task InsertChildConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO conversations(
    id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
    channel_kind, channel_conversation_id, channel_display_name,
    created_at_utc, updated_at_utc, kind, parent_conversation_id)
VALUES(
    $id, $title, $workspaceRootId, $mode, $toolPermissionMode, $agentId,
    NULL, NULL, NULL, $createdAt, $updatedAt, $kind, $parentConversationId);";
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue(
            "$workspaceRootId",
            conversation.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$toolPermissionMode", (int)conversation.ToolPermissionMode);
        command.Parameters.AddWithValue("$agentId", conversation.AgentId);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$kind", (int)conversation.Kind);
        command.Parameters.AddWithValue(
            "$parentConversationId",
            conversation.ParentConversationId?.ToString("D")
            ?? throw new InvalidOperationException("The child conversation has no parent."));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTaskMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO messages(
    id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc,
    agent_id, agent_name, agent_role, input_tokens, output_tokens, duration_ms, error_message)
VALUES(
    $id, $conversationId, $role, $markdownContent, $status, $createdAt, $updatedAt,
    $agentId, $agentName, $agentRole, $inputTokens, $outputTokens, $durationMs, $errorMessage);";
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$markdownContent", message.MarkdownContent);
        command.Parameters.AddWithValue("$status", (int)message.Status);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", message.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", message.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentName", message.AgentName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentRole", message.AgentRole ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", message.InputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", message.OutputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", message.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", message.ErrorMessage ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTaskAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubagentTaskRecord task,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO subagent_tasks(
    id, parent_conversation_id, parent_turn_id, child_conversation_id, child_turn_id,
    subagent_id, subagent_name, task_text, status, attempt, retry_of_task_id,
    definition_snapshot_json, parent_execution_snapshot_json, resolved_model_profile_id,
    max_run_seconds, final_text, input_tokens, output_tokens, error_code, error_message,
    cancel_requested_at_utc, queued_at_utc, started_at_utc, completed_at_utc,
    created_at_utc, updated_at_utc)
VALUES(
    $id, $parentConversationId, $parentTurnId, $childConversationId, $childTurnId,
    $subagentId, $subagentName, $taskText, $status, $attempt, $retryOfTaskId,
    $definitionSnapshotJson, $parentExecutionSnapshotJson, $resolvedModelProfileId,
    $maxRunSeconds, $finalText, $inputTokens, $outputTokens, $errorCode, $errorMessage,
    $cancelRequestedAt, $queuedAt, $startedAt, $completedAt, $createdAt, $updatedAt);";
        AddTaskParameters(command, task);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateTaskSelectCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, parent_conversation_id, parent_turn_id, child_conversation_id, child_turn_id,
       subagent_id, subagent_name, task_text, status, attempt, retry_of_task_id,
       definition_snapshot_json, parent_execution_snapshot_json, resolved_model_profile_id,
       max_run_seconds, final_text, input_tokens, output_tokens, error_code, error_message,
       cancel_requested_at_utc, queued_at_utc, started_at_utc, completed_at_utc,
       created_at_utc, updated_at_utc
FROM subagent_tasks";
        return command;
    }

    private static void AddTaskParameters(SqliteCommand command, SubagentTaskRecord task)
    {
        command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
        command.Parameters.AddWithValue("$parentConversationId", task.ParentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", task.ParentTurnId.ToString("D"));
        command.Parameters.AddWithValue("$childConversationId", task.ChildConversationId.ToString("D"));
        command.Parameters.AddWithValue("$childTurnId", task.ChildTurnId.ToString("D"));
        command.Parameters.AddWithValue("$subagentId", task.SubagentId);
        command.Parameters.AddWithValue("$subagentName", task.SubagentName);
        command.Parameters.AddWithValue("$taskText", task.TaskText);
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$attempt", task.Attempt);
        command.Parameters.AddWithValue("$retryOfTaskId", task.RetryOfTaskId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$definitionSnapshotJson", task.DefinitionSnapshotJson);
        command.Parameters.AddWithValue("$parentExecutionSnapshotJson", task.ParentExecutionSnapshotJson);
        command.Parameters.AddWithValue(
            "$resolvedModelProfileId",
            task.ResolvedModelProfileId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$maxRunSeconds", task.MaxRunSeconds);
        command.Parameters.AddWithValue("$finalText", task.FinalText ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", task.InputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", task.OutputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", task.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", task.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$cancelRequestedAt",
            task.CancelRequestedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$queuedAt", task.QueuedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$startedAt", task.StartedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", task.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", task.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", task.UpdatedAtUtc.ToString("O"));
    }

    private static SubagentTaskRecord ReadTask(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            ReadGuid(reader, 2),
            ReadGuid(reader, 3),
            ReadGuid(reader, 4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            (SubagentTaskStatus)reader.GetInt32(8),
            reader.GetInt32(9),
            ReadNullableGuid(reader, 10),
            reader.GetString(11),
            reader.GetString(12),
            ReadNullableGuid(reader, 13),
            reader.GetInt32(14),
            ReadNullableString(reader, 15),
            ReadNullableInt32(reader, 16),
            ReadNullableInt32(reader, 17),
            ReadNullableString(reader, 18),
            ReadNullableString(reader, 19),
            ReadNullableDateTimeOffset(reader, 20),
            ReadDateTimeOffset(reader, 21),
            ReadNullableDateTimeOffset(reader, 22),
            ReadNullableDateTimeOffset(reader, 23),
            ReadDateTimeOffset(reader, 24),
            ReadDateTimeOffset(reader, 25));

    private static SubagentDeliveryRecord ReadDelivery(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            ReadGuid(reader, 2),
            ReadGuid(reader, 3),
            (SubagentDeliveryStatus)reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt32(6),
            ReadNullableGuid(reader, 7),
            ReadNullableDateTimeOffset(reader, 8),
            reader.GetInt32(9),
            ReadDateTimeOffset(reader, 10),
            ReadNullableGuid(reader, 11),
            ReadNullableString(reader, 12),
            ReadDateTimeOffset(reader, 13),
            ReadDateTimeOffset(reader, 14),
            ReadNullableDateTimeOffset(reader, 15),
            ReadNullableDateTimeOffset(reader, 16));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader, ordinal);
}
