using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data;

namespace SelfClaw.Infrastructure.Repositories;

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
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var results = await ReadConversationsAsync(connection, cancellationToken);
        if (await ConsolidateDuplicateAgentConversationsAsync(connection, results, cancellationToken))
        {
            results = await ReadConversationsAsync(connection, cancellationToken);
        }

        return CollapseDuplicateAgentConversations(results);
    }

    public async Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, profile_id, workspace_root_id, mode, tool_permission_mode, team_max_rounds, team_output_mode,
       parent_conversation_id, root_conversation_id, bound_agent_id, bound_agent_name, bound_agent_role,
       created_at_utc, updated_at_utc
FROM conversations
WHERE id = $id
LIMIT 1;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? SqliteMappings.ReadConversation(reader)
            : null;
    }

    public async Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var effectiveConversation = await ReuseExistingAgentConversationAsync(connection, conversation, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO conversations(
    id, title, profile_id, workspace_root_id, mode, tool_permission_mode, team_max_rounds, team_output_mode,
    parent_conversation_id, root_conversation_id, bound_agent_id, bound_agent_name, bound_agent_role,
    created_at_utc, updated_at_utc)
VALUES(
    $id, $title, $profileId, $workspaceRootId, $mode, $toolPermissionMode, $teamMaxRounds, $teamOutputMode,
    $parentConversationId, $rootConversationId, $boundAgentId, $boundAgentName, $boundAgentRole,
    $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    title = excluded.title,
    profile_id = excluded.profile_id,
    workspace_root_id = excluded.workspace_root_id,
    mode = excluded.mode,
    tool_permission_mode = excluded.tool_permission_mode,
    team_max_rounds = excluded.team_max_rounds,
    team_output_mode = excluded.team_output_mode,
    parent_conversation_id = excluded.parent_conversation_id,
    root_conversation_id = excluded.root_conversation_id,
    bound_agent_id = excluded.bound_agent_id,
    bound_agent_name = excluded.bound_agent_name,
    bound_agent_role = excluded.bound_agent_role,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", effectiveConversation.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", effectiveConversation.Title);
        command.Parameters.AddWithValue("$profileId", effectiveConversation.ProfileId.ToString("D"));
        command.Parameters.AddWithValue("$workspaceRootId", effectiveConversation.WorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$mode", (int)effectiveConversation.Mode);
        command.Parameters.AddWithValue("$toolPermissionMode", (int)effectiveConversation.ToolPermissionMode);
        command.Parameters.AddWithValue("$teamMaxRounds", TeamDiscussionDefaults.ClampRounds(effectiveConversation.TeamMaxRounds));
        command.Parameters.AddWithValue("$teamOutputMode", (int)effectiveConversation.TeamOutputMode);
        command.Parameters.AddWithValue("$parentConversationId", effectiveConversation.ParentConversationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$rootConversationId", effectiveConversation.RootConversationId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$boundAgentId", effectiveConversation.BoundAgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$boundAgentName", effectiveConversation.BoundAgentName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$boundAgentRole", effectiveConversation.BoundAgentRole ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", effectiveConversation.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", effectiveConversation.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return effectiveConversation;
    }

    public async Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
DELETE FROM conversations
WHERE id = $id
   OR parent_conversation_id = $id
   OR root_conversation_id = $id;";
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

        return results;
    }

    public async Task<MessageRecord> UpsertMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO messages(id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc, agent_id, agent_name, agent_role, input_tokens, output_tokens, duration_ms, error_message)
VALUES($id, $conversationId, $role, $markdownContent, $status, $createdAt, $updatedAt, $agentId, $agentName, $agentRole, $inputTokens, $outputTokens, $durationMs, $errorMessage)
ON CONFLICT(id) DO UPDATE SET
    markdown_content = excluded.markdown_content,
    status = excluded.status,
    updated_at_utc = excluded.updated_at_utc,
    agent_id = excluded.agent_id,
    agent_name = excluded.agent_name,
    agent_role = excluded.agent_role,
    input_tokens = excluded.input_tokens,
    output_tokens = excluded.output_tokens,
    duration_ms = excluded.duration_ms,
    error_message = excluded.error_message;";
        command.Parameters.AddWithValue("$id", message.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", message.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$role", (int)message.Role);
        command.Parameters.AddWithValue("$markdownContent", message.MarkdownContent);
        command.Parameters.AddWithValue("$status", (int)message.Status);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", message.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", message.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentName", message.AgentName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$agentRole", message.AgentRole ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$inputTokens", message.InputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$outputTokens", message.OutputTokens ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", message.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", message.ErrorMessage ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return message;
    }

    public async Task<IReadOnlyList<TeamAgentRecord>> ListTeamAgentsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
WITH ranked AS (
    SELECT id,
           conversation_id,
           name,
           role,
           goal_prompt,
           status,
           sort_order,
           created_at_utc,
           updated_at_utc,
           ROW_NUMBER() OVER (
               PARTITION BY conversation_id, lower(trim(name)), lower(trim(role))
               ORDER BY sort_order ASC, updated_at_utc DESC, created_at_utc ASC, id ASC
           ) AS rn
    FROM team_agents
    WHERE conversation_id = $conversationId
)
SELECT id, conversation_id, name, role, goal_prompt, status, sort_order, created_at_utc, updated_at_utc
FROM ranked
WHERE rn = 1
ORDER BY sort_order ASC, created_at_utc ASC;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));

        var results = new List<TeamAgentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadTeamAgent(reader));
        }

        return results;
    }

    public async Task<TeamAgentRecord> UpsertTeamAgentAsync(TeamAgentRecord teamAgent, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        var effectiveTeamAgent = teamAgent;

        await using (var existingCommand = connection.CreateCommand())
        {
            existingCommand.CommandText = @"
SELECT id, conversation_id, name, role, goal_prompt, status, sort_order, created_at_utc, updated_at_utc
FROM team_agents
WHERE conversation_id = $conversationId
  AND lower(trim(name)) = lower(trim($name))
  AND lower(trim(role)) = lower(trim($role))
ORDER BY sort_order ASC, updated_at_utc DESC, created_at_utc ASC, id ASC
LIMIT 1;";
            existingCommand.Parameters.AddWithValue("$conversationId", teamAgent.ConversationId.ToString("D"));
            existingCommand.Parameters.AddWithValue("$name", teamAgent.Name);
            existingCommand.Parameters.AddWithValue("$role", teamAgent.Role);

            await using var existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await existingReader.ReadAsync(cancellationToken))
            {
                var existing = SqliteMappings.ReadTeamAgent(existingReader);
                effectiveTeamAgent = teamAgent with
                {
                    Id = existing.Id,
                    CreatedAtUtc = existing.CreatedAtUtc
                };
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO team_agents(id, conversation_id, name, role, goal_prompt, status, sort_order, created_at_utc, updated_at_utc)
VALUES($id, $conversationId, $name, $role, $goalPrompt, $status, $sortOrder, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    role = excluded.role,
    goal_prompt = excluded.goal_prompt,
    status = excluded.status,
    sort_order = excluded.sort_order,
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", effectiveTeamAgent.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", effectiveTeamAgent.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$name", effectiveTeamAgent.Name);
        command.Parameters.AddWithValue("$role", effectiveTeamAgent.Role);
        command.Parameters.AddWithValue("$goalPrompt", effectiveTeamAgent.GoalPrompt);
        command.Parameters.AddWithValue("$status", (int)effectiveTeamAgent.Status);
        command.Parameters.AddWithValue("$sortOrder", effectiveTeamAgent.SortOrder);
        command.Parameters.AddWithValue("$createdAt", effectiveTeamAgent.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", effectiveTeamAgent.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return effectiveTeamAgent;
    }

    public async Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, after_segment_index
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
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, result_summary, correlation_id, duration_ms, created_at_utc, updated_at_utc, agent_id, message_id, after_segment_index)
VALUES($id, $conversationId, $toolName, $argumentsJson, $status, $resultSummary, $correlationId, $durationMs, $createdAt, $updatedAt, $agentId, $messageId, $afterSegmentIndex)
ON CONFLICT(id) DO UPDATE SET
    status = excluded.status,
    result_summary = excluded.result_summary,
    duration_ms = excluded.duration_ms,
    agent_id = COALESCE(excluded.agent_id, tool_runs.agent_id),
    message_id = COALESCE(excluded.message_id, tool_runs.message_id),
    after_segment_index = COALESCE(excluded.after_segment_index, tool_runs.after_segment_index),
    updated_at_utc = excluded.updated_at_utc;";
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$conversationId", record.ConversationId.ToString("D"));
        command.Parameters.AddWithValue("$toolName", record.ToolName);
        command.Parameters.AddWithValue("$argumentsJson", record.ArgumentsJson);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$resultSummary", record.ResultSummary ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", record.CorrelationId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$durationMs", record.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", record.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", record.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$agentId", record.AgentId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$messageId", record.MessageId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$afterSegmentIndex", record.AfterSegmentIndex ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return record;
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

    private static async Task<ConversationRecord> ReuseExistingAgentConversationAsync(
        SqliteConnection connection,
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        if (!conversation.IsAgentConversation)
        {
            return conversation;
        }

        if (conversation.BoundAgentId is Guid boundAgentId)
        {
            var matchesById = await FindExistingAgentConversationsByAgentIdAsync(
                connection,
                conversation.EffectiveRootConversationId,
                boundAgentId,
                cancellationToken);
            if (matchesById.Count > 0)
            {
                var canonicalById = matchesById[0];
                await MergeDuplicateAgentConversationsAsync(connection, canonicalById, matchesById.Skip(1), cancellationToken);
                return conversation with
                {
                    Id = canonicalById.Id,
                    CreatedAtUtc = canonicalById.CreatedAtUtc
                };
            }
        }

        if (string.IsNullOrWhiteSpace(conversation.BoundAgentName) ||
            string.IsNullOrWhiteSpace(conversation.BoundAgentRole))
        {
            return conversation;
        }

        var matchesByName = await FindExistingAgentConversationsByNameAsync(
            connection,
            conversation.EffectiveRootConversationId,
            conversation.BoundAgentName,
            conversation.BoundAgentRole,
            cancellationToken);
        if (matchesByName.Count == 0)
        {
            return conversation;
        }

        var canonicalByName = matchesByName[0];
        await MergeDuplicateAgentConversationsAsync(connection, canonicalByName, matchesByName.Skip(1), cancellationToken);
        return conversation with
        {
            Id = canonicalByName.Id,
            CreatedAtUtc = canonicalByName.CreatedAtUtc
        };
    }

    private static async Task<IReadOnlyList<ConversationRecord>> FindExistingAgentConversationsByAgentIdAsync(
        SqliteConnection connection,
        Guid rootConversationId,
        Guid boundAgentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, profile_id, workspace_root_id, mode, tool_permission_mode, team_max_rounds, team_output_mode,
       parent_conversation_id, root_conversation_id, bound_agent_id, bound_agent_name, bound_agent_role,
       created_at_utc, updated_at_utc
FROM conversations
WHERE parent_conversation_id IS NOT NULL
  AND COALESCE(root_conversation_id, parent_conversation_id, id) = $rootConversationId
  AND bound_agent_id = $boundAgentId
ORDER BY updated_at_utc DESC, created_at_utc ASC, id ASC;";
        command.Parameters.AddWithValue("$rootConversationId", rootConversationId.ToString("D"));
        command.Parameters.AddWithValue("$boundAgentId", boundAgentId.ToString("D"));

        var results = new List<ConversationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadConversation(reader));
        }

        return CollapseDuplicateAgentConversations(results);
    }

    private static async Task<IReadOnlyList<ConversationRecord>> FindExistingAgentConversationsByNameAsync(
        SqliteConnection connection,
        Guid rootConversationId,
        string boundAgentName,
        string boundAgentRole,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, profile_id, workspace_root_id, mode, tool_permission_mode, team_max_rounds, team_output_mode,
       parent_conversation_id, root_conversation_id, bound_agent_id, bound_agent_name, bound_agent_role,
       created_at_utc, updated_at_utc
FROM conversations
WHERE parent_conversation_id IS NOT NULL
  AND COALESCE(root_conversation_id, parent_conversation_id, id) = $rootConversationId
  AND lower(trim(bound_agent_name)) = lower(trim($boundAgentName))
  AND lower(trim(bound_agent_role)) = lower(trim($boundAgentRole))
ORDER BY updated_at_utc DESC, created_at_utc ASC, id ASC;";
        command.Parameters.AddWithValue("$rootConversationId", rootConversationId.ToString("D"));
        command.Parameters.AddWithValue("$boundAgentName", boundAgentName);
        command.Parameters.AddWithValue("$boundAgentRole", boundAgentRole);

        var results = new List<ConversationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadConversation(reader));
        }

        return results;
    }

    private static async Task MergeDuplicateAgentConversationsAsync(
        SqliteConnection connection,
        ConversationRecord canonical,
        IEnumerable<ConversationRecord> duplicates,
        CancellationToken cancellationToken)
    {
        foreach (var duplicate in duplicates)
        {
            await ReassignConversationReferencesAsync(connection, "messages", duplicate.Id, canonical.Id, cancellationToken);
            await ReassignConversationReferencesAsync(connection, "tool_runs", duplicate.Id, canonical.Id, cancellationToken);

            await using var deleteCommand = connection.CreateCommand();
            deleteCommand.CommandText = @"
DELETE FROM conversations
WHERE id = $duplicateConversationId;";
            deleteCommand.Parameters.AddWithValue("$duplicateConversationId", duplicate.Id.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReassignConversationReferencesAsync(
        SqliteConnection connection,
        string tableName,
        Guid sourceConversationId,
        Guid targetConversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $@"
UPDATE {tableName}
SET conversation_id = $targetConversationId
WHERE conversation_id = $sourceConversationId;";
        command.Parameters.AddWithValue("$targetConversationId", targetConversationId.ToString("D"));
        command.Parameters.AddWithValue("$sourceConversationId", sourceConversationId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<ConversationRecord>> ReadConversationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT id, title, profile_id, workspace_root_id, mode, tool_permission_mode, team_max_rounds, team_output_mode,
       parent_conversation_id, root_conversation_id, bound_agent_id, bound_agent_name, bound_agent_role,
       created_at_utc, updated_at_utc
FROM conversations
ORDER BY updated_at_utc DESC;";

        var results = new List<ConversationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(SqliteMappings.ReadConversation(reader));
        }

        return results;
    }

    private static async Task<bool> ConsolidateDuplicateAgentConversationsAsync(
        SqliteConnection connection,
        IEnumerable<ConversationRecord> conversations,
        CancellationToken cancellationToken)
    {
        var duplicateGroups = conversations
            .Where(item => item.IsAgentConversation)
            .GroupBy(GetConversationIdentity)
            .Select(group => group
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .ToArray())
            .Where(group => group.Length > 1)
            .ToArray();
        if (duplicateGroups.Length == 0)
        {
            return false;
        }

        foreach (var group in duplicateGroups)
        {
            await MergeDuplicateAgentConversationsAsync(connection, group[0], group.Skip(1), cancellationToken);
        }

        return true;
    }

    private static IReadOnlyList<ConversationRecord> CollapseDuplicateAgentConversations(IEnumerable<ConversationRecord> conversations)
        => conversations
            .GroupBy(GetConversationIdentity)
            .Select(group => group
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .First())
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToArray();

    private static string GetConversationIdentity(ConversationRecord conversation)
    {
        if (!conversation.IsAgentConversation)
        {
            return "root:" + conversation.Id.ToString("D");
        }

        if (conversation.BoundAgentId is Guid boundAgentId)
        {
            return $"agent:{conversation.EffectiveRootConversationId:D}:{boundAgentId:D}";
        }

        return $"agent:{conversation.EffectiveRootConversationId:D}:{NormalizeIdentityPart(conversation.BoundAgentName)}:{NormalizeIdentityPart(conversation.BoundAgentRole)}";
    }

    private static string NormalizeIdentityPart(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
}
