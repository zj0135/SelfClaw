using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteRepositoriesTests : IDisposable
{
    private readonly string _rootPath;

    public SqliteRepositoriesTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Repositories_round_trip_profiles_conversations_messages_and_tools()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var profileRepository = new SqliteProfileRepository(database);
        var conversationRepository = new SqliteConversationRepository(database);

        await profileRepository.InitializeAsync();
        await conversationRepository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var profile = new ProviderProfile(Guid.NewGuid(), "Local", "https://api.example.com/v1", "gpt-4.1", false, 0.7, false, 0.7, ApiStyle.OpenAICompatible, "secret:test", now, now);
        await profileRepository.UpsertProfileAsync(profile);

        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\Demo\\SelfClaw", now, now);
        await conversationRepository.UpsertWorkspaceRootAsync(workspace);

        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Chat",
            profile.Id,
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
            "using System;");
        await conversationRepository.UpsertToolExecutionAsync(toolRun);

        var loadedProfiles = await profileRepository.ListProfilesAsync();
        var loadedConversations = await conversationRepository.ListConversationsAsync();
        var loadedMessages = await conversationRepository.ListMessagesAsync(conversation.Id);
        var loadedToolRuns = await conversationRepository.ListToolExecutionsAsync(conversation.Id);
        var loadedRoots = await conversationRepository.ListWorkspaceRootsAsync();

        loadedProfiles.Should().ContainSingle().Which.Should().Be(profile);
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
        indexes.Should().Contain("ix_ai_provider_connections_kind");
        indexes.Should().Contain("ix_ai_model_profiles_connection");
        indexes.Should().Contain("ix_ai_model_profiles_updated");

        await using var versionCommand = verification.CreateCommand();
        versionCommand.CommandText = "SELECT MAX(version) FROM schema_versions;";
        var maxSchemaVersion = await versionCommand.ExecuteScalarAsync();
        maxSchemaVersion.Should().Be(18L);
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
            now);
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
    public async Task AiProviderRepository_disabling_provider_keeps_connection_and_models()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteAiProviderRepository(new SqliteDatabase(storagePaths));

        var now = DateTimeOffset.UtcNow;
        var providerConnection = new AiProviderConnection(
            Guid.NewGuid(),
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

        await repository.SetProviderConnectionEnabledAsync(providerConnection.Id, false);

        var enabledConnections = await repository.ListProviderConnectionsAsync();
        var allConnections = await repository.ListAllProviderConnectionsAsync();
        var loadedModelProfile = await repository.GetModelProfileAsync(modelProfile.Id);

        enabledConnections.Should().BeEmpty();
        allConnections.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        loadedModelProfile.Should().NotBeNull();

        await repository.SetProviderConnectionEnabledAsync(providerConnection.Id, true);

        enabledConnections = await repository.ListProviderConnectionsAsync();
        enabledConnections.Should().ContainSingle().Which.Id.Should().Be(providerConnection.Id);
    }

    [Fact]
    public async Task Initialize_adds_required_columns_to_legacy_conversations_table()
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
CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    workspace_root_id TEXT NULL,
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
        pragma.CommandText = "PRAGMA table_info(conversations);";
        await using var reader = await pragma.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        columns.Should().Contain("tool_permission_mode");
        columns.Should().Contain("mode");
        columns.Should().Contain("agent_id");
        columns.Should().Contain("channel_kind");
        columns.Should().Contain("channel_conversation_id");
        columns.Should().Contain("channel_display_name");
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

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
}


