using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal static class SqliteConversationTurnWriter
{
    internal static async Task<ConversationTurnCommit> StartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationTurnStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Conversation.Id != start.Turn.ConversationId || start.Turn.Status != ConversationTurnStatus.Running ||
            start.Turn.Origin == DirectTurnOrigin.Continuation || start.UserMessageId == Guid.Empty || start.UserMessageId == start.Turn.Id)
        {
            throw new ArgumentException("A start requires an owned Running turn and an independent user message.", nameof(start));
        }

        await UpsertConversationAsync(connection, transaction, start.Conversation, cancellationToken).ConfigureAwait(false);
        await InsertTurnAsync(connection, transaction, start.Turn, cancellationToken).ConfigureAwait(false);
        await UpsertUsageAsync(connection, transaction, start.Turn, cancellationToken).ConfigureAwait(false);
        var user = start.InputClaim is { } claim
            ? await ConsumeFollowUpAsync(connection, transaction, start, claim, cancellationToken).ConfigureAwait(false)
            : await InsertUserAsync(connection, transaction, start.Turn, start.UserMessageId,
                start.Prompt, start.Attachments, cancellationToken).ConfigureAwait(false);
        return new ConversationTurnCommit(start.Turn, [user], []);
    }

    private static async Task<MessageRecord> ConsumeFollowUpAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationTurnStart start,
        ConversationInputClaim claim, CancellationToken cancellationToken)
    {
        if (claim.InputId == Guid.Empty || claim.ConversationId != start.Turn.ConversationId ||
            claim.Revision <= 0 || claim.ClaimId == Guid.Empty || claim.OwnerRunId == Guid.Empty ||
            claim.MessageId != ConversationInputMessageId.Compute(claim.ClaimId, claim.InputId) ||
            start.UserMessageId != claim.MessageId || start.Turn.ExecutionMode != AgentExecutionMode.Direct ||
            start.Turn.Origin != DirectTurnOrigin.Interactive)
        {
            throw new ArgumentException("A follow-up start requires a matching, stable input claim.", nameof(start));
        }

        var at = start.Turn.StartedAtUtc;
        var user = await InsertUserAsync(connection, transaction, start.Turn, claim.MessageId,
            start.Prompt, start.Attachments, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_inputs SET status = 2, consumed_turn_id = $turnId, message_id = $messageId,
                consumed_at_utc = $at, updated_at_utc = $at, revision = revision + 1
            WHERE id = $id AND conversation_id = $conversationId AND kind = 0 AND status = 1 AND revision = $revision
                AND claim_id = $claimId AND claim_owner_run_id = $owner;
            """;
        command.Parameters.AddWithValue("$turnId", start.Turn.Id.ToString("D"));
        command.Parameters.AddWithValue("$messageId", claim.MessageId.ToString("D"));
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$id", claim.InputId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", start.Turn.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", claim.Revision);
        command.Parameters.AddWithValue("$claimId", claim.ClaimId.ToString("D"));
        command.Parameters.AddWithValue("$owner", claim.OwnerRunId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The follow-up claim no longer permits consumption.");
        }

        await using var revision = connection.CreateCommand();
        revision.Transaction = transaction;
        revision.CommandText = "UPDATE conversation_input_state SET queue_revision = queue_revision + 1 WHERE conversation_id = $id;";
        revision.Parameters.AddWithValue("$id", start.Turn.ConversationId.ToString("D"));
        await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    internal static async Task<long> ReserveSequenceAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_message_sequences(conversation_id, next_sequence)
            VALUES($conversationId, 2)
            ON CONFLICT(conversation_id) DO UPDATE SET next_sequence = next_sequence + 1
            RETURNING next_sequence - 1;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Message sequence allocation returned no value."));
    }

    internal static async Task<MessageRecord> InsertUserAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationTurnRecord turn,
        Guid messageId,
        string prompt,
        IReadOnlyList<MessageAttachmentRecord>? attachments,
        CancellationToken cancellationToken,
        DateTimeOffset? createdAtUtc = null)
    {
        var sequence = await ReserveSequenceAsync(connection, transaction, turn.ConversationId, cancellationToken).ConfigureAwait(false);
        var at = createdAtUtc ?? turn.StartedAtUtc;
        var message = new MessageRecord(messageId, turn.ConversationId, turn.Id, sequence, MessageRole.User,
            prompt, MessageStatus.Sealed, at, at, Attachments: attachments);
        await WriteMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        await ReplaceAttachmentsAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    internal static async Task<bool> TryWriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConversationTurnCommit commit,
        CancellationToken cancellationToken,
        bool allowInsert = false)
    {
        ValidateCommit(commit);
        var current = await ReadTurnAsync(connection, transaction, commit.Turn.Id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            if (!allowInsert)
            {
                return false;
            }

            await InsertTurnAsync(connection, transaction, commit.Turn, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ValidateTurnIdentity(current, commit.Turn);
            if (current.Status != ConversationTurnStatus.Running)
            {
                return false;
            }

            await UpdateTurnAsync(connection, transaction, commit.Turn, cancellationToken).ConfigureAwait(false);
        }

        await WriteContentAsync(connection, transaction, commit, cancellationToken).ConfigureAwait(false);
        await UpsertUsageAsync(connection, transaction, commit.Turn, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static async Task<ConversationTurnRecord?> ReadTurnAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid turnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SqliteMappings.TurnSelectColumns}
            FROM conversation_turns t LEFT JOIN turn_usage u ON u.turn_id = t.id
            WHERE t.id = $turnId;
            """;
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? SqliteMappings.ReadTurn(reader) : null;
    }

    internal static async Task InsertTurnAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationTurnRecord turn, CancellationToken cancellationToken)
    {
        ValidateTurn(turn);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc, completed_at_utc, error_message)
            VALUES($id, $conversationId, $executionMode, $origin, $status, $startedAt, $completedAt, $error);
            """;
        AddTurnParameters(command, turn);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UpsertConversationAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationRecord conversation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
                channel_kind, channel_conversation_id, channel_display_name, created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($id, $title, $workspaceRootId, $mode, $permission, $agentId, $channelKind, $channelId, $channelName,
                $createdAt, $updatedAt, $kind, $parent)
            ON CONFLICT(id) DO UPDATE SET title = excluded.title, workspace_root_id = excluded.workspace_root_id,
                mode = excluded.mode, tool_permission_mode = excluded.tool_permission_mode, agent_id = excluded.agent_id,
                channel_kind = excluded.channel_kind, channel_conversation_id = excluded.channel_conversation_id,
                channel_display_name = excluded.channel_display_name, updated_at_utc = excluded.updated_at_utc
            WHERE conversations.kind = excluded.kind AND conversations.parent_conversation_id IS excluded.parent_conversation_id;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$workspaceRootId", conversation.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$permission", (int)conversation.ToolPermissionMode);
        command.Parameters.AddWithValue("$agentId", conversation.AgentId);
        command.Parameters.AddWithValue("$channelKind", conversation.ChannelKind ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelId", conversation.ChannelConversationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelName", conversation.ChannelDisplayName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$kind", (int)conversation.Kind);
        command.Parameters.AddWithValue("$parent", conversation.ParentConversationId?.ToString("D") ?? (object)DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("Conversation ownership cannot be changed.");
        }
    }

    private static async Task WriteContentAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationTurnCommit commit, CancellationToken cancellationToken)
    {
        foreach (var message in commit.Messages)
        {
            await WriteMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        }

        foreach (var tool in commit.ToolExecutions)
        {
            await WriteToolAsync(connection, transaction, tool, cancellationToken).ConfigureAwait(false);
        }

        foreach (var message in commit.Messages)
        {
            await ReplaceSegmentsAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
            await ReplaceAttachmentsAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task UpdateTurnAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationTurnRecord turn, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_turns SET status = $status, completed_at_utc = $completedAt, error_message = $error
            WHERE id = $id AND conversation_id = $conversationId AND status = 0;
            """;
        AddTurnParameters(command, turn);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The Running turn changed during its transaction.");
        }
    }

    private static async Task WriteMessageAsync(
        SqliteConnection connection, SqliteTransaction transaction, MessageRecord message, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(id, conversation_id, turn_id, sequence, role, markdown_content, status,
                created_at_utc, updated_at_utc, agent_id, agent_name, agent_role)
            SELECT $id, $conversationId, $turnId, $sequence, $role, $content, $status,
                $createdAt, $updatedAt, $agentId, $agentName, $agentRole
            WHERE EXISTS (SELECT 1 FROM conversation_message_sequences WHERE conversation_id = $conversationId AND next_sequence > $sequence)
            ON CONFLICT(id) DO UPDATE SET markdown_content = excluded.markdown_content, status = excluded.status,
                updated_at_utc = excluded.updated_at_utc, agent_id = excluded.agent_id,
                agent_name = excluded.agent_name, agent_role = excluded.agent_role
            WHERE messages.status = 1 AND messages.conversation_id = excluded.conversation_id
                AND messages.turn_id = excluded.turn_id AND messages.sequence = excluded.sequence AND messages.role = excluded.role;
            """;
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$turnId", message.TurnId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", message.Sequence);
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$content", message.MarkdownContent);
        command.Parameters.AddWithValue("$status", (int)message.Status);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", message.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", message.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentName", message.AgentName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentRole", message.AgentRole ?? (object)DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A fragment is sealed, has changed ownership, or uses an unreserved sequence.");
        }
    }

    private static async Task UpsertUsageAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationTurnRecord turn, CancellationToken cancellationToken)
    {
        if (turn.Usage is not { } usage)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO turn_usage(turn_id, conversation_id, model, input_tokens, uncached_input_tokens, cached_input_tokens,
                cache_write_input_tokens, output_tokens, reasoning_tokens, total_tokens, provider_calls,
                context_tokens, context_window_tokens, cost_usd_micros, cost_source, additional_counts_json, created_at_utc)
            VALUES($turnId, $conversationId, $model, $input, $uncached, $cached, $cacheWrite, $output, $reasoning,
                $total, $calls, $context, $window, $cost, $costSource, $additional, $createdAt)
            ON CONFLICT(turn_id) DO UPDATE SET model = excluded.model, input_tokens = excluded.input_tokens,
                uncached_input_tokens = excluded.uncached_input_tokens, cached_input_tokens = excluded.cached_input_tokens,
                cache_write_input_tokens = excluded.cache_write_input_tokens, output_tokens = excluded.output_tokens,
                reasoning_tokens = excluded.reasoning_tokens, total_tokens = excluded.total_tokens, provider_calls = excluded.provider_calls,
                context_tokens = excluded.context_tokens, context_window_tokens = excluded.context_window_tokens,
                cost_usd_micros = excluded.cost_usd_micros, cost_source = excluded.cost_source,
                additional_counts_json = excluded.additional_counts_json;
            """;
        command.Parameters.AddWithValue("$turnId", turn.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", turn.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$model", usage.Model ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$input", usage.InputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$uncached", usage.UncachedInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cached", usage.CachedInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cacheWrite", usage.CacheWriteInputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$output", usage.OutputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$reasoning", usage.ReasoningTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$total", usage.TotalTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$calls", usage.ProviderCalls);
        command.Parameters.AddWithValue("$context", usage.ContextTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$window", usage.ContextWindowTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cost", usage.CostUsdMicros ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$costSource", (int)usage.CostSource);
        command.Parameters.AddWithValue("$additional", usage.AdditionalCountsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", turn.StartedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteToolAsync(
        SqliteConnection connection, SqliteTransaction transaction, ToolExecutionRecord tool, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms,
                created_at_utc, updated_at_utc, agent_id, message_id, result_content, source_kind, source_id, display_name,
                effective_arguments_json, hook_feedback_json, hook_outcome_json)
            VALUES($id, $conversationId, $name, $arguments, $status, $summary, $correlation, $duration,
                $createdAt, $updatedAt, $agentId, $messageId, $content, $sourceKind, $sourceId, $displayName,
                $effectiveArguments, $feedback, $outcome)
            ON CONFLICT(id) DO UPDATE SET status = excluded.status, result_summary = excluded.result_summary,
                result_content = COALESCE(excluded.result_content, tool_runs.result_content), duration_ms = excluded.duration_ms,
                updated_at_utc = excluded.updated_at_utc, effective_arguments_json = COALESCE(excluded.effective_arguments_json, tool_runs.effective_arguments_json),
                hook_feedback_json = COALESCE(excluded.hook_feedback_json, tool_runs.hook_feedback_json),
                hook_outcome_json = COALESCE(excluded.hook_outcome_json, tool_runs.hook_outcome_json)
            WHERE tool_runs.message_id = excluded.message_id AND tool_runs.conversation_id = excluded.conversation_id
                AND tool_runs.tool_name = excluded.tool_name AND tool_runs.arguments_json = excluded.arguments_json;
            """;
        command.Parameters.AddWithValue("$id", tool.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", tool.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$name", tool.ToolName);
        command.Parameters.AddWithValue("$arguments", tool.ArgumentsJson);
        command.Parameters.AddWithValue("$status", (int)tool.Status);
        command.Parameters.AddWithValue("$summary", tool.ResultSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlation", tool.CorrelationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$duration", tool.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", tool.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", tool.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", tool.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$messageId", tool.MessageId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$content", tool.ResultContent ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$sourceKind", tool.SourceKind is { } kind ? (int)kind : DBNull.Value);
        command.Parameters.AddWithValue("$sourceId", tool.SourceId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$displayName", tool.DisplayName ?? (object)DBNull.Value);
        var hooks = ToolHookOutcomeColumns.Split(tool.HookOutcome);
        command.Parameters.AddWithValue("$effectiveArguments", hooks.EffectiveArgumentsJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$feedback", hooks.FeedbackJson ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outcome", hooks.OutcomeJson ?? (object)DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("A tool cannot change its fragment or call identity.");
        }
    }

    private static async Task ReplaceSegmentsAsync(
        SqliteConnection connection, SqliteTransaction transaction, MessageRecord message, CancellationToken cancellationToken)
    {
        if (message.Segments is not { } segments)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM message_segments WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var segment in segments)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO message_segments(message_id, ordinal, kind, text, tool_run_id) VALUES($id, $ordinal, $kind, $text, $toolId);";
            command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", segment.Ordinal);
            command.Parameters.AddWithValue("$kind", (int)segment.Kind);
            command.Parameters.AddWithValue("$text", segment.Text ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$toolId", segment.ToolRunId?.ToString("D") ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceAttachmentsAsync(
        SqliteConnection connection, SqliteTransaction transaction, MessageRecord message, CancellationToken cancellationToken)
    {
        if (message.Attachments is not { } attachments)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM message_attachments WHERE message_id = $id;";
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        foreach (var attachment in attachments)
        {
            if (attachment.MessageId != message.Id)
            {
                throw new ArgumentException("An attachment must belong to its message.", nameof(message));
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO message_attachments(id, message_id, kind, file_name, media_type, storage_path, byte_length, created_at_utc)
                VALUES($id, $messageId, $kind, $name, $mediaType, $path, $length, $createdAt);
                """;
            command.Parameters.AddWithValue("$id", attachment.Id.ToString("D"));
            command.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)attachment.Kind);
            command.Parameters.AddWithValue("$name", attachment.FileName);
            command.Parameters.AddWithValue("$mediaType", attachment.MediaType);
            command.Parameters.AddWithValue("$path", attachment.StoragePath);
            command.Parameters.AddWithValue("$length", attachment.ByteLength);
            command.Parameters.AddWithValue("$createdAt", attachment.CreatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateCommit(ConversationTurnCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateTurn(commit.Turn);
        if (commit.Messages.Select(message => message.Id).Distinct().Count() != commit.Messages.Count ||
            commit.ToolExecutions.Select(tool => tool.Id).Distinct().Count() != commit.ToolExecutions.Count)
        {
            throw new ArgumentException("A commit cannot contain duplicate fragments or tools.", nameof(commit));
        }

        foreach (var message in commit.Messages)
        {
            if (message.Id == Guid.Empty || message.Id == commit.Turn.Id || message.ConversationId != commit.Turn.ConversationId ||
                message.TurnId != commit.Turn.Id || message.Role != MessageRole.Assistant || message.Sequence <= 0 ||
                (commit.Turn.Status != ConversationTurnStatus.Running && message.Status == MessageStatus.Streaming) ||
                (message.Segments?.Any(segment => segment.MessageId != message.Id) ?? false))
            {
                throw new ArgumentException("A commit requires actual assistant fragments owned by its logical turn.", nameof(commit));
            }
        }

        var calls = commit.Messages.SelectMany(message => message.Segments ?? [])
            .Where(segment => segment.Kind == MessageSegmentKind.ToolCall).ToArray();
        foreach (var tool in commit.ToolExecutions)
        {
            if (tool.ConversationId != commit.Turn.ConversationId || tool.MessageId is not { } messageId ||
                !commit.Messages.Any(message => message.Id == messageId) ||
                calls.Count(segment => segment.MessageId == messageId && segment.ToolRunId == tool.Id) != 1)
            {
                throw new ArgumentException("Every tool must be committed with its actual fragment and ToolCall block.", nameof(commit));
            }
        }

        if (calls.Any(call => !commit.ToolExecutions.Any(tool => tool.Id == call.ToolRunId && tool.MessageId == call.MessageId)))
        {
            throw new ArgumentException("Every ToolCall block requires its owned tool in the same commit.", nameof(commit));
        }
    }

    private static void ValidateTurn(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Id == Guid.Empty || turn.ConversationId == Guid.Empty || !Enum.IsDefined(turn.Status) ||
            !Enum.IsDefined(turn.ExecutionMode) || !Enum.IsDefined(turn.Origin) ||
            (turn.Status == ConversationTurnStatus.Running) != (turn.CompletedAtUtc is null))
        {
            throw new ArgumentException("A turn requires valid ownership, execution identity and lifecycle timestamps.", nameof(turn));
        }
    }

    private static void ValidateTurnIdentity(ConversationTurnRecord current, ConversationTurnRecord next)
    {
        if (current.ConversationId != next.ConversationId || current.ExecutionMode != next.ExecutionMode ||
            current.Origin != next.Origin || current.StartedAtUtc != next.StartedAtUtc)
        {
            throw new ArgumentException("The logical turn identity cannot change.", nameof(next));
        }
    }

    private static void AddTurnParameters(SqliteCommand command, ConversationTurnRecord turn)
    {
        command.Parameters.AddWithValue("$id", turn.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", turn.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$executionMode", (int)turn.ExecutionMode);
        command.Parameters.AddWithValue("$origin", (int)turn.Origin);
        command.Parameters.AddWithValue("$status", (int)turn.Status);
        command.Parameters.AddWithValue("$startedAt", turn.StartedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", turn.CompletedAtUtc?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", turn.ErrorMessage ?? (object)DBNull.Value);
    }
}
