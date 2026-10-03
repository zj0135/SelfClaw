using FluentAssertions;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
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
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversationRepository = new SqliteConversationRepository(database);
        var workspaceRepository = new SqliteWorkspaceRepository(database);

        await conversationRepository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\Demo\\SelfClaw", now, now);
        await workspaceRepository.UpsertWorkspaceRootAsync(workspace);

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

        var turns = new SqliteConversationTurnRepository(database);
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now);
        var started = await turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, "Hello", Guid.NewGuid()));
        var userMessage = started.Messages.Single();
        var assistantMessageId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var assistantMessage = new MessageRecord(assistantMessageId, conversation.Id, turn.Id,
            await turns.ReserveMessageSequenceAsync(conversation.Id), MessageRole.Assistant,
            "Hi there", MessageStatus.Sealed, now, now, Segments:
            [new(assistantMessageId, 0, MessageSegmentKind.Thinking, "plan", null),
             new(assistantMessageId, 1, MessageSegmentKind.Text, "Hi there", null),
             new(assistantMessageId, 2, MessageSegmentKind.ToolCall, null, toolId)]);
        var toolRun = new ToolExecutionRecord(toolId, conversation.Id, "read_workspace_file", "{}",
            ToolExecutionStatus.Completed, "Read Program.cs", "call-1", 4.2d, now, now,
            MessageId: assistantMessageId, ResultContent: "using System;", SourceKind: ToolSourceKind.Mcp,
            SourceId: "filesystem", DisplayName: "read_file");
        await turns.TryFinalizeTurnAsync(new ConversationTurnCommit(turn with
        {
            Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = now, Usage = new TurnUsage(OutputTokens: 32)
        }, [assistantMessage], [toolRun]));

        var loadedConversations = await conversationRepository.ListConversationsAsync();
        var loadedMessages = await conversationRepository.ListMessagesAsync(conversation.Id);
        var loadedToolRuns = await conversationRepository.ListToolExecutionsAsync(conversation.Id);
        var loadedRoots = await workspaceRepository.ListWorkspaceRootsAsync();

        loadedConversations.Should().ContainSingle().Which.Should().Be(conversation);
        loadedMessages.Should().HaveCount(2);
        // record equality compares the Segments arrays by reference, so compare fields explicitly.
        var loadedAssistant = loadedMessages.Should().Contain(message => message.Id == assistantMessageId)
            .Which;
        loadedAssistant.MarkdownContent.Should().Be(assistantMessage.MarkdownContent);
        (await turns.ListTurnsAsync(conversation.Id)).Single().Usage?.OutputTokens.Should().Be(32);
        loadedAssistant.Segments.Should().BeEquivalentTo(assistantMessage.Segments);
        loadedMessages.Should().Contain(message => message.Id == userMessage.Id)
            .Which.Segments.Should().BeNull();
        loadedToolRuns.Should().ContainSingle().Which.Should().Be(toolRun);
        loadedRoots.Should().ContainSingle().Which.Should().Be(workspace);
    }

    [Fact]
    public async Task Initialize_adds_ai_provider_schema()
    {
        var storagePaths = StoragePathDefaults.Create(
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
        maxSchemaVersion.Should().Be(30L);
    }

    [Fact]
    public async Task AiProviderRepository_round_trips_provider_connections_model_profiles_and_selections()
    {
        var storagePaths = StoragePathDefaults.Create(
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
        var storagePaths = StoragePathDefaults.Create(
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
        var storagePaths = StoragePathDefaults.Create(
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

    [Fact]
    public async Task Initialize_adds_v28_hook_columns_and_extension_source_path()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);

        await database.EnsureInitializedAsync();

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        var toolRunColumns = await ReadTableColumnNamesAsync(verification, "tool_runs");
        toolRunColumns.Should().Contain([
            "effective_arguments_json",
            "hook_feedback_json",
            "hook_outcome_json"]);
        (await ReadTableColumnNamesAsync(verification, "extension_packages")).Should().Contain("source_path");
    }

    [Fact]
    public async Task Repositories_round_trip_notice_segments()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var repository = new SqliteConversationRepository(database);
        var turns = new SqliteConversationTurnRepository(database);
        await repository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Chat",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await repository.UpsertConversationAsync(conversation);
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now);
        await turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, "prompt", Guid.NewGuid()));
        var messageId = Guid.NewGuid();
        var message = new MessageRecord(
            messageId,
            conversation.Id,
            turn.Id,
            await turns.ReserveMessageSequenceAsync(conversation.Id),
            MessageRole.Assistant,
            "answer",
            MessageStatus.Sealed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Thinking, "plan", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Notice, "Hook added context.", null),
                new MessageSegmentRecord(messageId, 2, MessageSegmentKind.Text, "answer", null)
            ]);
        await turns.TryFinalizeTurnAsync(new ConversationTurnCommit(turn with
        {
            Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = now
        }, [message], []));

        var loaded = (await repository.ListMessagesAsync(conversation.Id)).Single(item => item.Role == MessageRole.Assistant);
        loaded.Segments.Should().BeEquivalentTo(message.Segments);
        var notice = loaded.Segments!.Single(segment => segment.Kind == MessageSegmentKind.Notice);
        notice.Ordinal.Should().Be(1);
        notice.Text.Should().Be("Hook added context.");
        notice.ToolRunId.Should().BeNull();
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


