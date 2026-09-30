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

        if (finalization.AssistantMessage.Segments is not null)
        {
            await ReplaceMessageSegmentsAsync(
                    connection,
                    transaction,
                    finalization.AssistantMessage,
                    cancellationToken)
                .ConfigureAwait(false);
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
INSERT INTO messages(id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc, agent_id, agent_name, agent_role, duration_ms, error_message)
VALUES($id, $conversationId, $role, $markdownContent, $status, $createdAt, $updatedAt, $agentId, $agentName, $agentRole, $durationMs, $errorMessage)
ON CONFLICT(id) DO UPDATE SET
    markdown_content = excluded.markdown_content,
    status = excluded.status,
    updated_at_utc = excluded.updated_at_utc,
    agent_id = excluded.agent_id,
    agent_name = excluded.agent_name,
    agent_role = excluded.agent_role,
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
        command.Parameters.AddWithValue("$durationMs", message.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", message.ErrorMessage ?? (object)DBNull.Value);
        if (onlyIfStreaming)
        {
            command.Parameters.AddWithValue("$streamingStatus", (int)MessageStatus.Streaming);
        }

        var written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        if (written && message.Usage is not null)
        {
            await UpsertTurnUsageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private static async Task UpsertTurnUsageAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        var usage = message.Usage!;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
INSERT INTO turn_usage(
    message_id, conversation_id, model, input_tokens, uncached_input_tokens, cached_input_tokens,
    cache_write_input_tokens, output_tokens, reasoning_tokens, total_tokens, provider_calls,
    context_tokens, context_window_tokens, cost_usd_micros, cost_source, additional_counts_json, created_at_utc)
VALUES(
    $messageId, $conversationId, $model, $inputTokens, $uncachedInputTokens, $cachedInputTokens,
    $cacheWriteInputTokens, $outputTokens, $reasoningTokens, $totalTokens, $providerCalls,
    $contextTokens, $contextWindowTokens, $costUsdMicros, $costSource, $additionalCountsJson, $createdAtUtc)
ON CONFLICT(message_id) DO UPDATE SET
    conversation_id = excluded.conversation_id,
    model = excluded.model,
    input_tokens = excluded.input_tokens,
    uncached_input_tokens = excluded.uncached_input_tokens,
    cached_input_tokens = excluded.cached_input_tokens,
    cache_write_input_tokens = excluded.cache_write_input_tokens,
    output_tokens = excluded.output_tokens,
    reasoning_tokens = excluded.reasoning_tokens,
    total_tokens = excluded.total_tokens,
    provider_calls = excluded.provider_calls,
    context_tokens = excluded.context_tokens,
    context_window_tokens = excluded.context_window_tokens,
    cost_usd_micros = excluded.cost_usd_micros,
    cost_source = excluded.cost_source,
    additional_counts_json = excluded.additional_counts_json;";
        command.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$model", usage.Model ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", usage.InputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$uncachedInputTokens", usage.UncachedInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cachedInputTokens", usage.CachedInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cacheWriteInputTokens", usage.CacheWriteInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", usage.OutputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$reasoningTokens", usage.ReasoningTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$totalTokens", usage.TotalTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$providerCalls", usage.ProviderCalls);
        command.Parameters.AddWithValue("$contextTokens", usage.ContextTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$contextWindowTokens", usage.ContextWindowTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$costUsdMicros", usage.CostUsdMicros ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$costSource", (int)usage.CostSource);
        command.Parameters.AddWithValue("$additionalCountsJson", usage.AdditionalCountsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", message.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, result_content, source_kind, source_id, display_name, effective_arguments_json, hook_feedback_json, hook_outcome_json)
VALUES($id, $conversationId, $toolName, $argumentsJson, $status, $resultSummary, $correlationId, $durationMs, $createdAt, $updatedAt, $agentId, $messageId, $resultContent, $sourceKind, $sourceId, $displayName, $effectiveArgumentsJson, $hookFeedbackJson, $hookOutcomeJson)
ON CONFLICT(id) DO UPDATE SET
    status = excluded.status,
    result_summary = excluded.result_summary,
    result_content = COALESCE(excluded.result_content, tool_runs.result_content),
    duration_ms = excluded.duration_ms,
    agent_id = COALESCE(excluded.agent_id, tool_runs.agent_id),
    message_id = COALESCE(excluded.message_id, tool_runs.message_id),
    source_kind = COALESCE(excluded.source_kind, tool_runs.source_kind),
    source_id = COALESCE(excluded.source_id, tool_runs.source_id),
    display_name = COALESCE(excluded.display_name, tool_runs.display_name),
    effective_arguments_json = COALESCE(excluded.effective_arguments_json, tool_runs.effective_arguments_json),
    hook_feedback_json = COALESCE(excluded.hook_feedback_json, tool_runs.hook_feedback_json),
    hook_outcome_json = COALESCE(excluded.hook_outcome_json, tool_runs.hook_outcome_json),
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
        command.Parameters.AddWithValue("$resultContent", record.ResultContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", record.SourceKind is null ? DBNull.Value : (int)record.SourceKind.Value);
        command.Parameters.AddWithValue("$sourceId", record.SourceId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$displayName", record.DisplayName ?? (object)DBNull.Value);
        var hookColumns = ToolHookOutcomeColumns.Split(record.HookOutcome);
        command.Parameters.AddWithValue("$effectiveArgumentsJson", hookColumns.EffectiveArgumentsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$hookFeedbackJson", hookColumns.FeedbackJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$hookOutcomeJson", hookColumns.OutcomeJson ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceMessageSegmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM message_segments WHERE message_id = $messageId;";
            deleteCommand.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var segment in message.Segments!)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = @"
INSERT INTO message_segments(message_id, ordinal, kind, text, tool_run_id)
VALUES($messageId, $ordinal, $kind, $text, $toolRunId);";
            insertCommand.Parameters.AddWithValue("$messageId", segment.MessageId.ToString("D"));
            insertCommand.Parameters.AddWithValue("$ordinal", segment.Ordinal);
            insertCommand.Parameters.AddWithValue("$kind", (int)segment.Kind);
            insertCommand.Parameters.AddWithValue("$text", (object?)segment.Text ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("$toolRunId", segment.ToolRunId.HasValue
                ? segment.ToolRunId.Value.ToString("D")
                : DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
