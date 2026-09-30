using Microsoft.Data.Sqlite;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Session.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Agents.Cli.Session;

/// <summary>
/// SQLite-backed <see cref="ICliAgentSessionStore"/> persisting the resume id a CLI agent associates
/// with a conversation. Rows live in the <c>cli_agent_sessions</c> table, keyed by
/// <c>(conversation_id, agent_kind)</c> so a single conversation can target different CLIs over its
/// lifetime without their session ids colliding.
/// </summary>
internal sealed class SqliteCliAgentSessionStore : ICliAgentSessionStore
{
    private readonly SqliteDatabase _database;

    public SqliteCliAgentSessionStore(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<string?> GetSessionIdAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT session_id
FROM cli_agent_sessions
WHERE conversation_id = $conversationId AND agent_kind = $agentKind
LIMIT 1;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$agentKind", (int)agentKind);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }

    public async Task SetSessionIdAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id must be a non-empty value.", nameof(sessionId));

        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO cli_agent_sessions(conversation_id, agent_kind, session_id, cost_baseline_usd_micros, created_at_utc, updated_at_utc)
VALUES($conversationId, $agentKind, $sessionId, 0, $now, $now)
ON CONFLICT(conversation_id, agent_kind) DO UPDATE SET
    session_id = excluded.session_id,
    cost_baseline_usd_micros = CASE
        WHEN cli_agent_sessions.session_id = excluded.session_id THEN cli_agent_sessions.cost_baseline_usd_micros
        ELSE 0
    END,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$agentKind", (int)agentKind);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> TrackCumulativeCostAsync(
        Guid conversationId,
        CliAgentKind agentKind,
        string sessionId,
        long cumulativeCostUsdMicros,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (cumulativeCostUsdMicros < 0)
            throw new ArgumentOutOfRangeException(
                nameof(cumulativeCostUsdMicros),
                "A cumulative session cost cannot be negative.");

        // A zeroed result (crash and startup-error paths report zeroed usage) must not erase the
        // baseline of a live session; the next non-zero result still differences correctly.
        if (cumulativeCostUsdMicros == 0)
            return 0;

        await using var connection = await _database
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var baseline = await ReadCostBaselineAsync(
                connection,
                transaction,
                conversationId,
                agentKind,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);

        // A cumulative value below the baseline means the session reset (for example a mid-session
        // clear): the reported value is then the whole spend since the reset.
        var delta = cumulativeCostUsdMicros >= baseline ? cumulativeCostUsdMicros - baseline : cumulativeCostUsdMicros;
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = @"
INSERT INTO cli_agent_sessions(conversation_id, agent_kind, session_id, cost_baseline_usd_micros, created_at_utc, updated_at_utc)
VALUES($conversationId, $agentKind, $sessionId, $cumulative, $now, $now)
ON CONFLICT(conversation_id, agent_kind) DO UPDATE SET
    session_id = excluded.session_id,
    cost_baseline_usd_micros = excluded.cost_baseline_usd_micros,
    updated_at_utc = excluded.updated_at_utc;";
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$agentKind", (int)agentKind);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$cumulative", cumulativeCostUsdMicros);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return delta;
    }

    private static async Task<long> ReadCostBaselineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid conversationId,
        CliAgentKind agentKind,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
SELECT session_id, cost_baseline_usd_micros
FROM cli_agent_sessions
WHERE conversation_id = $conversationId AND agent_kind = $agentKind
LIMIT 1;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$agentKind", (int)agentKind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return 0;

        // A different session id means the CLI started a fresh session, so its reported total restarts.
        return string.Equals(reader.GetString(0), sessionId, StringComparison.Ordinal)
            ? reader.GetInt64(1)
            : 0;
    }
}
