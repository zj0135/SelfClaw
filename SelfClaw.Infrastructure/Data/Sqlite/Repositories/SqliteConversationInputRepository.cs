using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed partial class SqliteConversationInputRepository : IConversationInputRepository, IConversationInputStore, IConversationInputSchedulerStore
{
    internal const int MaximumInputsPerConversation = 20;
    internal const int MaximumInputsPerApplication = 500;
    internal const int MaximumPromptBytes = 64 * 1024;
    internal const int MaximumPreviewLength = 200;

    private const string RecordSelectColumns = """
        i.id, i.conversation_id, i.client_request_id, i.sequence, i.kind, i.target_turn_id, i.status, i.revision,
        i.payload_json, i.execution_snapshot_json, i.claim_id, i.claim_owner_run_id, i.consumed_turn_id, i.message_id,
        i.created_at_utc, i.updated_at_utc, i.consumed_at_utc, i.reason_code, i.error_message
        """;

    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions PayloadJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SqliteDatabase _database;
    private readonly Func<string, CancellationToken, Task>? _beforeCommit;

    public SqliteConversationInputRepository(SqliteDatabase database)
    {
        _database = database;
    }

    internal SqliteConversationInputRepository(SqliteDatabase database, Func<string, CancellationToken, Task> beforeCommit)
        : this(database)
    {
        _beforeCommit = beforeCommit;
    }

    public async Task<bool> HasUnprocessedInputsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM conversation_inputs WHERE conversation_id = $id AND status IN (0, 1, 3));";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
    }


    public async Task<ConversationInputAcceptResult> AcceptAsync(
        ConversationInputAcceptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAccept(request);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        if (await ReadByIdentityAsync(connection, transaction, request.ConversationId, request.ClientRequestId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            var duplicate = existing.Kind == request.Kind &&
                (existing.TargetTurnId ?? Guid.Empty) == (request.TargetTurnId ?? Guid.Empty) &&
                (existing.RequestPrompt ?? existing.Prompt) == request.Prompt &&
                (request.RequestFingerprint is not null
                    ? existing.RequestFingerprint == request.RequestFingerprint
                    : SnapshotEquals(existing.ExecutionSnapshot, request.ExecutionSnapshot));
            var revision = await ReadQueueRevisionAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ConversationInputAcceptResult(
                duplicate ? ConversationInputAcceptStatus.Duplicate : ConversationInputAcceptStatus.Conflict,
                duplicate ? existing : null,
                revision,
                duplicate ? null : "request-conflict");
        }

        if (request.Conversation is { } conversation)
        {
            await EnsureConversationAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
        }

        var state = await ReadStateAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (!request.QueueEnabled && state.UnprocessedCount > 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ConversationInputAcceptResult(ConversationInputAcceptStatus.Rejected, null,
                state.QueueRevision, ConversationInputReason.QueueDisabled);
        }

        var globalUnprocessed = await CountGlobalUnprocessedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (state.UnprocessedCount >= MaximumInputsPerConversation || globalUnprocessed >= MaximumInputsPerApplication)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ConversationInputAcceptResult(ConversationInputAcceptStatus.Full, null,
                state.QueueRevision, ConversationInputReason.Capacity);
        }

        var sequence = await AllocateSequenceAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var inputId = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conversation_inputs(id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                    payload_json, execution_snapshot_json, status, revision, claim_id, claim_owner_run_id,
                    consumed_turn_id, message_id, created_at_utc, updated_at_utc, consumed_at_utc, reason_code, error_message)
                VALUES($id, $conversationId, $request, $sequence, $kind, $target, $payload, $snapshot, 0, 1,
                    NULL, NULL, NULL, NULL, $at, $at, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$id", inputId.ToString("D"));
            insert.Parameters.AddWithValue("$conversationId", request.ConversationId.ToString("D"));
            insert.Parameters.AddWithValue("$request", request.ClientRequestId);
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$kind", (int)request.Kind);
            insert.Parameters.AddWithValue("$target", request.TargetTurnId?.ToString("D") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$payload", SerializePayload(request.Prompt, request.RequestFingerprint));
            insert.Parameters.AddWithValue("$snapshot", request.ExecutionSnapshot is { } snapshot ? SerializeSnapshot(snapshot) : (object)DBNull.Value);
            insert.Parameters.AddWithValue("$at", now.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var record = await ReadByIdAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The accepted input disappeared within its transaction.");
        var queueRevision = await ReadQueueRevisionAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationInputAcceptResult(ConversationInputAcceptStatus.Accepted, record, queueRevision);
    }

    public async Task<ConversationInputUpdateResult> EditAsync(
        ConversationInputEditRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Prompt.Length == 0 || Encoding.UTF8.GetByteCount(request.Prompt) > MaximumPromptBytes)
        {
            return new ConversationInputUpdateResult(ConversationInputUpdateStatus.Invalid, null, 0);
        }

        return await ConditionalUpdateAsync(request.InputId, request.ExpectedRevision, """
            UPDATE conversation_inputs SET payload_json = json_set(payload_json, '$.prompt', $prompt), status = 0, reason_code = NULL, error_message = NULL,
                revision = revision + 1, updated_at_utc = $at
            WHERE id = $id AND revision = $revision AND status IN (0, 3);
            """, ("$prompt", request.Prompt), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputUpdateResult> CancelAsync(
        ConversationInputCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ConditionalUpdateAsync(request.InputId, request.ExpectedRevision, """
            UPDATE conversation_inputs SET status = 4, revision = revision + 1, updated_at_utc = $at
            WHERE id = $id AND revision = $revision AND status IN (0, 3);
            """, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputUpdateResult> HoldAsync(
        Guid inputId, int expectedRevision, string reasonCode, string? errorMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        return await ConditionalUpdateAsync(inputId, expectedRevision, """
            UPDATE conversation_inputs SET status = 3, reason_code = $reason, error_message = $error,
                revision = revision + 1, updated_at_utc = $at
            WHERE id = $id AND revision = $revision AND status IN (0, 1);
            """, ("$reason", reasonCode), ("$error", errorMessage ?? (object)DBNull.Value), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputUpdateResult> RetryAsync(
        Guid inputId, int expectedRevision, CancellationToken cancellationToken = default,
        ConversationInputExecutionSnapshot? executionSnapshot = null)
    {
        return await ConditionalUpdateAsync(inputId, expectedRevision, """
            UPDATE conversation_inputs SET status = 0, reason_code = NULL, error_message = NULL,
                execution_snapshot_json = COALESCE($snapshot, execution_snapshot_json),
                revision = revision + 1, updated_at_utc = $at
            WHERE id = $id AND revision = $revision AND status = 3;
            """, ("$snapshot", executionSnapshot is null ? DBNull.Value : SerializeSnapshot(executionSnapshot)), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputRecord?> FindRequestAsync(Guid conversationId, string clientRequestId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadByIdentityAsync(connection, transaction, conversationId, clientRequestId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputRecord?> GetInputAsync(Guid inputId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadByIdAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputQueueState> GetQueueStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadQueueStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputQueueState> SetPausedAsync(
        ConversationInputPauseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_input_state SET paused = $paused, pause_reason = $reason,
                    queue_revision = queue_revision + 1
                WHERE conversation_id = $id AND queue_revision = $revision;
                """;
            command.Parameters.AddWithValue("$paused", request.Paused ? 1 : 0);
            command.Parameters.AddWithValue("$reason", request.Paused ? (request.Reason ?? ConversationInputReason.QueuePaused) : (object)DBNull.Value);
            command.Parameters.AddWithValue("$id", request.ConversationId.ToString("D"));
            command.Parameters.AddWithValue("$revision", request.ExpectedQueueRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return await ReadQueueStateAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
            }
        }

        var state = await ReadQueueStateAsync(connection, transaction, request.ConversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }


    private async Task<ConversationInputUpdateResult> ConditionalUpdateAsync(
        Guid inputId, int expectedRevision, string sql,
        (string Name, object Value)? extra1 = null, (string Name, object Value)? extra2 = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var conversationId = await ReadConversationIdAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false);
        if (conversationId is null)
        {
            return new ConversationInputUpdateResult(ConversationInputUpdateStatus.Missing, null, 0);
        }

        int changed;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", inputId.ToString("D"));
            command.Parameters.AddWithValue("$revision", expectedRevision);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (extra1 is { } first) command.Parameters.AddWithValue(first.Name, first.Value);
            if (extra2 is { } second) command.Parameters.AddWithValue(second.Name, second.Value);
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed != 1)
        {
            return new ConversationInputUpdateResult(ConversationInputUpdateStatus.Conflict, null,
                await ReadQueueRevisionAsync(connection, transaction, conversationId.Value, cancellationToken).ConfigureAwait(false));
        }

        await AdvanceQueueRevisionAsync(connection, transaction, conversationId.Value, cancellationToken).ConfigureAwait(false);
        var record = await ReadByIdAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false);
        var revision = await ReadQueueRevisionAsync(connection, transaction, conversationId.Value, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationInputUpdateResult(ConversationInputUpdateStatus.Applied, record, revision);
    }

    private static async Task EnsureConversationAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationRecord conversation, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
                channel_kind, channel_conversation_id, channel_display_name, created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($id, $title, $workspaceRootId, $mode, $permission, $agentId, $channelKind, $channelId, $channelName,
                $createdAt, $updatedAt, $kind, $parent)
            ON CONFLICT(id) DO NOTHING;
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
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> AllocateSequenceAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_input_state(conversation_id, next_input_sequence, queue_revision, paused, pause_reason)
            VALUES($id, 2, 1, 0, NULL)
            ON CONFLICT(conversation_id) DO UPDATE SET next_input_sequence = next_input_sequence + 1,
                queue_revision = queue_revision + 1
            RETURNING next_input_sequence - 1;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Input sequence allocation returned no value."));
    }

    private static async Task AdvanceQueueRevisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_input_state(conversation_id, next_input_sequence, queue_revision, paused, pause_reason)
            VALUES($id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM conversation_inputs WHERE conversation_id = $id), 1, 0, NULL)
            ON CONFLICT(conversation_id) DO UPDATE SET queue_revision = queue_revision + 1;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateAccept(ConversationInputAcceptRequest request)
    {
        if (request.ConversationId == Guid.Empty)
            throw new ArgumentException("An input requires a conversation id.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        if (request.Kind != ConversationInputKind.FollowUp || request.ExecutionSnapshot?.Mode != AgentExecutionMode.Direct)
            throw new ArgumentException("Only Direct follow-ups can be accepted.", nameof(request));
        if (request.ClientRequestId.Length > 128)
            throw new ArgumentException("The client request id is too long.", nameof(request));
        if (request.Kind == ConversationInputKind.FollowUp && request.TargetTurnId is not null)
            throw new ArgumentException("A follow-up cannot target a turn.", nameof(request));
        if (request.Kind == ConversationInputKind.Steer && request.TargetTurnId is null)
            throw new ArgumentException("A steer requires a target turn.", nameof(request));
        if (request.Kind == ConversationInputKind.FollowUp && request.ExecutionSnapshot is null)
            throw new ArgumentException("A follow-up requires an execution snapshot.", nameof(request));
        if (request.Prompt.Length == 0)
            throw new ArgumentException("An input prompt cannot be empty.", nameof(request));
        if (Encoding.UTF8.GetByteCount(request.Prompt) > MaximumPromptBytes)
            throw new ArgumentException("The input prompt exceeds the maximum size.", nameof(request));
    }
}
