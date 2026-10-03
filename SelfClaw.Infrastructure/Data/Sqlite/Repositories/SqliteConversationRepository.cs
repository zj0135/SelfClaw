using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteConversationRepository : IConversationRepository
{
    private readonly SqliteDatabase _database;

    public SqliteConversationRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _database.EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadConversationsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, workspace_root_id, mode, tool_permission_mode,
       agent_id, channel_kind, channel_conversation_id, channel_display_name,
       created_at_utc, updated_at_utc, kind, parent_conversation_id
FROM conversations
WHERE id = $id
LIMIT 1;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? SqliteMappings.ReadConversation(reader)
            : null;
    }

    public async Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ValidateConversationOwnership(conversation);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await SqliteConversationTurnWriter.UpsertConversationAsync(connection, transaction, conversation, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT {SqliteMappings.MessageSelectColumns}
FROM messages m
WHERE m.conversation_id = $conversationId
ORDER BY m.sequence ASC;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));

        var results = new List<MessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteMappings.ReadMessage(reader));
        }

        if (results.Count == 0)
        {
            return results;
        }

        var attachmentsByMessageId = await ReadMessageAttachmentsAsync(
            connection,
            results.Select(item => item.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var segmentsByMessageId = await ReadMessageSegmentsAsync(
            connection,
            results.Select(item => item.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);

        return results
            .Select(message => message with
            {
                Attachments = attachmentsByMessageId.TryGetValue(message.Id, out var attachments)
                    ? attachments
                    : message.Attachments,
                Segments = message.Role == MessageRole.Assistant
                    ? segmentsByMessageId.TryGetValue(message.Id, out var segments)
                        ? segments
                        : message.Segments
                    : null
            })
            .ToArray();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<MessageSegmentRecord>>> ReadMessageSegmentsAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        var parameterNames = messageIds
            .Select((_, index) => "$messageId" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT message_id, ordinal, kind, text, tool_run_id
FROM message_segments
WHERE message_id IN ({string.Join(", ", parameterNames)})
ORDER BY message_id, ordinal ASC;";

        for (var index = 0; index < messageIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], messageIds[index].ToString("D"));
        }

        var results = new Dictionary<Guid, List<MessageSegmentRecord>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var segment = new MessageSegmentRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                (MessageSegmentKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)));
            if (!results.TryGetValue(segment.MessageId, out var segments))
            {
                segments = [];
                results[segment.MessageId] = segments;
            }

            segments.Add(segment);
        }

        return results.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<MessageSegmentRecord>)item.Value.ToArray());
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<MessageAttachmentRecord>>> ReadMessageAttachmentsAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> messageIds,
        CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        var parameterNames = messageIds
            .Select((_, index) => "$messageId" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
SELECT id, message_id, kind, file_name, media_type, storage_path, byte_length, created_at_utc
FROM message_attachments
WHERE message_id IN ({string.Join(", ", parameterNames)})
ORDER BY created_at_utc ASC;";

        for (var index = 0; index < messageIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], messageIds[index].ToString("D"));
        }

        var results = new Dictionary<Guid, List<MessageAttachmentRecord>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var attachment = SqliteMappings.ReadMessageAttachment(reader);
            if (!results.TryGetValue(attachment.MessageId, out var attachments))
            {
                attachments = [];
                results[attachment.MessageId] = attachments;
            }

            attachments.Add(attachment);
        }

        return results.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<MessageAttachmentRecord>)item.Value.ToArray());
    }

    public async Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, result_content, source_kind, source_id, display_name, effective_arguments_json, hook_feedback_json, hook_outcome_json
FROM tool_runs
WHERE conversation_id = $conversationId
ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));

        var results = new List<ToolExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteMappings.ReadToolRun(reader));
        }

        return results;
    }

    private static async Task<List<ConversationRecord>> ReadConversationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, workspace_root_id, mode, tool_permission_mode,
       agent_id, channel_kind, channel_conversation_id, channel_display_name,
       created_at_utc, updated_at_utc, kind, parent_conversation_id
FROM conversations
WHERE kind = $interactiveKind
ORDER BY updated_at_utc DESC;";
        command.Parameters.AddWithValue("$interactiveKind", (int)ConversationKind.Interactive);

        var results = new List<ConversationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteMappings.ReadConversation(reader));
        }

        return results;
    }

    private static void ValidateConversationOwnership(ConversationRecord conversation)
    {
        var valid = conversation.Kind switch
        {
            ConversationKind.Interactive => conversation.ParentConversationId is null,
            ConversationKind.Subagent => conversation.ParentConversationId is not null,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                "Interactive conversations cannot have a parent and Subagent conversations require one.",
                nameof(conversation));
        }
    }
}
