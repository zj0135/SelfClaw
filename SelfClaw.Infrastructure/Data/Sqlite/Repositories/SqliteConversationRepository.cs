using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteConversationRepository : IConversationRepository, ITurnFinalizationRepository
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
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO conversations(
    id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
    channel_kind, channel_conversation_id, channel_display_name,
    created_at_utc, updated_at_utc, kind, parent_conversation_id)
VALUES(
    $id, $title, $workspaceRootId, $mode, $toolPermissionMode, $agentId,
    $channelKind, $channelConversationId, $channelDisplayName,
    $createdAt, $updatedAt, $kind, $parentConversationId)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title,
    workspace_root_id = excluded.workspace_root_id,
    mode = excluded.mode,
    tool_permission_mode = excluded.tool_permission_mode,
    agent_id = excluded.agent_id,
    channel_kind = excluded.channel_kind,
    channel_conversation_id = excluded.channel_conversation_id,
    channel_display_name = excluded.channel_display_name,
    kind = excluded.kind,
    parent_conversation_id = excluded.parent_conversation_id,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", conversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$workspaceRootId", conversation.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$toolPermissionMode", (int)conversation.ToolPermissionMode);
        command.Parameters.AddWithValue("$agentId", conversation.AgentId);
        command.Parameters.AddWithValue("$channelKind", conversation.ChannelKind ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelConversationId", conversation.ChannelConversationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$channelDisplayName", conversation.ChannelDisplayName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$kind", (int)conversation.Kind);
        command.Parameters.AddWithValue(
            "$parentConversationId",
            conversation.ParentConversationId?.ToString("D") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc, agent_id, agent_name, agent_role, input_tokens, output_tokens, duration_ms, error_message
FROM messages
WHERE conversation_id = $conversationId
ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));

        var results = new List<MessageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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
            cancellationToken);

        return results
            .Select(message => attachmentsByMessageId.TryGetValue(message.Id, out var attachments)
                ? message with { Attachments = attachments }
                : message)
            .ToArray();
    }

    public async Task<MessageRecord> UpsertMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SqliteTurnFinalizationWriter.UpsertMessageAsync(
                connection,
                transaction: null,
                message,
                onlyIfStreaming: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (message.Attachments is not null)
        {
            await ReplaceMessageAttachmentsAsync(connection, message, cancellationToken).ConfigureAwait(false);
        }

        return message;
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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

    private static async Task ReplaceMessageAttachmentsAsync(
        SqliteConnection connection,
        MessageRecord message,
        CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.CommandText = "DELETE FROM message_attachments WHERE message_id = $messageId;";
            deleteCommand.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (message.Attachments is not { Count: > 0 } attachments)
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
INSERT INTO message_attachments(id, message_id, kind, file_name, media_type, storage_path, byte_length, created_at_utc)
VALUES($id, $messageId, $kind, $fileName, $mediaType, $storagePath, $byteLength, $createdAt);";
            insertCommand.Parameters.AddWithValue("$id", attachment.Id.ToString("D"));
            insertCommand.Parameters.AddWithValue("$messageId", message.Id.ToString("D"));
            insertCommand.Parameters.AddWithValue("$kind", (int)attachment.Kind);
            insertCommand.Parameters.AddWithValue("$fileName", attachment.FileName);
            insertCommand.Parameters.AddWithValue("$mediaType", attachment.MediaType);
            insertCommand.Parameters.AddWithValue("$storagePath", attachment.StoragePath);
            insertCommand.Parameters.AddWithValue("$byteLength", attachment.ByteLength);
            insertCommand.Parameters.AddWithValue("$createdAt", attachment.CreatedAtUtc.ToString("O"));
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, after_segment_index, result_content, source_kind, source_id, display_name
FROM tool_runs
WHERE conversation_id = $conversationId
ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));

        var results = new List<ToolExecutionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadToolRun(reader));
        }

        return results;
    }

    public async Task<ToolExecutionRecord> UpsertToolExecutionAsync(ToolExecutionRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SqliteTurnFinalizationWriter.UpsertToolExecutionAsync(
                connection,
                transaction: null,
                record,
                cancellationToken)
            .ConfigureAwait(false);
        return record;
    }

    public async Task<bool> TryFinalizeTurnAsync(
        TurnFinalization finalization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalization);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sqliteTransaction = (SqliteTransaction)transaction;
        var messageWritten = await SqliteTurnFinalizationWriter.TryWriteAsync(
                connection,
                sqliteTransaction,
                finalization,
                cancellationToken)
            .ConfigureAwait(false);
        if (!messageWritten)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, name, root_path, created_at_utc, updated_at_utc
FROM workspace_roots
ORDER BY updated_at_utc DESC;";

        var results = new List<WorkspaceRoot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadWorkspaceRoot(reader));
        }

        return results;
    }

    public async Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO workspace_roots(id, name, root_path, created_at_utc, updated_at_utc)
VALUES($id, $name, $rootPath, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    root_path = excluded.root_path,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", workspaceRoot.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", workspaceRoot.Name);
        command.Parameters.AddWithValue("$rootPath", workspaceRoot.RootPath);
        command.Parameters.AddWithValue("$createdAt", workspaceRoot.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", workspaceRoot.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return workspaceRoot;
    }

    public async Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM workspace_roots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", workspaceRootId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
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
