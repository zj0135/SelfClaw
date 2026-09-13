using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Subagents.Persistence.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Agents.Subagents.Persistence;

internal sealed class SqliteSubagentActivityReader : ISubagentActivityReader
{
    private readonly SqliteDatabase _database;

    public SqliteSubagentActivityReader(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<SubagentActivityPage> ListAsync(SubagentActivityQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PageSize is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "A task page contains at most 50 tasks.");
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var (counts, version) = await ReadIndexAsync(connection, transaction, query, cancellationToken).ConfigureAwait(false);
        var offset = ReadOffset(query, version, counts.Total);
        var tasks = await SqliteSubagentActivityQueries.ReadTasksAsync(
            connection, transaction, query, offset, cancellationToken).ConfigureAwait(false);
        var nextOffset = offset + tasks.Count;
        var cursor = nextOffset < counts.Total
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SubagentActivityCursor(
                query.ParentConversationId, query.ParentTurnId, version, nextOffset)))
            : null;
        return new SubagentActivityPage(counts, version, tasks, cursor);
    }

    public async Task<SubagentActivityDetail?> GetDetailAsync(
        Guid parentConversationId, Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var task = await SqliteSubagentActivityQueries.ReadTaskAsync(
            connection, transaction, parentConversationId, taskId, cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return null;
        }

        var taskText = await SqliteSubagentActivityQueries.ReadTaskTextAsync(
            connection, transaction, taskId, cancellationToken).ConfigureAwait(false);
        var message = await SqliteSubagentActivityQueries.ReadMessageAsync(
            connection, transaction, task, cancellationToken).ConfigureAwait(false);
        var tools = await SqliteSubagentActivityQueries.ReadToolsAsync(
            connection, transaction, task, cancellationToken).ConfigureAwait(false);
        return new SubagentActivityDetail(task, SubagentActivityContent.Create(task.Status, taskText, message, tools));
    }

    private static async Task<(SubagentActivityCounts Counts, string Version)> ReadIndexAsync(
        SqliteConnection connection, SqliteTransaction transaction, SubagentActivityQuery query, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.id, t.status, t.queued_at_utc
            FROM subagent_tasks t
            JOIN conversations p ON p.id = t.parent_conversation_id AND p.kind = $interactive
            WHERE t.parent_conversation_id = $parent AND ($turn IS NULL OR t.parent_turn_id = $turn)
            ORDER BY CASE WHEN t.status IN (0, 1) THEN 0 ELSE 1 END, t.queued_at_utc, t.id;
            """;
        command.Parameters.AddWithValue("$interactive", (int)ConversationKind.Interactive);
        command.Parameters.AddWithValue("$parent", query.ParentConversationId.ToString("D"));
        command.Parameters.AddWithValue("$turn", query.ParentTurnId?.ToString("D") ?? (object)DBNull.Value);
        var counts = new int[6];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = reader.GetInt32(1);
            counts[status]++;
            var group = status is 0 or 1 ? "active" : "terminal";
            hash.AppendData(Encoding.UTF8.GetBytes($"{reader.GetString(0)}|{group}|{reader.GetString(2)}\n"));
        }

        return (new SubagentActivityCounts(counts.Sum(), counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]),
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static int ReadOffset(SubagentActivityQuery query, string version, int total)
    {
        if (query.Cursor is null)
        {
            return 0;
        }

        SubagentActivityCursor? cursor;
        try
        {
            if (query.Cursor.Length > 1024)
            {
                throw new SubagentActivityReadException("invalid-task-cursor");
            }

            cursor = JsonSerializer.Deserialize<SubagentActivityCursor>(Convert.FromBase64String(query.Cursor));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new SubagentActivityReadException("invalid-task-cursor");
        }

        if (cursor is null || cursor.ParentConversationId != query.ParentConversationId || cursor.ParentTurnId != query.ParentTurnId)
        {
            throw new SubagentActivityReadException("invalid-task-cursor");
        }

        if (cursor.ListVersion != version)
        {
            throw new SubagentActivityReadException("task-list-changed");
        }

        if (cursor.Offset < 0 || cursor.Offset > total)
        {
            throw new SubagentActivityReadException("invalid-task-cursor");
        }

        return cursor.Offset;
    }
}
