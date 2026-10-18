using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed partial class SqliteConversationInputRepository
{
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

        var messages = await ConsumeInputsAsync(connection, transaction, commit, rows, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<MessageRecord>> ConsumeInputsAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationInputBoundaryCommit commit,
        IReadOnlyList<SqliteConversationInputRow> rows, CancellationToken cancellationToken)
    {
        var messages = new List<MessageRecord>();
        var at = DateTimeOffset.UtcNow;
        var attachmentsRoot = AttachmentPathResolver.ResolveRoot(_database.Paths.AppDataDirectory);
        for (var index = 0; index < commit.Batch.Inputs.Count; index++)
        {
            var input = commit.Batch.Inputs[index];
            var message = await SqliteConversationTurnWriter.InsertUserAsync(connection, transaction, commit.Content.Turn,
                input.MessageId, input.Prompt, ResolveAttachments(attachmentsRoot, rows[index].Attachments, input.MessageId),
                cancellationToken, at).ConfigureAwait(false);
            await UpdateConsumedAsync(connection, transaction, commit.Batch, input, message, at, cancellationToken).ConfigureAwait(false);
            messages.Add(message);
        }

        return messages;
    }

    // Admission already proved the file exists; a file that vanished in between is skipped so a
    // single lost image cannot fail the whole boundary commit.
    private static IReadOnlyList<MessageAttachmentRecord>? ResolveAttachments(string root,
        IReadOnlyList<AttachmentReference>? references, Guid messageId)
    {
        if (references is not { Count: > 0 }) return null;
        var records = new List<MessageAttachmentRecord>(references.Count);
        foreach (var reference in references)
        {
            if (!AttachmentPathResolver.TryResolveExistingFile(root, reference.RelativePath, out var fullPath)) continue;
            records.Add(AttachmentPathResolver.CreateRecord(reference, fullPath, messageId, DateTimeOffset.UtcNow));
        }

        return records.Count == 0 ? null : records;
    }

    private static async Task UpdateConsumedAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationInputBatch batch,
        ConversationInputItem input, MessageRecord message, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_inputs SET status = 2, consumed_turn_id = $turnId, message_id = $messageId,
                consumed_at_utc = $at, updated_at_utc = $at
            WHERE id = $id AND conversation_id = $conversationId AND target_turn_id = $turnId AND kind = 1
                AND status = 1 AND revision = $revision AND claim_id = $claimId AND claim_owner_run_id = $owner;
            """;
        command.Parameters.AddWithValue("$turnId", batch.TurnId.ToString("D"));
        command.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$id", input.InputId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", batch.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$revision", input.Revision);
        command.Parameters.AddWithValue("$claimId", batch.ClaimId.ToString("D"));
        command.Parameters.AddWithValue("$owner", batch.OwnerRunId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The complete input claim must be consumed atomically.");
        }
    }

    private static async Task<ConversationInputConsumption?> ReadConsumptionAsync(
        SqliteConnection connection, SqliteTransaction transaction, ConversationInputBatch batch,
        IReadOnlyList<SqliteConversationInputRow> rows, CancellationToken cancellationToken)
    {
        if (rows.All(row => row.Status != 2))
        {
            return null;
        }

        if (rows.Any(row => row.Status != 2 || row.ConsumedTurnId != batch.TurnId || row.MessageId != ConversationInputMessageId.Compute(batch.ClaimId, row.Id)))
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
                throw new InvalidOperationException("A boundary batch must be a claimed Steer bound to its turn.");
            }

            var payload = ReadPayload(reader.GetString(8));
            rows.Add(new SqliteConversationInputRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt64(2), reader.GetInt32(3), Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)),
                Guid.Parse(reader.GetString(6)), reader.GetInt32(7), payload.Prompt,
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)), payload.Attachments));
        }

        return rows;
    }

    private static void ValidateBatch(ConversationInputBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.ConversationId == Guid.Empty || batch.TurnId == Guid.Empty || batch.OwnerRunId == Guid.Empty ||
            batch.ClaimId == Guid.Empty || batch.Inputs.Count == 0 ||
            batch.Inputs.Select(input => input.InputId).Distinct().Count() != batch.Inputs.Count ||
            batch.Inputs.Where((input, index) => input.InputId == Guid.Empty || input.Revision <= 0 || input.Sequence <= 0 ||
                input.MessageId != ConversationInputMessageId.Compute(batch.ClaimId, input.InputId) ||
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
                !SameAttachments(row.Attachments, batch.Inputs[index].Attachments) ||
                row.ConversationId != batch.ConversationId || row.TargetTurnId != batch.TurnId ||
                row.ClaimId != batch.ClaimId || row.OwnerRunId != batch.OwnerRunId).Any())
        {
            throw new InvalidOperationException("The input batch no longer matches its durable claim and owner.");
        }
    }

    private static bool SameAttachments(IReadOnlyList<AttachmentReference>? left, IReadOnlyList<AttachmentReference>? right)
        => (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => left.SequenceEqual(right)
        };

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
