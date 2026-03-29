using FluentAssertions;
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
        var profile = new ProviderProfile(Guid.NewGuid(), "Local", "https://api.example.com/v1", "gpt-4.1", ApiStyle.OpenAICompatible, "secret:test", now, now);
        await profileRepository.UpsertProfileAsync(profile);

        var workspace = new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\Demo\\SelfClaw", now, now);
        await conversationRepository.UpsertWorkspaceRootAsync(workspace);

        var conversation = new ConversationRecord(Guid.NewGuid(), "Chat", profile.Id, workspace.Id, now, now);
        await conversationRepository.UpsertConversationAsync(conversation);

        var userMessage = new MessageRecord(Guid.NewGuid(), conversation.Id, MessageRole.User, "Hello", MessageStatus.Completed, now, now);
        var assistantMessage = new MessageRecord(Guid.NewGuid(), conversation.Id, MessageRole.Assistant, "Hi there", MessageStatus.Completed, now, now, OutputTokens: 32);
        await conversationRepository.UpsertMessageAsync(userMessage);
        await conversationRepository.UpsertMessageAsync(assistantMessage);

        var toolRun = new ToolExecutionRecord(Guid.NewGuid(), conversation.Id, "read_workspace_file", "{}", ToolExecutionStatus.Completed, "Read Program.cs", "call-1", 4.2d, now, now);
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