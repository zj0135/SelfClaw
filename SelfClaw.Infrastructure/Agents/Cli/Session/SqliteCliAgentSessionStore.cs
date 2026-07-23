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
INSERT INTO cli_agent_sessions(conversation_id, agent_kind, session_id, created_at_utc, updated_at_utc)
VALUES($conversationId, $agentKind, $sessionId, $now, $now)
ON CONFLICT(conversation_id, agent_kind) DO UPDATE SET
    session_id = excluded.session_id,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$agentKind", (int)agentKind);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
