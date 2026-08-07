using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal static class SqliteTurnFinalizationWriter
{
    internal static async Task<bool> TryWriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TurnFinalization finalization,
        CancellationToken cancellationToken)
    {
        var messageWritten = await UpsertMessageAsync(
                connection,
                transaction,
                finalization.AssistantMessage,
                onlyIfStreaming: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!messageWritten)
        {
            return false;
        }

        foreach (var toolExecution in finalization.ToolExecutions)
        {
            await UpsertToolExecutionAsync(
                    connection,
                    transaction,
                    toolExecution,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    internal static async Task<bool> UpsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MessageRecord message,
        bool onlyIfStreaming,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO messages(id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc, agent_id, agent_name, agent_role, input_tokens, output_tokens, duration_ms, error_message)
VALUES($id, $conversationId, $role, $markdownContent, $status, $createdAt, $updatedAt, $agentId, $agentName, $agentRole, $inputTokens, $outputTokens, $durationMs, $errorMessage)
ON CONFLICT(id) DO UPDATE SET
    markdown_content = excluded.markdown_content,
    status = excluded.status,
    updated_at_utc = excluded.updated_at_utc,
    agent_id = excluded.agent_id,
    agent_name = excluded.agent_name,
    agent_role = excluded.agent_role,
    input_tokens = excluded.input_tokens,
    output_tokens = excluded.output_tokens,
    duration_ms = excluded.duration_ms,
    error_message = excluded.error_message" +
            (onlyIfStreaming ? " WHERE messages.status = $streamingStatus;" : ";");
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
        if (onlyIfStreaming)
        {
            command.Parameters.AddWithValue("$streamingStatus", (int)MessageStatus.Streaming);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    internal static async Task UpsertToolExecutionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ToolExecutionRecord record,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, after_segment_index, result_content, source_kind, source_id, display_name)
VALUES($id, $conversationId, $toolName, $argumentsJson, $status, $resultSummary, $correlationId, $durationMs, $createdAt, $updatedAt, $agentId, $messageId, $afterSegmentIndex, $resultContent, $sourceKind, $sourceId, $displayName)
ON CONFLICT(id) DO UPDATE SET
    status = excluded.status,
    result_summary = excluded.result_summary,
    result_content = COALESCE(excluded.result_content, tool_runs.result_content),
    duration_ms = excluded.duration_ms,
    agent_id = COALESCE(excluded.agent_id, tool_runs.agent_id),
    message_id = COALESCE(excluded.message_id, tool_runs.message_id),
    after_segment_index = COALESCE(excluded.after_segment_index, tool_runs.after_segment_index),
    source_kind = COALESCE(excluded.source_kind, tool_runs.source_kind),
    source_id = COALESCE(excluded.source_id, tool_runs.source_id),
    display_name = COALESCE(excluded.display_name, tool_runs.display_name),
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", record.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$toolName", record.ToolName);
        command.Parameters.AddWithValue("$argumentsJson", record.ArgumentsJson);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$resultSummary", record.ResultSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", record.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", record.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", record.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$messageId", record.MessageId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$afterSegmentIndex", record.AfterSegmentIndex ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$resultContent", record.ResultContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", record.SourceKind is null ? DBNull.Value : (int)record.SourceKind.Value);
        command.Parameters.AddWithValue("$sourceId", record.SourceId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$displayName", record.DisplayName ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
