using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed partial class SqliteConversationInputRepository : IConversationInputSteeringStore
{
    public async Task<ConversationInputBatch?> TryClaimPendingSteersAsync(
        Guid conversationId, Guid turnId, Guid ownerRunId, CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty || turnId == Guid.Empty || ownerRunId == Guid.Empty)
        {
            throw new ArgumentException("A steer claim requires conversation, turn and owner identities.");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await IsLiveSteerTargetAsync(connection, transaction, conversationId, turnId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var claimId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow.ToString("O");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_inputs SET status = 1, claim_id = $claim, claim_owner_run_id = $owner,
                    revision = revision + 1, updated_at_utc = $at
                WHERE conversation_id = $conversationId AND target_turn_id = $turnId AND kind = 1 AND status = 0;
                """;
            command.Parameters.AddWithValue("$claim", claimId.ToString("D"));
            command.Parameters.AddWithValue("$owner", ownerRunId.ToString("D"));
            command.Parameters.AddWithValue("$at", at);
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                // The high-water batch was empty: the turn still owns the boundary and may close next.
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var rows = await ReadClaimAsync(connection, transaction, claimId, cancellationToken).ConfigureAwait(false);
        var skillRows = _skillTokenDetector is null
            ? []
            : rows.Where(row => _skillTokenDetector.ContainsExplicitActivation(row.Prompt)).ToArray();
        if (skillRows.Length > 0)
        {
            // An explicit Skill activation can only run from a fresh turn setup, so the steer is
            // converted to a follow-up inside the same claim transaction and never enters this turn.
            await ReleaseClaimedInputsAsync(connection, transaction, claimId, skillRows.Select(row => row.Id).ToArray(),
                ConversationInputReason.SkillRequiresNewTurn, cancellationToken).ConfigureAwait(false);
        }

        var claimedRows = rows.Where(row => !skillRows.Any(skill => skill.Id == row.Id)).ToArray();
        if (claimedRows.Length == 0)
        {
            await AdvanceQueueRevisionAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var batch = new ConversationInputBatch(conversationId, turnId, ownerRunId, claimId,
            claimedRows.Select(row => new ConversationInputItem(row.Id, row.Sequence, row.Revision,
                ConversationInputMessageId.Compute(claimId, row.Id), row.Prompt)).ToArray());
        ValidateRows(batch, claimedRows);
        await AdvanceQueueRevisionAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batch;
    }

    private static async Task ReleaseClaimedInputsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid claimId, IReadOnlyList<Guid> inputIds, string reasonCode, CancellationToken cancellationToken)
    {
        foreach (var inputId in inputIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_inputs SET kind = 0, target_turn_id = NULL, status = 0,
                    claim_id = NULL, claim_owner_run_id = NULL, reason_code = $reason,
                    revision = revision + 1, updated_at_utc = $at
                WHERE id = $id AND claim_id = $claim AND kind = 1 AND status = 1;
                """;
            command.Parameters.AddWithValue("$reason", reasonCode);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", inputId.ToString("D"));
            command.Parameters.AddWithValue("$claim", claimId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("A claimed steer changed while it was being released.");
            }
        }
    }

    public async Task<int> ReleasePendingSteersToFollowUpAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var changed = await ReleasePendingSteersAsync(connection, transaction, conversationId, turnId, reasonCode,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<int> HoldUnconsumedSteersAsync(Guid conversationId, Guid turnId, string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var changed = await HoldUnconsumedSteersAsync(connection, transaction, conversationId, turnId, reasonCode, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Settles the turn's steer intake inside its terminal transaction: any steer not consumed by a
    /// real boundary is held, so a claimed input is never reopened as a cancellable follow-up.
    /// </summary>
    internal static async Task SettleSteerIntakeAsync(SqliteConnection connection, SqliteTransaction transaction,
        ConversationTurnRecord turn, CancellationToken cancellationToken)
    {
        if (turn.ExecutionMode != AgentExecutionMode.Direct || turn.Origin != DirectTurnOrigin.Interactive)
        {
            return;
        }

        var changed = await HoldUnconsumedSteersAsync(connection, transaction, turn.ConversationId, turn.Id,
            turn.Status == ConversationTurnStatus.Succeeded
                ? ConversationInputReason.TargetTurnClosed : ReasonFor(turn.Status), cancellationToken).ConfigureAwait(false);
        if (changed > 0)
        {
            await AdvanceQueueRevisionAsync(connection, transaction, turn.ConversationId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReleasePendingSteersAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid conversationId, Guid turnId, string reasonCode, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_inputs SET kind = 0, target_turn_id = NULL, status = 0,
                claim_id = NULL, claim_owner_run_id = NULL, reason_code = $reason,
                revision = revision + 1, updated_at_utc = $at
            WHERE conversation_id = $conversationId AND target_turn_id = $turnId AND kind = 1 AND status = 0;
            """;
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> HoldUnconsumedSteersAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid conversationId, Guid turnId, string reasonCode, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversation_inputs SET status = 3, reason_code = $reason, error_message = NULL,
                revision = revision + 1, updated_at_utc = $at
            WHERE conversation_id = $conversationId AND target_turn_id = $turnId AND kind = 1 AND status IN (0, 1);
            """;
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsLiveSteerTargetAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid conversationId, Guid turnId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM conversation_turns
                WHERE id = $turnId AND conversation_id = $conversationId
                    AND status = 0 AND execution_mode = 0 AND origin = 0);
            """;
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) != 0;
    }

    private static string ReasonFor(ConversationTurnStatus status) => status switch
    {
        ConversationTurnStatus.Cancelled => ConversationInputReason.TurnCancelled,
        ConversationTurnStatus.Truncated => ConversationInputReason.TurnTruncated,
        ConversationTurnStatus.Blocked => ConversationInputReason.TurnBlocked,
        ConversationTurnStatus.Interrupted => ConversationInputReason.TurnInterrupted,
        _ => ConversationInputReason.TurnFailed
    };
}
