using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
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

    public async Task<IReadOnlyList<AutomationRunRecord>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = new List<AutomationRunRecord>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT {RunColumns} FROM automation_runs WHERE status IN (0, 1) ORDER BY created_at_utc;";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                interrupted.Add(ReadRun(reader));
            }
        }

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
        return interrupted;
    }

    private const string RunColumns = """
        id, task_id, conversation_id, trigger, scheduled_for_utc, status, skip_reason, attempt,
        model_profile_id, capability_snapshot_json, workspace_root_id, workspace_path_snapshot,
        started_at_utc, completed_at_utc, error_code, error_message, created_at_utc
        """;

    private static AutomationRunRecord ReadRun(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            (AutomationRunTrigger)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            (AutomationRunStatus)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : (AutomationRunSkipReason)reader.GetInt32(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            DateTimeOffset.Parse(reader.GetString(16)));
}
