using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteConversationCompactionRepository : IConversationCompactionRepository
{
    private readonly SqliteDatabase _database;

    public SqliteConversationCompactionRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<ConversationCompactionRecord>> ListAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteMappings.CompactionSelectColumns}
            FROM conversation_compactions c
            WHERE c.conversation_id = $conversationId
            ORDER BY c.created_at_utc, c.id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        var compactions = new List<ConversationCompactionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            compactions.Add(SqliteMappings.ReadCompaction(reader));
        }

        return compactions;
    }

    public async Task<ConversationCompactionRecord?> GetLatestAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        // The replay boundary decides which summary is current: a later timestamp with an older boundary
        // would replay messages that a newer compaction already summarized. Time is only a tie-break.
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SqliteMappings.CompactionSelectColumns}
            FROM conversation_compactions c
            WHERE c.conversation_id = $conversationId
            ORDER BY c.through_sequence DESC, c.created_at_utc DESC, c.id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteMappings.ReadCompaction(reader)
            : null;
    }
}
