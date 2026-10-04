using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite;

/// <summary>
/// Settles work a previous process left unfinished. It runs once during infrastructure
/// initialization, before any hosted service or window can admit a run, so every Running
/// Interactive turn it sees belongs to a dead process. Continuation and Subagent turns are
/// recovered by their own delivery and task owners.
/// </summary>
internal sealed class SqliteStartupRecovery
{
    private const string InterruptedTurnMessage = "Interrupted by restart";
    private const string UnfinishedToolSummary = "The agent run ended before this tool call completed.";

    private readonly SqliteDatabase _database;
    private readonly Func<string, CancellationToken, Task>? _beforeCommit;

    public SqliteStartupRecovery(SqliteDatabase database, Func<string, CancellationToken, Task>? beforeCommit = null)
    {
        _database = database;
        _beforeCommit = beforeCommit;
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Children first: they select their turns by the Running status the last statement clears.
        // A Claimed input never consumed was not started; it is held rather than replayed.
        command.CommandText = """
            UPDATE tool_runs SET status = 2, result_summary = COALESCE(result_summary, $toolSummary), updated_at_utc = $at
            WHERE status IN (0, 3) AND message_id IN (
                SELECT m.id FROM messages m JOIN conversation_turns t ON t.id = m.turn_id
                WHERE t.status = 0 AND t.origin = 0);
            UPDATE messages SET status = 2, updated_at_utc = $at
            WHERE status = 1 AND turn_id IN (SELECT id FROM conversation_turns WHERE status = 0 AND origin = 0);
            UPDATE conversation_turns SET status = 6, completed_at_utc = $at, error_message = $turnMessage
            WHERE status = 0 AND origin = 0;

            UPDATE conversation_inputs SET status = 3, reason_code = $restart, error_message = NULL,
                revision = revision + 1, updated_at_utc = $at
            WHERE status = 1;
            UPDATE conversation_inputs SET status = 3, reason_code = $staleSteer, error_message = NULL,
                revision = revision + 1, updated_at_utc = $at
            WHERE kind = 1 AND status = 0;
            INSERT INTO conversation_input_state(conversation_id, next_input_sequence, queue_revision, paused, pause_reason)
            SELECT conversation_id, MAX(sequence) + 1, 1, 1, $restart FROM conversation_inputs
            WHERE status IN (0, 3) GROUP BY conversation_id
            ON CONFLICT(conversation_id) DO UPDATE SET paused = 1,
                pause_reason = CASE WHEN conversation_input_state.paused = 1
                    THEN conversation_input_state.pause_reason ELSE excluded.pause_reason END,
                queue_revision = queue_revision + 1;
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$toolSummary", UnfinishedToolSummary);
        command.Parameters.AddWithValue("$turnMessage", InterruptedTurnMessage);
        command.Parameters.AddWithValue("$restart", ConversationInputReason.InterruptedByRestart);
        command.Parameters.AddWithValue("$staleSteer", ConversationInputReason.StaleSteerTarget);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (_beforeCommit is not null)
        {
            await _beforeCommit("recovery-before-commit", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
