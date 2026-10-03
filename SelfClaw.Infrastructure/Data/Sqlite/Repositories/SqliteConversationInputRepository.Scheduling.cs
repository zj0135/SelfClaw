using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed partial class SqliteConversationInputRepository
{
    public async Task<ConversationInputQueueState> PauseQueueAsync(Guid conversationId, string reason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_input_state(conversation_id, next_input_sequence, queue_revision, paused, pause_reason)
            SELECT id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM conversation_inputs WHERE conversation_id = $id), 1, 1, $reason
            FROM conversations WHERE id = $id
            ON CONFLICT(conversation_id) DO UPDATE SET paused = 1, pause_reason = $reason,
                queue_revision = queue_revision + 1;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$reason", reason);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var state = await ReadQueueStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return state;
    }

    public async Task<ConversationInputRecord?> TryClaimNextFollowUpAsync(
        Guid conversationId, Guid ownerRunId, Guid claimId, CancellationToken cancellationToken = default)
    {
        if (ownerRunId == Guid.Empty || claimId == Guid.Empty)
        {
            throw new ArgumentException("A claim requires a run owner and claim identity.");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var state = await ReadStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        if (state.Paused)
        {
            return null;
        }

        Guid? candidate;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id FROM conversation_inputs
                WHERE conversation_id = $id AND kind = 0 AND status = 0
                    AND NOT EXISTS (SELECT 1 FROM conversation_inputs earlier
                        WHERE earlier.conversation_id = $id AND earlier.status IN (0, 1, 3)
                            AND earlier.sequence < conversation_inputs.sequence)
                ORDER BY sequence LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
            candidate = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value ? Guid.Parse(value) : null;
        }

        if (candidate is not { } inputId)
        {
            return null;
        }

        int revision;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_inputs SET status = 1, claim_id = $claim, claim_owner_run_id = $owner,
                    revision = revision + 1, updated_at_utc = $at
                WHERE id = $id AND status = 0;
                """;
            command.Parameters.AddWithValue("$claim", claimId.ToString("D"));
            command.Parameters.AddWithValue("$owner", ownerRunId.ToString("D"));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", inputId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return null;
            }

            revision = await ReadRevisionAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false);
        }

        await AdvanceQueueRevisionAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        var record = await ReadByIdAsync(connection, transaction, inputId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The claimed input disappeared within its transaction.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<Guid>> ListDispatchableConversationIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Dispatchable = not paused and the earliest unprocessed item is a Pending FollowUp. A
        // paused queue or a Held/claimed head would make every scan reserve and immediately release
        // a run handle, which republishes the transcript and flickers the UI.
        command.CommandText = """
            SELECT DISTINCT i.conversation_id
            FROM conversation_inputs i
            JOIN conversations c ON c.id = i.conversation_id AND c.kind = 0
            LEFT JOIN conversation_input_state s ON s.conversation_id = i.conversation_id
            WHERE i.status = 0 AND i.kind = 0
                AND COALESCE(s.paused, 0) = 0
                AND NOT EXISTS (SELECT 1 FROM conversation_inputs earlier
                    WHERE earlier.conversation_id = i.conversation_id
                        AND earlier.status IN (0, 1, 3)
                        AND earlier.sequence < i.sequence);
            """;
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    public async Task<IReadOnlyList<ConversationInputQueueState>> RecoverStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var interrupted = connection.CreateCommand())
        {
            interrupted.Transaction = transaction;
            interrupted.CommandText = """
                UPDATE conversation_turns SET status = 6, completed_at_utc = $at, error_message = 'Interrupted by restart'
                WHERE status = 0 AND execution_mode = 0 AND origin = 0;
                UPDATE messages SET status = 2
                WHERE status = 1 AND turn_id IN (SELECT id FROM conversation_turns
                    WHERE status = 6 AND error_message = 'Interrupted by restart');
                """;
            interrupted.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await interrupted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var conversationIds = new List<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DISTINCT conversation_id FROM conversation_inputs WHERE status IN (0, 1, 3);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                conversationIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var states = new List<ConversationInputQueueState>();
        foreach (var conversationId in conversationIds)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE conversation_inputs SET status = 3, reason_code = $reason, error_message = NULL,
                        revision = revision + 1, updated_at_utc = $at
                    WHERE conversation_id = $id AND status = 1;
                    """;
                command.Parameters.AddWithValue("$reason", ConversationInputReason.InterruptedByRestart);
                command.Parameters.AddWithValue("$at", now);
                command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE conversation_inputs SET status = 3, reason_code = $reason, error_message = NULL,
                        revision = revision + 1, updated_at_utc = $at
                    WHERE conversation_id = $id AND kind = 1 AND status = 0
                        ;
                    """;
                command.Parameters.AddWithValue("$reason", ConversationInputReason.StaleSteerTarget);
                command.Parameters.AddWithValue("$at", now);
                command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO conversation_input_state(conversation_id, next_input_sequence, queue_revision, paused, pause_reason)
                    VALUES($id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM conversation_inputs WHERE conversation_id = $id), 1, 1, $reason)
                    ON CONFLICT(conversation_id) DO UPDATE SET paused = 1,
                        pause_reason = CASE WHEN conversation_input_state.paused = 1 THEN conversation_input_state.pause_reason ELSE $reason END,
                        queue_revision = queue_revision + 1;
                    """;
                command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
                command.Parameters.AddWithValue("$reason", ConversationInputReason.InterruptedByRestart);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            states.Add(await ReadQueueStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false));
        }

        if (_beforeCommit is not null)
            await _beforeCommit("recovery-before-commit", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return states;
    }

}
