using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteRepositoriesTests : IDisposable
{
    private readonly string _rootPath;

    public SqliteRepositoriesTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Repositories_round_trip_conversations_messages_tools_and_workspace_roots()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversationRepository = new SqliteConversationRepository(database);

        await conversationRepository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\Demo\\SelfClaw", now, now);
        await conversationRepository.UpsertWorkspaceRootAsync(workspace);

        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Chat",
            workspace.Id,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await conversationRepository.UpsertConversationAsync(conversation);

        var userMessage = new MessageRecord(Guid.NewGuid(), conversation.Id, MessageRole.User, "Hello", MessageStatus.Completed, now, now);
        var assistantMessage = new MessageRecord(Guid.NewGuid(), conversation.Id, MessageRole.Assistant, "Hi there", MessageStatus.Completed, now, now, OutputTokens: 32);
        await conversationRepository.UpsertMessageAsync(userMessage);
        await conversationRepository.UpsertMessageAsync(assistantMessage);

        var toolRun = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversation.Id,
            "read_workspace_file",
            "{}",
            ToolExecutionStatus.Completed,
            "Read Program.cs",
            "call-1",
            4.2d,
            now,
            now,
            assistantMessage.Id,
            1,
            "using System;",
            ToolSourceKind.Mcp,
            "filesystem",
            "read_file");
        await conversationRepository.UpsertToolExecutionAsync(toolRun);

        var loadedConversations = await conversationRepository.ListConversationsAsync();
        var loadedMessages = await conversationRepository.ListMessagesAsync(conversation.Id);
        var loadedToolRuns = await conversationRepository.ListToolExecutionsAsync(conversation.Id);
        var loadedRoots = await conversationRepository.ListWorkspaceRootsAsync();

        loadedConversations.Should().ContainSingle().Which.Should().Be(conversation);
        loadedMessages.Should().HaveCount(2);
        loadedMessages.Should().Contain(assistantMessage);
        loadedToolRuns.Should().ContainSingle().Which.Should().Be(toolRun);
        loadedRoots.Should().ContainSingle().Which.Should().Be(workspace);
    }

    [Fact]
    public async Task Initialize_adds_ai_provider_schema()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);

        await database.EnsureInitializedAsync();

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();

        var tables = await ReadSqliteObjectNamesAsync(verification, "table");
        var indexes = await ReadSqliteObjectNamesAsync(verification, "index");

        tables.Should().Contain("ai_provider_connections");
        tables.Should().Contain("ai_model_profiles");
        tables.Should().Contain("ai_model_profile_selections");
        tables.Should().Contain("cli_agent_sessions");
        tables.Should().Contain("extension_packages");
        tables.Should().Contain("mcp_server_configs");
        tables.Should().NotContain("profiles");
        indexes.Should().Contain("ix_ai_provider_connections_kind");
        indexes.Should().Contain("ix_ai_model_profiles_connection");
        indexes.Should().Contain("ix_ai_model_profiles_updated");

        var conversationColumns = await ReadTableColumnNamesAsync(verification, "conversations");
        conversationColumns.Should().NotContain("profile_id");

        await using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        var maxSchemaVersion = await versionCommand.ExecuteScalarAsync();
        maxSchemaVersion.Should().Be(24L);
    }

    [Fact]
    public async Task AiProviderRepository_round_trips_provider_connections_model_profiles_and_selections()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var repository = new SqliteAiProviderRepository(database);

        var now = DateTimeOffset.UtcNow;
        var providerConnection = new AiProviderConnection(
            Guid.NewGuid(),
            "openai",
            "OpenAI",
            AiProviderKind.OpenAI,
            new Uri("https://api.openai.com/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                ["api_key"] = "secret:openai"
            },
            ReadJsonObject("{\"organization\":\"org-test\",\"timeout_seconds\":30}"),
            now,
            now);
        await repository.UpsertProviderConnectionAsync(providerConnection);

        var modelProfile = new AiModelProfile(
            Guid.NewGuid(),
            providerConnection.Id,
            "GPT-4.1",
            AiProviderApiFormat.OpenAIResponses,
            "gpt-4.1",
            new AiSamplingOptions(true, 0.2, true, 0.9),
            ReadJsonObject("{\"reasoning.effort\":\"medium\",\"store\":true}"),
            now,
            now,
            IsEnabled: false);
        await repository.UpsertModelProfileAsync(modelProfile);

        var selection = new AiModelProfileSelection("desktop.default", modelProfile.Id, now);
        await repository.SetModelProfileSelectionAsync(selection);

        var loadedProviderConnection = await repository.GetProviderConnectionAsync(providerConnection.Id);
        var loadedProviderConnections = await repository.ListProviderConnectionsAsync();
        var loadedModelProfile = await repository.GetModelProfileAsync(modelProfile.Id);
        var loadedModelProfiles = await repository.ListModelProfilesAsync(providerConnection.Id);
        var loadedSelection = await repository.GetModelProfileSelectionAsync(selection.Scope);

        loadedProviderConnections.Should().ContainSingle();
        loadedProviderConnection.Should().NotBeNull();
        loadedProviderConnection!.Id.Should().Be(providerConnection.Id);
        loadedProviderConnection.CatalogId.Should().Be("openai");
        loadedProviderConnection.Name.Should().Be("OpenAI");
        loadedProviderConnection.ProviderKind.Should().Be(AiProviderKind.OpenAI);
        loadedProviderConnection.Endpoint.AbsoluteUri.Should().Be("https://api.openai.com/v1/");
        loadedProviderConnection.AuthKind.Should().Be(AiProviderAuthKind.ApiKey);
        loadedProviderConnection.CredentialRefs.Should().ContainKey("api_key").WhoseValue.Should().Be("secret:openai");
        loadedProviderConnection.ConnectionOptions["organization"].GetString().Should().Be("org-test");
        loadedProviderConnection.ConnectionOptions["timeout_seconds"].GetInt32().Should().Be(30);

        loadedModelProfiles.Should().ContainSingle();
        loadedModelProfile.Should().NotBeNull();
        loadedModelProfile!.Id.Should().Be(modelProfile.Id);
        loadedModelProfile.ProviderConnectionId.Should().Be(providerConnection.Id);
        loadedModelProfile.ApiFormat.Should().Be(AiProviderApiFormat.OpenAIResponses);
        loadedModelProfile.Model.Should().Be("gpt-4.1");
        loadedModelProfile.Sampling.Should().Be(modelProfile.Sampling);
        loadedModelProfile.ModelOptions["reasoning.effort"].GetString().Should().Be("medium");
        loadedModelProfile.ModelOptions["store"].GetBoolean().Should().BeTrue();
        loadedModelProfile.IsEnabled.Should().BeFalse();

        loadedSelection.Should().Be(selection);
    }

    [Fact]
    public async Task AiProviderRepository_delete_provider_connection_cascades_model_profiles()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteAiProviderRepository(new SqliteDatabase(storagePaths));

        var now = DateTimeOffset.UtcNow;
        var providerConnection = new AiProviderConnection(
            Guid.NewGuid(),
            "custom",
            "Local",
            AiProviderKind.OpenAICompatible,
            new Uri("http://localhost:11434/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                ["api_key"] = "secret:local"
            },
            ReadJsonObject("{}"),
            now,
            now);
        var modelProfile = new AiModelProfile(
            Guid.NewGuid(),
            providerConnection.Id,
            "Local model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "local-model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            ReadJsonObject("{}"),
            now,
            now);

        await repository.UpsertProviderConnectionAsync(providerConnection);
        await repository.UpsertModelProfileAsync(modelProfile);
        await repository.SetModelProfileSelectionAsync(new AiModelProfileSelection("desktop.default", modelProfile.Id, now));

        await repository.DeleteProviderConnectionAsync(providerConnection.Id);

        var loadedProviderConnection = await repository.GetProviderConnectionAsync(providerConnection.Id);
        var loadedModelProfile = await repository.GetModelProfileAsync(modelProfile.Id);
        var loadedSelection = await repository.GetModelProfileSelectionAsync("desktop.default");

        loadedProviderConnection.Should().BeNull();
        loadedModelProfile.Should().BeNull();
        loadedSelection.Should().BeNull();
    }

    [Fact]
    public async Task AiProviderRepository_model_enablement_requires_enabled_model_and_provider()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteAiProviderRepository(new SqliteDatabase(storagePaths));

        var now = DateTimeOffset.UtcNow;
        var providerConnection = new AiProviderConnection(
            Guid.NewGuid(),
            "custom",
            "Local",
            AiProviderKind.OpenAICompatible,
            new Uri("http://localhost:11434/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                ["api_key"] = "secret:local"
            },
            ReadJsonObject("{}"),
            now,
            now);
        var modelProfile = new AiModelProfile(
            Guid.NewGuid(),
            providerConnection.Id,
            "Local model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "local-model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            ReadJsonObject("{}"),
            now,
            now);

        await repository.UpsertProviderConnectionAsync(providerConnection);
        await repository.UpsertModelProfileAsync(modelProfile);

        var enabledModels = await repository.ListEnabledModelProfilesAsync();
        enabledModels.Should().ContainSingle().Which.Id.Should().Be(modelProfile.Id);

        await repository.SetModelProfileEnabledAsync(modelProfile.Id, false);

        var allModels = await repository.ListModelProfilesAsync(providerConnection.Id);
        allModels.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        (await repository.GetModelProfileAsync(modelProfile.Id)).Should().NotBeNull();
        (await repository.ListEnabledModelProfilesAsync()).Should().BeEmpty();

        await repository.SetAllModelProfilesEnabledAsync(providerConnection.Id, true);
        (await repository.ListEnabledModelProfilesAsync()).Should().ContainSingle();

        await repository.SetProviderConnectionEnabledAsync(providerConnection.Id, false);

        var enabledConnections = await repository.ListProviderConnectionsAsync();
        var allConnections = await repository.ListAllProviderConnectionsAsync();
        var loadedModelProfile = await repository.GetModelProfileAsync(modelProfile.Id);

        enabledConnections.Should().BeEmpty();
        allConnections.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        loadedModelProfile.Should().NotBeNull();
        (await repository.GetProviderConnectionAsync(providerConnection.Id)).Should().NotBeNull();
        (await repository.ListEnabledModelProfilesAsync()).Should().BeEmpty();

        await repository.SetProviderConnectionEnabledAsync(providerConnection.Id, true);

        enabledConnections = await repository.ListProviderConnectionsAsync();
        enabledConnections.Should().ContainSingle().Which.Id.Should().Be(providerConnection.Id);
        (await repository.ListEnabledModelProfilesAsync()).Should().ContainSingle();

        await repository.SetAllModelProfilesEnabledAsync(providerConnection.Id, false);
        (await repository.ListEnabledModelProfilesAsync()).Should().BeEmpty();
        (await repository.ListModelProfilesAsync(providerConnection.Id))
            .Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_adds_catalog_id_to_legacy_ai_provider_connections()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        Directory.CreateDirectory(_rootPath);

        var providerId = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE ai_provider_connections (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    provider_kind INTEGER NOT NULL,
    endpoint TEXT NOT NULL,
    auth_kind INTEGER NOT NULL,
    credential_refs_json TEXT NOT NULL DEFAULT '{}',
    connection_options_json TEXT NOT NULL DEFAULT '{}',
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
INSERT INTO ai_provider_connections(
    id, name, provider_kind, endpoint, auth_kind, credential_refs_json,
    connection_options_json, is_enabled, created_at_utc, updated_at_utc)
VALUES(
    $id, 'Legacy gateway', 1, 'https://legacy.example/v1/', 0, '{}', '{}', 1,
    '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$id", providerId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAiProviderRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();

        var migrated = await repository.GetProviderConnectionAsync(providerId);
        migrated.Should().NotBeNull();
        migrated!.CatalogId.Should().Be("custom");

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        await using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        (await versionCommand.ExecuteScalarAsync()).Should().Be(24L);
    }

    [Fact]
    public async Task Initialize_v21_removes_legacy_profiles_without_losing_conversation_dependencies()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        Directory.CreateDirectory(_rootPath);

        var profileId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE schema_versions (
    version INTEGER NOT NULL PRIMARY KEY,
    applied_at_utc TEXT NOT NULL
);
INSERT INTO schema_versions(version, applied_at_utc)
VALUES(20, '2026-01-01T00:00:00.0000000+00:00');

CREATE TABLE profiles (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    model TEXT NOT NULL,
    temperature_enabled INTEGER NOT NULL DEFAULT 0,
    temperature REAL NOT NULL DEFAULT 0.7,
    top_p_enabled INTEGER NOT NULL DEFAULT 0,
    top_p REAL NOT NULL DEFAULT 0.7,
    api_style INTEGER NOT NULL,
    secret_ref TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE workspace_roots (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    root_path TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build',
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(profile_id) REFERENCES profiles(id) ON DELETE RESTRICT,
    FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL
);

CREATE TABLE messages (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    role INTEGER NOT NULL,
    markdown_content TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    agent_id TEXT NULL,
    agent_name TEXT NULL,
    agent_role TEXT NULL,
    input_tokens INTEGER NULL,
    output_tokens INTEGER NULL,
    duration_ms REAL NULL,
    error_message TEXT NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);

CREATE TABLE tool_runs (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    status INTEGER NOT NULL,
    result_summary TEXT NULL,
    result_content TEXT NULL,
    correlation_id TEXT NULL,
    duration_ms REAL NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    agent_id TEXT NULL,
    message_id TEXT NULL,
    after_segment_index INTEGER NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);

CREATE TABLE cli_agent_sessions (
    conversation_id TEXT NOT NULL,
    agent_kind INTEGER NOT NULL,
    session_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(conversation_id, agent_kind),
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);

INSERT INTO profiles(
    id, name, endpoint, model, temperature_enabled, temperature,
    top_p_enabled, top_p, api_style, secret_ref, created_at_utc, updated_at_utc)
VALUES(
    $profileId, 'Legacy profile', 'https://legacy.example/v1', 'legacy-model', 0, 0.7,
    0, 0.7, 0, 'secret:legacy', $createdAt, $updatedAt);
INSERT INTO workspace_roots(id, name, root_path, created_at_utc, updated_at_utc)
VALUES($workspaceId, 'Legacy repo', 'E:\\Legacy\\Repo', $createdAt, $updatedAt);
INSERT INTO conversations(
    id, title, profile_id, workspace_root_id, mode, tool_permission_mode, agent_id,
    channel_kind, channel_conversation_id, channel_display_name, created_at_utc, updated_at_utc)
VALUES(
    $conversationId, 'Legacy chat', $profileId, $workspaceId, 0, $toolPermissionMode, 'legacy-agent',
    'feishu', 'channel-42', 'Legacy channel', $createdAt, $updatedAt);
INSERT INTO messages(
    id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc)
VALUES($messageId, $conversationId, 1, 'Preserved message', 1, $createdAt, $updatedAt);
INSERT INTO tool_runs(
    id, conversation_id, tool_name, arguments_json, status, result_summary, result_content,
    correlation_id, duration_ms, created_at_utc, updated_at_utc, message_id, after_segment_index)
VALUES(
    $toolRunId, $conversationId, 'read_workspace_file', '{}', 2, 'Preserved tool', 'contents',
    'call-42', 12.5, $createdAt, $updatedAt, $messageId, 3);
INSERT INTO cli_agent_sessions(
    conversation_id, agent_kind, session_id, created_at_utc, updated_at_utc)
VALUES($conversationId, 1, 'session-42', $createdAt, $updatedAt);";
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$workspaceId", workspaceId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
            command.Parameters.AddWithValue("$toolRunId", toolRunId.ToString("D"));
            command.Parameters.AddWithValue("$toolPermissionMode", (int)ToolPermissionMode.RequireApproval);
            command.Parameters.AddWithValue("$createdAt", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$updatedAt", "2026-01-02T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(storagePaths);
        var repository = new SqliteConversationRepository(database);
        await repository.InitializeAsync();

        var migratedConversation = await repository.GetConversationAsync(conversationId);
        var migratedMessages = await repository.ListMessagesAsync(conversationId);
        var migratedToolRuns = await repository.ListToolExecutionsAsync(conversationId);

        migratedConversation.Should().NotBeNull();
        migratedConversation!.Title.Should().Be("Legacy chat");
        migratedConversation.WorkspaceRootId.Should().Be(workspaceId);
        migratedConversation.ToolPermissionMode.Should().Be(ToolPermissionMode.RequireApproval);
        migratedConversation.AgentId.Should().Be("legacy-agent");
        migratedConversation.ChannelKind.Should().Be("feishu");
        migratedConversation.ChannelConversationId.Should().Be("channel-42");
        migratedConversation.ChannelDisplayName.Should().Be("Legacy channel");
        migratedMessages.Should().ContainSingle().Which.MarkdownContent.Should().Be("Preserved message");
        migratedToolRuns.Should().ContainSingle().Which.ResultContent.Should().Be("contents");

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();

        var tables = await ReadSqliteObjectNamesAsync(verification, "table");
        tables.Should().NotContain("profiles");
        (await ReadTableColumnNamesAsync(verification, "conversations")).Should().NotContain("profile_id");

        await using var sessionCommand = verification.CreateCommand();
        sessionCommand.CommandText = @"
SELECT session_id
FROM cli_agent_sessions
WHERE conversation_id = $conversationId AND agent_kind = 1;";
        sessionCommand.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        (await sessionCommand.ExecuteScalarAsync()).Should().Be("session-42");

        await using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        (await versionCommand.ExecuteScalarAsync()).Should().Be(24L);

        await using var foreignKeyCheck = verification.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
        await using var foreignKeyReader = await foreignKeyCheck.ExecuteReaderAsync();
        (await foreignKeyReader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_v21_recovers_from_a_leftover_conversations_new_table()
    {
        // Simulates a crash during a prior v21 rebuild: conversations still carries profile_id and a
        // stray conversations_new table remains. Initialization must drop the stale table, rebuild
        // cleanly, and keep the conversation row.
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        Directory.CreateDirectory(_rootPath);

        var profileId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE schema_versions (version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL);
INSERT INTO schema_versions(version, applied_at_utc) VALUES(20, '2026-01-01T00:00:00.0000000+00:00');

CREATE TABLE profiles (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    model TEXT NOT NULL,
    temperature_enabled INTEGER NOT NULL DEFAULT 0,
    temperature REAL NOT NULL DEFAULT 0.7,
    top_p_enabled INTEGER NOT NULL DEFAULT 0,
    top_p REAL NOT NULL DEFAULT 0.7,
    api_style INTEGER NOT NULL,
    secret_ref TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build',
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

-- Stale artifact from a crashed migration; must not collide with the new rebuild.
CREATE TABLE conversations_new (id TEXT NOT NULL PRIMARY KEY, title TEXT NOT NULL);
INSERT INTO conversations_new(id, title) VALUES('stale', 'stale');

INSERT INTO profiles(id, name, endpoint, model, api_style, secret_ref, created_at_utc, updated_at_utc)
VALUES($profileId, 'Legacy profile', 'https://legacy.example/v1', 'legacy-model', 0, 'secret', $createdAt, $updatedAt);
INSERT INTO conversations(
    id, title, profile_id, mode, tool_permission_mode, agent_id, created_at_utc, updated_at_utc)
VALUES($conversationId, 'Recovered chat', $profileId, 0, 0, 'build', $createdAt, $updatedAt);";
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$createdAt", "2026-01-01T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$updatedAt", "2026-01-02T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(storagePaths);
        var repository = new SqliteConversationRepository(database);
        await repository.InitializeAsync();

        var migratedConversation = await repository.GetConversationAsync(conversationId);
        migratedConversation.Should().NotBeNull();
        migratedConversation!.Title.Should().Be("Recovered chat");

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        var tables = await ReadSqliteObjectNamesAsync(verification, "table");
        tables.Should().NotContain("profiles");
        tables.Should().NotContain("conversations_new");
        (await ReadTableColumnNamesAsync(verification, "conversations")).Should().NotContain("profile_id");
    }

    [Fact]
    public async Task Initialize_v22_adds_conversation_ownership_without_losing_data()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        Directory.CreateDirectory(_rootPath);
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE schema_versions (version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL);
INSERT INTO schema_versions(version, applied_at_utc) VALUES(22, '2026-01-01T00:00:00.0000000+00:00');

CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build',
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

CREATE TABLE messages (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    role INTEGER NOT NULL,
    markdown_content TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    agent_id TEXT NULL,
    agent_name TEXT NULL,
    agent_role TEXT NULL,
    input_tokens INTEGER NULL,
    output_tokens INTEGER NULL,
    duration_ms REAL NULL,
    error_message TEXT NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);

INSERT INTO conversations(
    id, title, mode, tool_permission_mode, agent_id, created_at_utc, updated_at_utc)
VALUES($conversationId, 'Version 22 chat', 0, 1, 'build', $createdAt, $createdAt);
INSERT INTO messages(
    id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc)
VALUES($messageId, $conversationId, 0, 'Preserved v22 message', 1, $createdAt, $createdAt);";
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
            command.Parameters.AddWithValue("$createdAt", "2026-01-01T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteConversationRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();

        var conversation = await repository.GetConversationAsync(conversationId);
        conversation.Should().NotBeNull();
        conversation!.Kind.Should().Be(ConversationKind.Interactive);
        conversation.ParentConversationId.Should().BeNull();
        (await repository.ListMessagesAsync(conversationId)).Should().ContainSingle(message =>
            message.Id == messageId && message.MarkdownContent == "Preserved v22 message");

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        (await ReadTableColumnNamesAsync(verification, "conversations"))
            .Should().Contain(["kind", "parent_conversation_id"]);
        await using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        (await versionCommand.ExecuteScalarAsync()).Should().Be(24L);
        await using var foreignKeyCheck = verification.CreateCommand();
        foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
        await using var foreignKeyReader = await foreignKeyCheck.ExecuteReaderAsync();
        (await foreignKeyReader.ReadAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_adds_tool_anchor_columns_to_legacy_tool_runs_table()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        Directory.CreateDirectory(_rootPath);

        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE tool_runs (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    status INTEGER NOT NULL,
    result_summary TEXT NULL,
    correlation_id TEXT NULL,
    duration_ms REAL NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);";
            await command.ExecuteNonQueryAsync();
        }

        var database = new SqliteDatabase(storagePaths);
        var repository = new SqliteConversationRepository(database);
        await repository.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        await using var pragma = verification.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(tool_runs);";
        await using var reader = await pragma.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        columns.Should().Contain("message_id");
        columns.Should().Contain("after_segment_index");
        columns.Should().Contain("result_content");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<List<string>> ReadSqliteObjectNamesAsync(SqliteConnection connection, string type)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<string>> ReadTableColumnNamesAsync(
        SqliteConnection connection,
        string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
}


