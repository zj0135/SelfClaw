using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteConversationInputRepository : IConversationInputRepository
{
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

    public async Task<ConversationInputBatch?> ReadClaimedBatchAsync(
        Guid conversationId, Guid turnId, Guid ownerRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.claim_id FROM conversation_inputs i
            JOIN conversation_turns t ON t.id = i.target_turn_id AND t.conversation_id = i.conversation_id
            WHERE i.conversation_id = $conversationId AND i.target_turn_id = $turnId
                AND i.claim_owner_run_id = $owner AND i.status = 1 AND i.kind = 1
                AND t.status = 0 AND t.execution_mode = 0 AND t.origin = 0
            ORDER BY i.sequence LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        command.Parameters.AddWithValue("$owner", ownerRunId.ToString("D"));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string claim)
        {
            return null;
        }

        var claimId = Guid.Parse(claim);
        var rows = await ReadClaimAsync(connection, transaction, claimId, cancellationToken).ConfigureAwait(false);
        var batch = new ConversationInputBatch(conversationId, turnId, ownerRunId, claimId,
            rows.Select(row => new ConversationInputItem(row.Id, row.Sequence, row.Revision,
                CreateMessageId(claimId, row.Id), row.Prompt)).ToArray());
        ValidateRows(batch, rows);
        if (rows.Any(row => row.Status != 1))
        {
            throw new InvalidOperationException("A claimed input batch must have one consistent durable state.");
        }

        return batch;
    }

    public async Task<ConversationInputConsumption?> ReadConsumptionAsync(
        ConversationInputBatch batch, CancellationToken cancellationToken = default)
    {
        ValidateBatch(batch);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var rows = await ReadClaimAsync(connection, transaction, batch.ClaimId, cancellationToken).ConfigureAwait(false);
        ValidateRows(batch, rows);
        return await ReadConsumptionAsync(connection, transaction, batch, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationInputConsumption> CommitBoundaryAsync(
        ConversationInputBoundaryCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateBoundary(commit);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var rows = await ReadClaimAsync(connection, transaction, commit.Batch.ClaimId, cancellationToken).ConfigureAwait(false);
        ValidateRows(commit.Batch, rows);
        var consumed = await ReadConsumptionAsync(connection, transaction, commit.Batch, rows, cancellationToken).ConfigureAwait(false);
        if (consumed is not null)
        {
            return consumed;
        }

        if (rows.Any(row => row.Status != 1) ||
            !await SqliteConversationTurnWriter.TryWriteAsync(connection, transaction, commit.Content, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The input claim or its Running turn no longer permits consumption.");
        }

        var messages = await ConsumeInputsAsync(connection, transaction, commit, cancellationToken).ConfigureAwait(false);
        await AdvanceQueueRevisionAsync(connection, transaction, commit.Batch.ConversationId, cancellationToken).ConfigureAwait(false);
        var committedTurn = await SqliteConversationTurnWriter.ReadTurnAsync(connection, transaction, commit.Batch.TurnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The consumed turn disappeared within its transaction.");
        if (_beforeCommit is not null)
        {
            await _beforeCommit("boundary-before-commit", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationInputConsumption(commit.Batch.ClaimId, committedTurn, messages);
    }

    public async Task<bool> HasUnprocessedInputsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM conversation_inputs WHERE conversation_id = $id AND status IN (0, 1, 3));";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
    }

    private static async Task<IReadOnlyList<MessageRecord>> ConsumeInputsAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationInputBoundaryCommit commit, CancellationToken cancellationToken)
    {
        var messages = new List<MessageRecord>();
        var at = DateTimeOffset.UtcNow;
        foreach (var input in commit.Batch.Inputs)
        {
            var message = await SqliteConversationTurnWriter.InsertUserAsync(connection, transaction, commit.Content.Turn,
                input.MessageId, input.Prompt, null, cancellationToken, at).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_inputs SET status = 2, consumed_turn_id = $turnId, message_id = $messageId,
                    consumed_at_utc = $at, updated_at_utc = $at
                WHERE id = $id AND conversation_id = $conversationId AND target_turn_id = $turnId AND kind = 1
                    AND status = 1 AND revision = $revision AND claim_id = $claimId AND claim_owner_run_id = $owner;
                """;
            command.Parameters.AddWithValue("$turnId", commit.Batch.TurnId.ToString("D"));
            command.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            command.Parameters.AddWithValue("$at", at.ToString("O"));
            command.Parameters.AddWithValue("$id", input.InputId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", commit.Batch.ConversationId.ToString("D"));
            command.Parameters.AddWithValue("$revision", input.Revision);
            command.Parameters.AddWithValue("$claimId", commit.Batch.ClaimId.ToString("D"));
            command.Parameters.AddWithValue("$owner", commit.Batch.OwnerRunId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The complete input claim must be consumed atomically.");
            }

            messages.Add(message);
        }

        return messages;
    }

    private static async Task<ConversationInputConsumption?> ReadConsumptionAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationInputBatch batch,
        IReadOnlyList<SqliteConversationInputRow> rows, CancellationToken cancellationToken)
    {
        if (rows.All(row => row.Status != 2))
        {
            return null;
        }

        if (rows.Any(row => row.Status != 2 || row.ConsumedTurnId != batch.TurnId || row.MessageId != CreateMessageId(batch.ClaimId, row.Id)))
        {
            throw new InvalidOperationException("Input consumption contains a partial or inconsistent mapping.");
        }

        var turn = await SqliteConversationTurnWriter.ReadTurnAsync(connection, transaction, batch.TurnId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The consumed turn no longer exists.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {SqliteMappings.MessageSelectColumns}
            FROM conversation_inputs i JOIN messages m ON m.id = i.message_id
            WHERE i.claim_id = $claimId ORDER BY m.sequence;
            """;
        command.Parameters.AddWithValue("$claimId", batch.ClaimId.ToString("D"));
        var messages = new List<MessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(SqliteMappings.ReadMessage(reader));
        }

        if (messages.Count != batch.Inputs.Count || messages.Where((message, index) =>
                message.Id != batch.Inputs[index].MessageId || message.TurnId != batch.TurnId ||
                message.ConversationId != batch.ConversationId || message.Role != MessageRole.User ||
                message.Status != MessageStatus.Sealed || message.MarkdownContent != batch.Inputs[index].Prompt).Any())
        {
            throw new InvalidOperationException("The consumed users do not match the ordered claim.");
        }

        return new ConversationInputConsumption(batch.ClaimId, turn, messages);
    }

    private static async Task<IReadOnlyList<SqliteConversationInputRow>> ReadClaimAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid claimId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, conversation_id, sequence, revision, target_turn_id, claim_id, claim_owner_run_id,
                status, payload_json, consumed_turn_id, message_id, kind
            FROM conversation_inputs WHERE claim_id = $claimId ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$claimId", claimId.ToString("D"));
        var rows = new List<SqliteConversationInputRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt32(11) != 1 || reader.IsDBNull(4) || reader.IsDBNull(6))
            {
                throw new InvalidOperationException("P2 can only consume an already-claimed Steer batch.");
            }

            rows.Add(new SqliteConversationInputRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2), reader.GetInt32(3), Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)),
                Guid.Parse(reader.GetString(6)), reader.GetInt32(7), ReadPrompt(reader.GetString(8)),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10))));
        }

        return rows;
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

    private static string ReadPrompt(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1 ||
            !document.RootElement.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The input payload format is not supported.");
        }

        return prompt.GetString() ?? throw new InvalidOperationException("An input prompt cannot be null.");
    }

    private static Guid CreateMessageId(Guid claimId, Guid inputId)
    {
        // Stable across repeated claimed reads, without persisting an unconsumed message mapping.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"SelfClaw.InputMessage.v1/{claimId:D}/{inputId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void ValidateBatch(ConversationInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.ConversationId == Guid.Empty || batch.TurnId == Guid.Empty || batch.OwnerRunId == Guid.Empty ||
            batch.ClaimId == Guid.Empty || batch.Inputs.Count == 0 ||
            batch.Inputs.Select(input => input.InputId).Distinct().Count() != batch.Inputs.Count ||
            batch.Inputs.Where((input, index) => input.InputId == Guid.Empty || input.Revision <= 0 || input.Sequence <= 0 ||
                input.MessageId != CreateMessageId(batch.ClaimId, input.InputId) ||
                (index > 0 && batch.Inputs[index - 1].Sequence >= input.Sequence)).Any())
        {
            throw new ArgumentException("An input batch requires a nonempty, ordered, immutable claim.", nameof(batch));
        }
    }

    private static void ValidateRows(ConversationInputBatch batch, IReadOnlyList<SqliteConversationInputRow> rows)
    {
        ValidateBatch(batch);
        if (rows.Count != batch.Inputs.Count || rows.Where((row, index) =>
                row.Id != batch.Inputs[index].InputId || row.Sequence != batch.Inputs[index].Sequence ||
                row.Revision != batch.Inputs[index].Revision || row.Prompt != batch.Inputs[index].Prompt ||
                row.ConversationId != batch.ConversationId || row.TargetTurnId != batch.TurnId ||
                row.ClaimId != batch.ClaimId || row.OwnerRunId != batch.OwnerRunId).Any())
        {
            throw new InvalidOperationException("The input batch no longer matches its durable claim and owner.");
        }
    }

    private static void ValidateBoundary(ConversationInputBoundaryCommit commit)
    {
        ValidateBatch(commit.Batch);
        var turn = commit.Content.Turn;
        if (turn.Id != commit.Batch.TurnId || turn.ConversationId != commit.Batch.ConversationId ||
            turn.Status != ConversationTurnStatus.Running || turn.ExecutionMode != AgentExecutionMode.Direct ||
            turn.Origin != DirectTurnOrigin.Interactive || commit.Content.Messages.Any(message => message.Status != MessageStatus.Sealed))
        {
            throw new ArgumentException("An input boundary seals fragments on its Running Interactive Direct turn.", nameof(commit));
        }
    }
}
