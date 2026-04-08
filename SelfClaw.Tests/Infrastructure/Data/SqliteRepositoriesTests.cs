using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Infrastructure.Repositories;

namespace SelfClaw.Tests.Repositories;

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
        var profile = new ProviderProfile(Guid.NewGuid(), "Local", "https://api.example.com/v1", "gpt-4.1", 0.7, 0.7, ApiStyle.OpenAICompatible, "secret:test", now, now);
        await profileRepository.UpsertProfileAsync(profile);

        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\Demo\\SelfClaw", now, now);
        await conversationRepository.UpsertWorkspaceRootAsync(workspace);

        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Chat",
            profile.Id,
            workspace.Id,
            ConversationMode.Team,
            ToolPermissionMode.RequireApproval,
            4,
            TeamOutputMode.AlwaysDocument,
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
            1);
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
    public async Task Initialize_adds_team_discussion_columns_to_legacy_conversations_table()
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
        columns.Should().Contain("team_max_rounds");
        columns.Should().Contain("team_output_mode");
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
}
