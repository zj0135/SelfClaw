using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;

namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence;

internal sealed class SqliteSubagentDeliveryRepository : ISubagentDeliveryStore
{
    private const int MaximumAttempts = 3;
    private const int MaximumErrorLength = 2048;
    private const string DeliveryColumns = """
        id, task_id, parent_conversation_id, parent_turn_id, status, envelope_json, envelope_bytes,
        lease_token, leased_until_utc, attempt_count, next_attempt_at_utc, continuation_turn_id,
        last_error, created_at_utc, updated_at_utc, delivered_at_utc, dead_lettered_at_utc
        """;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SecondRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly int EmptyBatchBytes = Encoding.UTF8.GetByteCount("{\"deliveries\":[]}");

    private readonly SqliteDatabase _database;
    private readonly ILogger<SqliteSubagentDeliveryRepository> _logger;

    public SqliteSubagentDeliveryRepository(
        SqliteDatabase database,
        ILogger<SqliteSubagentDeliveryRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _logger = logger;
    }

    internal SqliteSubagentDeliveryRepository(SqliteDatabase database)
        : this(database, NullLogger<SqliteSubagentDeliveryRepository>.Instance)
    {
    }

    public async Task<SubagentMailboxKey?> PeekReadyMailboxAsync(
        DateTimeOffset readyAtUtc,
        DateTimeOffset createdBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.parent_conversation_id, d.parent_turn_id, t.parent_execution_snapshot_json, d.created_at_utc
            FROM subagent_deliveries d
            INNER JOIN subagent_tasks t ON t.id = d.task_id
            INNER JOIN conversations c ON c.id = d.parent_conversation_id AND c.kind = $interactiveKind
            WHERE d.status = $pending
              AND d.next_attempt_at_utc <= $readyAt
              AND d.created_at_utc <= $createdBefore
              AND d.attempt_count < $maximumAttempts
              AND NOT EXISTS (
                  SELECT 1
                  FROM subagent_deliveries active
                  WHERE active.parent_conversation_id = d.parent_conversation_id
                    AND active.status = $leased)
            ORDER BY d.next_attempt_at_utc, d.created_at_utc, d.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$interactiveKind", (int)ConversationKind.Interactive);
        command.Parameters.AddWithValue("$pending", (int)SubagentDeliveryStatus.Pending);
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$readyAt", readyAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$createdBefore", createdBeforeUtc.ToString("O"));
        command.Parameters.AddWithValue("$maximumAttempts", MaximumAttempts);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SubagentMailboxKey(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            reader.GetString(2),
            ReadDateTimeOffset(reader, 3));
    }

    public async Task<SubagentDeliveryLease?> TryLeaseBatchAsync(
        SubagentMailboxKey mailbox,
        Guid leaseToken,
        Guid continuationTurnId,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leasedUntilUtc,
        int maximumBatchBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ValidateLeaseRequest(leaseToken, continuationTurnId, leasedAtUtc, leasedUntilUtc, maximumBatchBytes);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var candidates = await ReadBatchCandidatesAsync(
            connection,
            transaction,
            mailbox,
            leasedAtUtc,
            cancellationToken).ConfigureAwait(false);
        var selected = SelectWithinBatchLimit(candidates, maximumBatchBytes);
        if (selected.Count == 0 || await HasActiveLeaseAsync(
                connection,
                transaction,
                mailbox.ParentConversationId,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        foreach (var delivery in selected)
        {
            if (!await TryMarkLeasedAsync(
                    connection,
                    transaction,
                    delivery.Id,
                    leaseToken,
                    continuationTurnId,
                    leasedAtUtc,
                    leasedUntilUtc,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SubagentDeliveryMetrics.RecordLease(selected.Count);
        _logger.LogInformation(
            "Subagent delivery lease acquired. ParentConversationId={ParentConversationId} ParentTurnId={ParentTurnId} ContinuationTurnId={ContinuationTurnId} DeliveryCount={DeliveryCount} LeasedUntilUtc={LeasedUntilUtc}",
            mailbox.ParentConversationId,
            mailbox.ParentTurnId,
            continuationTurnId,
            selected.Count,
            leasedUntilUtc);
        var leasedDeliveries = selected.Select(delivery => delivery with
        {
            Status = SubagentDeliveryStatus.Leased,
            LeaseToken = leaseToken,
            LeasedUntilUtc = leasedUntilUtc,
            AttemptCount = delivery.AttemptCount + 1,
            ContinuationTurnId = continuationTurnId,
            UpdatedAtUtc = leasedAtUtc
        }).ToArray();
        return new SubagentDeliveryLease(
            leaseToken,
            continuationTurnId,
            mailbox.ParentConversationId,
            mailbox.ParentTurnId,
            mailbox.ParentExecutionSnapshotJson,
            leasedDeliveries,
            leasedUntilUtc);
    }

    public async Task<bool> TryRenewLeaseAsync(
        SubagentDeliveryLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leasedUntilUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.LeaseToken == Guid.Empty ||
            lease.ContinuationTurnId == Guid.Empty ||
            lease.Deliveries.Count == 0 ||
            leasedUntilUtc <= renewedAtUtc)
        {
            throw new ArgumentException("A lease renewal requires valid ids and a future deadline.");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadLeaseDeliveriesAsync(
            connection,
            transaction,
            lease,
            cancellationToken).ConfigureAwait(false);
        if (current.Count != lease.Deliveries.Count)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Subagent delivery lease renewal rejected because the batch no longer matches. LeaseToken={LeaseToken} ContinuationTurnId={ContinuationTurnId} ExpectedCount={ExpectedCount} ActualCount={ActualCount}",
                lease.LeaseToken,
                lease.ContinuationTurnId,
                lease.Deliveries.Count,
                current.Count);
            return false;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE subagent_deliveries
            SET leased_until_utc = $leasedUntil, updated_at_utc = $renewedAt
            WHERE status = $leased
              AND lease_token = $leaseToken
              AND continuation_turn_id = $continuationTurnId;
            """;
        command.Parameters.AddWithValue("$leasedUntil", leasedUntilUtc.ToString("O"));
        command.Parameters.AddWithValue("$renewedAt", renewedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$leaseToken", lease.LeaseToken.ToString("D"));
        command.Parameters.AddWithValue("$continuationTurnId", lease.ContinuationTurnId.ToString("D"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected != lease.Deliveries.Count)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "Subagent delivery lease renewal rolled back after a partial update. LeaseToken={LeaseToken} ContinuationTurnId={ContinuationTurnId} ExpectedCount={ExpectedCount} UpdatedCount={UpdatedCount}",
                lease.LeaseToken,
                lease.ContinuationTurnId,
                lease.Deliveries.Count,
                affected);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Subagent delivery lease renewed. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId} LeasedUntilUtc={LeasedUntilUtc}",
            lease.ParentConversationId,
            lease.ContinuationTurnId,
            leasedUntilUtc);
        return true;
    }

    public async Task<SubagentDeliveryResolutionResult> TryResolveAsync(
        SubagentDeliveryLease lease,
        SubagentDeliveryResolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(resolution);
        ValidateResolution(lease, resolution);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadLeaseDeliveriesAsync(
            connection,
            transaction,
            lease,
            cancellationToken).ConfigureAwait(false);
        if (current.Count != lease.Deliveries.Count)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return LeaseMismatch();
        }

        var delivered = new List<Guid>();
        var pending = new List<Guid>();
        var deadLettered = new List<Guid>();
        foreach (var delivery in current)
        {
            var target = ResolveTarget(delivery, resolution.Kind);
            if (!await TryUpdateResolutionAsync(
                    connection,
                    transaction,
                    delivery,
                    lease,
                    target,
                    resolution,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return LeaseMismatch();
            }

            AddResolutionId(target, delivery.Id, delivered, pending, deadLettered);
        }

        if (resolution.TurnFinalization is TurnFinalization finalization &&
            !await SqliteTurnFinalizationWriter.TryWriteAsync(
                    connection,
                    transaction,
                    finalization,
                    cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return LeaseMismatch();
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SubagentDeliveryMetrics.RecordResolution(delivered.Count, pending.Count, deadLettered.Count);
        _logger.LogInformation(
            "Subagent delivery resolved. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId} Resolution={Resolution} DeliveredCount={DeliveredCount} RetriedCount={RetriedCount} DeadLetteredCount={DeadLetteredCount}",
            lease.ParentConversationId,
            lease.ContinuationTurnId,
            resolution.Kind,
            delivered.Count,
            pending.Count,
            deadLettered.Count);
        return new SubagentDeliveryResolutionResult(true, delivered, pending, deadLettered);
    }

    public async Task<IReadOnlyList<SubagentDeliveryRecord>> RecoverExpiredLeasesAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var expired = await ReadExpiredLeasesAsync(
            connection,
            transaction,
            recoveredAtUtc,
            cancellationToken).ConfigureAwait(false);
        var deadLetters = new List<SubagentDeliveryRecord>();
        foreach (var delivery in expired)
        {
            var hasRecordedTools = delivery.ContinuationTurnId is Guid continuationTurnId &&
                                   await HasRecordedToolsAsync(
                                       connection,
                                       transaction,
                                       delivery.ParentConversationId,
                                       continuationTurnId,
                                       cancellationToken).ConfigureAwait(false);
            var deadLetter = hasRecordedTools || delivery.AttemptCount >= MaximumAttempts;
            await RecoverLeaseAsync(
                connection,
                transaction,
                delivery,
                recoveredAtUtc,
                deadLetter,
                cancellationToken).ConfigureAwait(false);
            if (deadLetter)
            {
                if (hasRecordedTools && delivery.ContinuationTurnId is Guid failedTurnId)
                {
                    await FinalizeRecoveredToolTurnAsync(
                        connection,
                        transaction,
                        delivery.ParentConversationId,
                        failedTurnId,
                        recoveredAtUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                deadLetters.Add(delivery with
                {
                    Status = SubagentDeliveryStatus.DeadLetter,
                    LeaseToken = null,
                    LeasedUntilUtc = null,
                    LastError = "The continuation lease expired and cannot be replayed safely.",
                    UpdatedAtUtc = recoveredAtUtc,
                    DeadLetteredAtUtc = recoveredAtUtc
                });
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SubagentDeliveryMetrics.RecordRecovery(expired.Count);
        if (expired.Count > 0)
        {
            _logger.LogWarning(
                "Expired Subagent delivery leases recovered. RecoveredCount={RecoveredCount} DeadLetteredCount={DeadLetteredCount} RecoveredAtUtc={RecoveredAtUtc}",
                expired.Count,
                deadLetters.Count,
                recoveredAtUtc);
        }
        return deadLetters;
    }

    internal async Task<SubagentDeliveryRecord?> GetAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateDeliverySelectCommand(connection);
        command.CommandText += " WHERE parent_conversation_id = $parentConversationId AND task_id = $taskId LIMIT 1;";
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDelivery(reader) : null;
    }

    private static async Task<IReadOnlyList<SubagentDeliveryRecord>> ReadBatchCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubagentMailboxKey mailbox,
        DateTimeOffset readyAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = CreateDeliverySelectCommand(connection, transaction, "d");
        command.CommandText += """
            INNER JOIN subagent_tasks t ON t.id = d.task_id
            WHERE d.parent_conversation_id = $parentConversationId
              AND d.parent_turn_id = $parentTurnId
              AND t.parent_execution_snapshot_json = $snapshot
              AND d.status = $pending
              AND d.next_attempt_at_utc <= $readyAt
              AND d.attempt_count < $maximumAttempts
            ORDER BY d.created_at_utc, d.id;
            """;
        command.Parameters.AddWithValue("$parentConversationId", mailbox.ParentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$parentTurnId", mailbox.ParentTurnId.ToString("D"));
        command.Parameters.AddWithValue("$snapshot", mailbox.ParentExecutionSnapshotJson);
        command.Parameters.AddWithValue("$pending", (int)SubagentDeliveryStatus.Pending);
        command.Parameters.AddWithValue("$readyAt", readyAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$maximumAttempts", MaximumAttempts);
        var deliveries = new List<SubagentDeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            deliveries.Add(ReadDelivery(reader));
        }

        return deliveries;
    }

    private static IReadOnlyList<SubagentDeliveryRecord> SelectWithinBatchLimit(
        IReadOnlyList<SubagentDeliveryRecord> candidates,
        int maximumBatchBytes)
    {
        var selected = new List<SubagentDeliveryRecord>();
        var bytes = EmptyBatchBytes;
        foreach (var candidate in candidates)
        {
            var nextBytes = bytes + candidate.EnvelopeBytes + (selected.Count == 0 ? 0 : 1);
            if (selected.Count > 0 && nextBytes > maximumBatchBytes)
            {
                break;
            }

            if (nextBytes > maximumBatchBytes)
            {
                throw new InvalidDataException("A stored Subagent envelope exceeds the continuation batch limit.");
            }

            selected.Add(candidate);
            bytes = nextBytes;
        }

        return selected;
    }

    private static async Task<bool> HasActiveLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid parentConversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM subagent_deliveries
            WHERE parent_conversation_id = $parentConversationId AND status = $leased;
            """;
        command.Parameters.AddWithValue("$parentConversationId", parentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) > 0;
    }

    private static async Task<bool> TryMarkLeasedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deliveryId,
        Guid leaseToken,
        Guid continuationTurnId,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leasedUntilUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE subagent_deliveries
            SET status = $leased,
                lease_token = $leaseToken,
                leased_until_utc = $leasedUntil,
                continuation_turn_id = $continuationTurnId,
                attempt_count = attempt_count + 1,
                updated_at_utc = $leasedAt
            WHERE id = $deliveryId AND status = $pending AND attempt_count < $maximumAttempts;
            """;
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$leaseToken", leaseToken.ToString("D"));
        command.Parameters.AddWithValue("$leasedUntil", leasedUntilUtc.ToString("O"));
        command.Parameters.AddWithValue("$continuationTurnId", continuationTurnId.ToString("D"));
        command.Parameters.AddWithValue("$leasedAt", leasedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$deliveryId", deliveryId.ToString("D"));
        command.Parameters.AddWithValue("$pending", (int)SubagentDeliveryStatus.Pending);
        command.Parameters.AddWithValue("$maximumAttempts", MaximumAttempts);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<SubagentDeliveryRecord>> ReadLeaseDeliveriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubagentDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        var expectedIds = lease.Deliveries.Select(delivery => delivery.Id).ToHashSet();
        await using var command = CreateDeliverySelectCommand(connection, transaction);
        command.CommandText += """
            WHERE status = $leased
              AND lease_token = $leaseToken
              AND continuation_turn_id = $continuationTurnId
            ORDER BY created_at_utc, id;
            """;
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$leaseToken", lease.LeaseToken.ToString("D"));
        command.Parameters.AddWithValue("$continuationTurnId", lease.ContinuationTurnId.ToString("D"));
        var deliveries = new List<SubagentDeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var delivery = ReadDelivery(reader);
            if (expectedIds.Contains(delivery.Id))
            {
                deliveries.Add(delivery);
            }
        }

        return deliveries.Count == expectedIds.Count ? deliveries : [];
    }

    private static async Task<bool> TryUpdateResolutionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubagentDeliveryRecord delivery,
        SubagentDeliveryLease lease,
        SubagentDeliveryStatus target,
        SubagentDeliveryResolution resolution,
        CancellationToken cancellationToken)
    {
        var pending = target == SubagentDeliveryStatus.Pending;
        var delivered = target == SubagentDeliveryStatus.Delivered;
        var deadLettered = target == SubagentDeliveryStatus.DeadLetter;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE subagent_deliveries
            SET status = $target,
                lease_token = NULL,
                leased_until_utc = NULL,
                continuation_turn_id = CASE WHEN $pending = 1 THEN NULL ELSE continuation_turn_id END,
                next_attempt_at_utc = $nextAttemptAt,
                last_error = $lastError,
                updated_at_utc = $occurredAt,
                delivered_at_utc = CASE WHEN $delivered = 1 THEN $occurredAt ELSE delivered_at_utc END,
                dead_lettered_at_utc = CASE WHEN $deadLettered = 1 THEN $occurredAt ELSE dead_lettered_at_utc END
            WHERE id = $deliveryId
              AND status = $leased
              AND lease_token = $leaseToken
              AND continuation_turn_id = $continuationTurnId;
            """;
        command.Parameters.AddWithValue("$target", (int)target);
        command.Parameters.AddWithValue("$pending", pending ? 1 : 0);
        command.Parameters.AddWithValue("$delivered", delivered ? 1 : 0);
        command.Parameters.AddWithValue("$deadLettered", deadLettered ? 1 : 0);
        command.Parameters.AddWithValue(
            "$nextAttemptAt",
            NextAttemptAt(delivery, target, resolution.OccurredAtUtc).ToString("O"));
        command.Parameters.AddWithValue(
            "$lastError",
            delivered ? DBNull.Value : NormalizeError(resolution.Error) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$occurredAt", resolution.OccurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$deliveryId", delivery.Id.ToString("D"));
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$leaseToken", lease.LeaseToken.ToString("D"));
        command.Parameters.AddWithValue("$continuationTurnId", lease.ContinuationTurnId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<SubagentDeliveryRecord>> ReadExpiredLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = CreateDeliverySelectCommand(connection, transaction);
        command.CommandText += """
            WHERE status = $leased AND leased_until_utc <= $recoveredAt
            ORDER BY created_at_utc, id;
            """;
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        command.Parameters.AddWithValue("$recoveredAt", recoveredAtUtc.ToString("O"));
        var deliveries = new List<SubagentDeliveryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            deliveries.Add(ReadDelivery(reader));
        }

        return deliveries;
    }

    private static async Task<bool> HasRecordedToolsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM tool_runs WHERE conversation_id = $conversationId AND message_id = $turnId;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) > 0;
    }

    private static async Task RecoverLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SubagentDeliveryRecord delivery,
        DateTimeOffset recoveredAtUtc,
        bool deadLetter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE subagent_deliveries
            SET status = $status,
                lease_token = NULL,
                leased_until_utc = NULL,
                continuation_turn_id = CASE WHEN $deadLetter = 1 THEN continuation_turn_id ELSE NULL END,
                next_attempt_at_utc = $recoveredAt,
                last_error = $error,
                updated_at_utc = $recoveredAt,
                dead_lettered_at_utc = CASE WHEN $deadLetter = 1 THEN $recoveredAt ELSE NULL END
            WHERE id = $deliveryId AND status = $leased;
            """;
        command.Parameters.AddWithValue(
            "$status",
            (int)(deadLetter ? SubagentDeliveryStatus.DeadLetter : SubagentDeliveryStatus.Pending));
        command.Parameters.AddWithValue("$deadLetter", deadLetter ? 1 : 0);
        command.Parameters.AddWithValue("$recoveredAt", recoveredAtUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$error",
            deadLetter
                ? "The continuation lease expired and cannot be replayed safely."
                : "The continuation lease expired and was returned to the mailbox.");
        command.Parameters.AddWithValue("$deliveryId", delivery.Id.ToString("D"));
        command.Parameters.AddWithValue("$leased", (int)SubagentDeliveryStatus.Leased);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task FinalizeRecoveredToolTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        Guid turnId,
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken)
    {
        const string error = "The application stopped after the continuation began tool execution; replay was suppressed.";
        await using (var messageCommand = connection.CreateCommand())
        {
            messageCommand.Transaction = transaction;
            messageCommand.CommandText = """
                INSERT INTO messages(
                    id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc, error_message)
                VALUES($id, $conversationId, $role, '', $status, $occurredAt, $occurredAt, $error)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    updated_at_utc = excluded.updated_at_utc,
                    error_message = excluded.error_message
                WHERE messages.status = $streaming;
                """;
            messageCommand.Parameters.AddWithValue("$id", turnId.ToString("D"));
            messageCommand.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            messageCommand.Parameters.AddWithValue("$role", (int)MessageRole.Assistant);
            messageCommand.Parameters.AddWithValue("$status", (int)MessageStatus.Failed);
            messageCommand.Parameters.AddWithValue("$streaming", (int)MessageStatus.Streaming);
            messageCommand.Parameters.AddWithValue("$occurredAt", recoveredAtUtc.ToString("O"));
            messageCommand.Parameters.AddWithValue("$error", error);
            await messageCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var toolCommand = connection.CreateCommand();
        toolCommand.Transaction = transaction;
        toolCommand.CommandText = """
            UPDATE tool_runs
            SET status = $failed,
                result_summary = COALESCE(result_summary, $error),
                updated_at_utc = $occurredAt
            WHERE conversation_id = $conversationId
              AND message_id = $turnId
              AND status IN ($running, $awaitingApproval);
            """;
        toolCommand.Parameters.AddWithValue("$failed", (int)ToolExecutionStatus.Failed);
        toolCommand.Parameters.AddWithValue("$running", (int)ToolExecutionStatus.Running);
        toolCommand.Parameters.AddWithValue("$awaitingApproval", (int)ToolExecutionStatus.AwaitingApproval);
        toolCommand.Parameters.AddWithValue("$error", error);
        toolCommand.Parameters.AddWithValue("$occurredAt", recoveredAtUtc.ToString("O"));
        toolCommand.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        toolCommand.Parameters.AddWithValue("$turnId", turnId.ToString("D"));
        await toolCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SubagentDeliveryStatus ResolveTarget(
        SubagentDeliveryRecord delivery,
        SubagentDeliveryResolutionKind kind)
        => kind switch
        {
            SubagentDeliveryResolutionKind.Succeeded => SubagentDeliveryStatus.Delivered,
            SubagentDeliveryResolutionKind.RetryableFailure when delivery.AttemptCount < MaximumAttempts =>
                SubagentDeliveryStatus.Pending,
            SubagentDeliveryResolutionKind.RetryableFailure => SubagentDeliveryStatus.DeadLetter,
            SubagentDeliveryResolutionKind.UnsafeFailure or SubagentDeliveryResolutionKind.DeadLetter =>
                SubagentDeliveryStatus.DeadLetter,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static DateTimeOffset NextAttemptAt(
        SubagentDeliveryRecord delivery,
        SubagentDeliveryStatus target,
        DateTimeOffset occurredAtUtc)
    {
        if (target != SubagentDeliveryStatus.Pending)
        {
            return delivery.NextAttemptAtUtc;
        }

        return occurredAtUtc + (delivery.AttemptCount <= 1 ? FirstRetryDelay : SecondRetryDelay);
    }

    private static void AddResolutionId(
        SubagentDeliveryStatus status,
        Guid deliveryId,
        ICollection<Guid> delivered,
        ICollection<Guid> pending,
        ICollection<Guid> deadLettered)
    {
        switch (status)
        {
            case SubagentDeliveryStatus.Delivered:
                delivered.Add(deliveryId);
                break;
            case SubagentDeliveryStatus.Pending:
                pending.Add(deliveryId);
                break;
            case SubagentDeliveryStatus.DeadLetter:
                deadLettered.Add(deliveryId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }
    }

    private static void ValidateLeaseRequest(
        Guid leaseToken,
        Guid continuationTurnId,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leasedUntilUtc,
        int maximumBatchBytes)
    {
        if (leaseToken == Guid.Empty || continuationTurnId == Guid.Empty)
        {
            throw new ArgumentException("A delivery lease requires non-empty lease and continuation ids.");
        }

        if (leasedUntilUtc <= leasedAtUtc)
        {
            throw new ArgumentException("A delivery lease deadline must be later than its start.");
        }

        if (maximumBatchBytes < EmptyBatchBytes + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchBytes));
        }
    }

    private static void ValidateResolution(
        SubagentDeliveryLease lease,
        SubagentDeliveryResolution resolution)
    {
        if (lease.LeaseToken == Guid.Empty || lease.ContinuationTurnId == Guid.Empty || lease.Deliveries.Count == 0)
        {
            throw new ArgumentException("A delivery resolution requires a non-empty lease.", nameof(lease));
        }

        var requiresFinalization = resolution.Kind is SubagentDeliveryResolutionKind.Succeeded
            or SubagentDeliveryResolutionKind.UnsafeFailure;
        if (requiresFinalization != (resolution.TurnFinalization is not null))
        {
            throw new ArgumentException(
                "Successful and unsafe delivery resolutions require exactly one turn finalization.",
                nameof(resolution));
        }

        if (resolution.TurnFinalization is not TurnFinalization finalization)
        {
            return;
        }

        var expectedStatus = resolution.Kind == SubagentDeliveryResolutionKind.Succeeded
            ? MessageStatus.Completed
            : MessageStatus.Failed;
        if (finalization.AssistantMessage.Id != lease.ContinuationTurnId ||
            finalization.AssistantMessage.ConversationId != lease.ParentConversationId ||
            finalization.AssistantMessage.Status != expectedStatus ||
            finalization.ToolExecutions.Any(tool =>
                tool.ConversationId != lease.ParentConversationId ||
                tool.MessageId != lease.ContinuationTurnId))
        {
            throw new ArgumentException("The continuation finalization does not belong to its delivery lease.", nameof(resolution));
        }
    }

    private static string? NormalizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return error.Length <= MaximumErrorLength ? error : error[..MaximumErrorLength];
    }

    private static SubagentDeliveryResolutionResult LeaseMismatch()
        => new(false, [], [], []);

    private static SqliteCommand CreateDeliverySelectCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction = null,
        string? alias = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var prefix = alias is null ? string.Empty : alias + ".";
        command.CommandText = "SELECT " + string.Join(
            ", ",
            DeliveryColumns.Split(',', StringSplitOptions.TrimEntries).Select(column => prefix + column));
        command.CommandText += alias is null ? " FROM subagent_deliveries " : $" FROM subagent_deliveries {alias} ";
        return command;
    }

    private static SubagentDeliveryRecord ReadDelivery(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            ReadGuid(reader, 2),
            ReadGuid(reader, 3),
            (SubagentDeliveryStatus)reader.GetInt32(4),
            reader.GetString(5),
            reader.GetInt32(6),
            ReadNullableGuid(reader, 7),
            ReadNullableDateTimeOffset(reader, 8),
            reader.GetInt32(9),
            ReadDateTimeOffset(reader, 10),
            ReadNullableGuid(reader, 11),
            ReadNullableString(reader, 12),
            ReadDateTimeOffset(reader, 13),
            ReadDateTimeOffset(reader, 14),
            ReadNullableDateTimeOffset(reader, 15),
            ReadNullableDateTimeOffset(reader, 16));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader, ordinal);
}
