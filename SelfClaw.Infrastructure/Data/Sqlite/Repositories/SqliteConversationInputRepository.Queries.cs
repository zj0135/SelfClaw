using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed partial class SqliteConversationInputRepository
{
    private static async Task<long> CountGlobalUnprocessedAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM conversation_inputs WHERE status IN (0, 1, 3);";
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task<long> ReadQueueRevisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        return state.QueueRevision;
    }

    private static async Task<ConversationInputStateRow> ReadStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(s.paused, 0), s.pause_reason, COALESCE(s.queue_revision, 0),
                (SELECT COUNT(*) FROM conversation_inputs i WHERE i.conversation_id = $id AND i.status IN (0, 1, 3))
            FROM (SELECT 1) seed LEFT JOIN conversation_input_state s ON s.conversation_id = $id;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ConversationInputStateRow(false, null, 0, 0);
        }

        return new ConversationInputStateRow(
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2),
            checked((int)reader.GetInt64(3)));
    }

    private static async Task<ConversationInputQueueState> ReadQueueStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(connection, transaction, conversationId, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, sequence, kind, status, revision, payload_json, reason_code, error_message
            FROM conversation_inputs WHERE conversation_id = $id AND status IN (0, 1, 3)
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        var items = new List<ConversationInputQueueItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new ConversationInputQueueItem(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                (ConversationInputKind)reader.GetInt32(2),
                (ConversationInputStatus)reader.GetInt32(3),
                reader.GetInt32(4),
                CreatePreview(ReadPrompt(reader.GetString(5))),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return new ConversationInputQueueState(conversationId, state.QueueRevision, state.Paused, state.PauseReason, false, items);
    }

    private static async Task<ConversationInputRecord?> ReadByIdentityAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, string clientRequestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RecordSelectColumns} FROM conversation_inputs i WHERE i.conversation_id = $conversationId AND i.client_request_id = $requestId;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$requestId", clientRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static async Task<ConversationInputRecord?> ReadByIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid inputId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {RecordSelectColumns} FROM conversation_inputs i WHERE i.id = $id;";
        command.Parameters.AddWithValue("$id", inputId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    private static async Task<Guid?> ReadConversationIdAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid inputId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT conversation_id FROM conversation_inputs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", inputId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value ? Guid.Parse(value) : null;
    }

    private static async Task<int> ReadRevisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid inputId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM conversation_inputs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", inputId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    private static ConversationInputRecord ReadRecord(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            (ConversationInputKind)reader.GetInt32(4),
            reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)),
            (ConversationInputStatus)reader.GetInt32(6),
            reader.GetInt32(7),
            ReadPrompt(reader.GetString(8)),
            reader.IsDBNull(9) ? null : DeserializeSnapshot(reader.GetString(9)),
            reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
            DateTimeOffset.Parse(reader.GetString(14)),
            DateTimeOffset.Parse(reader.GetString(15)),
            reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            ReadPayload(reader.GetString(8)).RequestPrompt,
            ReadPayload(reader.GetString(8)).RequestFingerprint);

    private static string SerializePayload(string prompt, string? fingerprint = null)
        => JsonSerializer.Serialize(new ConversationInputPayload(1, prompt, prompt, fingerprint), PayloadJson);

    private static ConversationInputPayload ReadPayload(string json)
        => JsonSerializer.Deserialize<ConversationInputPayload>(json, PayloadJson)
            ?? throw new InvalidOperationException("Invalid input payload.");

    private static string SerializeSnapshot(ConversationInputExecutionSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, SnapshotJson);

    private static ConversationInputExecutionSnapshot DeserializeSnapshot(string json)
        => JsonSerializer.Deserialize<ConversationInputExecutionSnapshot>(json, SnapshotJson)
           ?? throw new InvalidOperationException("The execution snapshot format is not supported.");

    private static bool SnapshotEquals(ConversationInputExecutionSnapshot? left, ConversationInputExecutionSnapshot? right)
        => (left is null && right is null) ||
           (left is not null && right is not null && string.Equals(SerializeSnapshot(left), SerializeSnapshot(right), StringComparison.Ordinal));

    private static string CreatePreview(string prompt)
    {
        var normalized = prompt.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaximumPreviewLength ? normalized : normalized[..MaximumPreviewLength];
    }

    private static string ReadPrompt(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("version", out var version) || !version.TryGetInt32(out var value) || value != 1 ||
            !document.RootElement.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("The input payload format is not supported.");
        }

        return prompt.GetString() ?? throw new InvalidOperationException("An input prompt cannot be null.");
    }
}
