using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Data.Sqlite;

/// <summary>
/// Settles Automation work a previous process left unfinished. It runs before any Automation run can
/// start. Interactive turns (origin = 0) are settled by <see cref="SqliteStartupRecovery"/>; Continuation
/// and Subagent turns by their own owners. An interrupted run is never replayed because a tool that
/// already ran may have side effects.
/// </summary>
internal sealed class AutomationStartupRecovery : IAutomationStartupRecovery
{
    private const string InterruptedTurnMessage = "Interrupted by restart";
    private const string UnfinishedToolSummary = "The agent run ended before this tool call completed.";
    private readonly SqliteDatabase _database;

    public AutomationStartupRecovery(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE tool_runs SET status = 2, result_summary = COALESCE(result_summary, $toolSummary), updated_at_utc = $at
            WHERE status IN (0, 3) AND message_id IN (
                SELECT m.id FROM messages m JOIN conversation_turns t ON t.id = m.turn_id
                WHERE t.status = 0 AND t.origin = 3);
            UPDATE messages SET status = 2, updated_at_utc = $at
            WHERE status = 1 AND turn_id IN (SELECT id FROM conversation_turns WHERE status = 0 AND origin = 3);
            UPDATE conversation_turns SET status = 6, completed_at_utc = $at, error_message = $turnMessage
            WHERE status = 0 AND origin = 3;

            UPDATE automation_runs SET status = 5, error_code = $errorCode, completed_at_utc = $at
            WHERE status IN (0, 1);
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$toolSummary", UnfinishedToolSummary);
        command.Parameters.AddWithValue("$turnMessage", InterruptedTurnMessage);
        command.Parameters.AddWithValue("$errorCode", AutomationErrorCodes.ProcessInterrupted);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
