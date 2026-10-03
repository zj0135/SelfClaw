using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteConversationTurnRepository : IConversationTurnRepository
{
    private readonly SqliteDatabase _database;
    private readonly Func<string, CancellationToken, Task>? _beforeStartCommit;

    internal Func<string, CancellationToken, Task>? BeforeFinalizeCommit { get; set; }

    public SqliteConversationTurnRepository(SqliteDatabase database)
    {
        _database = database;
    }

    internal SqliteConversationTurnRepository(SqliteDatabase database, Func<string, CancellationToken, Task> beforeStartCommit)
        : this(database)
    {
        _beforeStartCommit = beforeStartCommit;
    }

    public async Task<ConversationTurnCommit> StartTurnAsync(
        ConversationTurnStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await SqliteConversationTurnWriter.StartAsync(connection, transaction, start, cancellationToken).ConfigureAwait(false);
        if (_beforeStartCommit is not null)
        {
            await _beforeStartCommit("start-before-commit", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<long> ReserveMessageSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var sequence = await SqliteConversationTurnWriter.ReserveSequenceAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return sequence;
    }

    public async Task<IReadOnlyList<ConversationTurnRecord>> ListTurnsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteMappings.TurnSelectColumns}
            FROM conversation_turns t LEFT JOIN turn_usage u ON u.turn_id = t.id
            WHERE t.conversation_id = $conversationId
            ORDER BY t.started_at_utc, t.id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        var turns = new List<ConversationTurnRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(SqliteMappings.ReadTurn(reader));
        }

        return turns;
    }

    public async Task CommitProgressAsync(ConversationTurnCommit progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Turn.Status != ConversationTurnStatus.Running || progress.Turn.Origin == DirectTurnOrigin.Continuation)
        {
            throw new ArgumentException("Only a persisted Running, non-detached turn can commit progress.", nameof(progress));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await SqliteConversationTurnWriter.TryWriteAsync(connection, transaction, progress, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The turn is missing or already terminal.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TryFinalizeTurnAsync(ConversationTurnCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Turn.Status == ConversationTurnStatus.Running || commit.Turn.Origin == DirectTurnOrigin.Continuation)
        {
            throw new ArgumentException("Finalization requires a terminal turn; detached continuations finalize through delivery resolution.", nameof(commit));
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (!await SqliteConversationTurnWriter.TryWriteAsync(connection, transaction, commit, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (BeforeFinalizeCommit is not null)
        {
            await BeforeFinalizeCommit("finalize-before-commit", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
