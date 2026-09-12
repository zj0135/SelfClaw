using System.Globalization;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence;

internal static class SqliteSubagentActivityQueries
{
    private const string TaskSelect = """
        SELECT t.id, t.parent_conversation_id, t.parent_turn_id, t.child_conversation_id, t.child_turn_id,
               t.subagent_id, t.subagent_name, substr(t.task_text, 1, 240), t.status, t.attempt, t.retry_of_task_id,
               t.resolved_model_profile_id, m.name, t.cancel_requested_at_utc, t.queued_at_utc,
               t.started_at_utc, t.completed_at_utc, t.input_tokens, t.output_tokens, t.error_code, t.error_message,
               d.status, COALESCE(d.attempt_count, 0), d.last_error
        FROM subagent_tasks t
        JOIN conversations p ON p.id = t.parent_conversation_id AND p.kind = $interactive
        LEFT JOIN subagent_deliveries d ON d.task_id = t.id AND d.parent_conversation_id = t.parent_conversation_id
        LEFT JOIN ai_model_profiles m ON m.id = t.resolved_model_profile_id
        WHERE t.parent_conversation_id = $parent
        """;

    internal static async Task<IReadOnlyList<SubagentActivityTask>> ReadTasksAsync(
        SqliteConnection connection, SqliteTransaction transaction, SubagentActivityQuery query, int offset,
        CancellationToken cancellationToken)
    {
        await using var command = CreateTaskCommand(connection, transaction, query.ParentConversationId);
        command.CommandText += """

            AND ($turn IS NULL OR t.parent_turn_id = $turn)
            ORDER BY CASE WHEN t.status IN (0, 1) THEN 0 ELSE 1 END, t.queued_at_utc, t.id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$turn", query.ParentTurnId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var tasks = new List<SubagentActivityTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tasks.Add(ReadTask(reader));
        }

        return tasks.ToArray();
    }

    internal static async Task<SubagentActivityTask?> ReadTaskAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid parentConversationId, Guid taskId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateTaskCommand(connection, transaction, parentConversationId);
        command.CommandText += " AND t.id = $task;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTask(reader) : null;
    }

    internal static async Task<string> ReadTaskTextAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid taskId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT task_text FROM subagent_tasks WHERE id = $task;";
        command.Parameters.AddWithValue("$task", taskId.ToString("D"));
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The activity task disappeared within its read transaction."));
    }

    internal static async Task<MessageRecord?> ReadMessageAsync(
        SqliteConnection connection, SqliteTransaction transaction, SubagentActivityTask task, CancellationToken cancellationToken)
    {
        MessageRecord message;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc,
                       agent_id, agent_name, agent_role, input_tokens, output_tokens, duration_ms, error_message
                FROM messages WHERE id = $turn AND conversation_id = $child AND role = $assistant;
                """;
            command.Parameters.AddWithValue("$turn", task.ChildTurnId.ToString("D"));
            command.Parameters.AddWithValue("$child", task.ChildConversationId.ToString("D"));
            command.Parameters.AddWithValue("$assistant", (int)MessageRole.Assistant);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            message = SqliteMappings.ReadMessage(reader);
        }

        return message with { Segments = await ReadSegmentsAsync(connection, transaction, message.Id, cancellationToken).ConfigureAwait(false) };
    }

    internal static async Task<IReadOnlyList<ToolExecutionRecord>> ReadToolsAsync(
        SqliteConnection connection, SqliteTransaction transaction, SubagentActivityTask task, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms,
                   created_at_utc, updated_at_utc, agent_id, message_id, result_content, source_kind, source_id, display_name
            FROM tool_runs WHERE conversation_id = $child AND message_id = $turn ORDER BY created_at_utc, id;
            """;
        command.Parameters.AddWithValue("$child", task.ChildConversationId.ToString("D"));
        command.Parameters.AddWithValue("$turn", task.ChildTurnId.ToString("D"));
        var tools = new List<ToolExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tools.Add(SqliteMappings.ReadToolRun(reader));
        }

        return tools.ToArray();
    }

    private static async Task<IReadOnlyList<MessageSegmentRecord>> ReadSegmentsAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ordinal, kind, text, tool_run_id FROM message_segments WHERE message_id = $message ORDER BY ordinal;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        var segments = new List<MessageSegmentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            segments.Add(new MessageSegmentRecord(messageId, reader.GetInt32(0), (MessageSegmentKind)reader.GetInt32(1),
                Text(reader, 2), Id(reader, 3)));
        }

        return segments.ToArray();
    }

    private static SqliteCommand CreateTaskCommand(SqliteConnection connection, SqliteTransaction transaction, Guid parentConversationId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = TaskSelect;
        command.Parameters.AddWithValue("$parent", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$interactive", (int)ConversationKind.Interactive);
        return command;
    }

    private static SubagentActivityTask ReadTask(SqliteDataReader reader)
        => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), (SubagentTaskStatus)reader.GetInt32(8), reader.GetInt32(9), Id(reader, 10), Id(reader, 11),
            Text(reader, 12), Time(reader, 13), DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
            Time(reader, 15), Time(reader, 16), Number(reader, 17), Number(reader, 18), Text(reader, 19), Text(reader, 20),
            reader.IsDBNull(21) ? null : (SubagentDeliveryStatus)reader.GetInt32(21), reader.GetInt32(22), Text(reader, 23));

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static int? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    private static Guid? Id(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
}
