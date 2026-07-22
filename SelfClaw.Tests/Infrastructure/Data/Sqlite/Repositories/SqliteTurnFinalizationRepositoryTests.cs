using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteTurnFinalizationRepositoryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryFinalizeTurnAsync_writes_message_and_tools_atomically_and_only_once(
        bool persistStreamingAssistant)
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var database = new SqliteDatabase(storagePaths);
        var conversationRepository = new SqliteConversationRepository(database);
        var finalizationRepository = conversationRepository;
        await conversationRepository.InitializeAsync();

        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Turn",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            AgentId: "build",
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
        await conversationRepository.UpsertConversationAsync(conversation);

        var assistant = new MessageRecord(
            Guid.NewGuid(),
            conversation.Id,
            MessageRole.Assistant,
            "partial",
            MessageStatus.Streaming,
            now,
            now);
        if (persistStreamingAssistant)
        {
            await conversationRepository.UpsertMessageAsync(assistant);
        }

        var runningTool = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversation.Id,
            "read_file",
            "{}",
            ToolExecutionStatus.Running,
            ResultSummary: null,
            CorrelationId: "call-1",
            DurationMs: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            MessageId: assistant.Id);
        await conversationRepository.UpsertToolExecutionAsync(runningTool);

        var first = new TurnFinalization(
            assistant with { Status = MessageStatus.Cancelled, ErrorMessage = "Generation stopped." },
            [runningTool with { Status = ToolExecutionStatus.Cancelled }]);
        var second = new TurnFinalization(
            assistant with { Status = MessageStatus.Failed, ErrorMessage = "late failure" },
            [runningTool with { Status = ToolExecutionStatus.Failed }]);

        var firstWritten = await finalizationRepository.TryFinalizeTurnAsync(first);
        var secondWritten = await finalizationRepository.TryFinalizeTurnAsync(second);

        firstWritten.Should().BeTrue();
        secondWritten.Should().BeFalse();
        (await conversationRepository.ListMessagesAsync(conversation.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(MessageStatus.Cancelled);
        (await conversationRepository.ListToolExecutionsAsync(conversation.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
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
