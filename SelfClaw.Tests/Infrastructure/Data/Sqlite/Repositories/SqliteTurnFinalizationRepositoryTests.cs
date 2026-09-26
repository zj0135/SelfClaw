using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
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
        var storagePaths = StoragePathDefaults.Create(
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

    [Fact]
    public async Task Tool_hook_outcome_round_trips_through_the_hook_columns()
    {
        var (repository, conversationId) = await CreateConversationAsync();
        var modifiedBy = new HookSource("shell-guard", "rewrite");
        var approvalBy = new HookSource("shell-guard", "ask");
        var blockedBy = new HookSource("shell-guard", "deny");
        var outcome = new ToolHookOutcome(
            "{\"command\":\"ls\"}",
            [modifiedBy],
            [approvalBy],
            blockedBy,
            "Denied by policy.",
            [new HookFeedback(modifiedBy, "Rewritten.")],
            [new HookFailureNotice(new HookSource("lint", "check"), "timedOut", "Took too long.")]);

        var record = CreateToolRecord(conversationId) with
        {
            Status = ToolExecutionStatus.Failed,
            HookOutcome = outcome
        };
        await repository.UpsertToolExecutionAsync(record);

        var loaded = (await repository.ListToolExecutionsAsync(conversationId)).Should().ContainSingle().Subject;
        loaded.HookOutcome.Should().BeEquivalentTo(outcome);
        loaded.HookOutcome!.EffectiveArgumentsJson.Should().Be("{\"command\":\"ls\"}");
        loaded.HookOutcome!.ArgumentsModifiedBy.Should().Equal(modifiedBy);
        loaded.HookOutcome!.ApprovalRequiredBy.Should().Equal(approvalBy);
        loaded.HookOutcome!.BlockedBy.Should().Be(blockedBy);
        loaded.HookOutcome!.BlockReason.Should().Be("Denied by policy.");
        loaded.HookOutcome!.Feedback.Should().Equal(new HookFeedback(modifiedBy, "Rewritten."));
        loaded.HookOutcome!.IgnoredFailures.Should().Equal(
            new HookFailureNotice(new HookSource("lint", "check"), "timedOut", "Took too long."));
    }

    [Fact]
    public async Task Tool_run_without_hook_outcome_reads_back_null()
    {
        var (repository, conversationId) = await CreateConversationAsync();

        await repository.UpsertToolExecutionAsync(CreateToolRecord(conversationId));

        var loaded = (await repository.ListToolExecutionsAsync(conversationId)).Should().ContainSingle().Subject;
        loaded.HookOutcome.Should().BeNull();
    }

    [Fact]
    public async Task Tool_run_with_only_feedback_reads_back_the_feedback()
    {
        var (repository, conversationId) = await CreateConversationAsync();
        var source = new HookSource("shell-guard", "lint");
        var record = CreateToolRecord(conversationId) with
        {
            HookOutcome = new ToolHookOutcome(null, [], [], null, null, [new HookFeedback(source, "Quote it.")], [])
        };

        await repository.UpsertToolExecutionAsync(record);

        var loaded = (await repository.ListToolExecutionsAsync(conversationId)).Should().ContainSingle().Subject;
        loaded.HookOutcome.Should().BeEquivalentTo(record.HookOutcome);
        loaded.HookOutcome!.EffectiveArgumentsJson.Should().BeNull();
        loaded.HookOutcome!.Feedback.Should().Equal(new HookFeedback(source, "Quote it."));
    }

    // The runtime writes a Running row without hooks and a terminal row with them; the terminal write
    // must merge rather than overwrite the start record's absent hook columns.
    [Fact]
    public async Task Completing_a_tool_run_keeps_the_hook_outcome_written_at_completion()
    {
        var (repository, conversationId) = await CreateConversationAsync();
        var blockedBy = new HookSource("shell-guard", "deny");
        var running = CreateToolRecord(conversationId) with { Status = ToolExecutionStatus.Running };
        await repository.UpsertToolExecutionAsync(running);

        var completed = running with
        {
            Status = ToolExecutionStatus.Failed,
            HookOutcome = new ToolHookOutcome(null, [], [], blockedBy, "Blocked.", [], [])
        };
        await repository.UpsertToolExecutionAsync(completed);

        var loaded = (await repository.ListToolExecutionsAsync(conversationId)).Should().ContainSingle().Subject;
        loaded.HookOutcome.Should().BeEquivalentTo(completed.HookOutcome);
        loaded.HookOutcome!.BlockedBy.Should().Be(blockedBy);
        loaded.HookOutcome!.BlockReason.Should().Be("Blocked.");
    }

    private async Task<(SqliteConversationRepository Repository, Guid ConversationId)> CreateConversationAsync()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteConversationRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Turn",
            null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await repository.UpsertConversationAsync(conversation);
        return (repository, conversation.Id);
    }

    private static ToolExecutionRecord CreateToolRecord(Guid conversationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "run_shell_command",
            "{\"command\":\"rm -rf /\"}",
            ToolExecutionStatus.Running,
            null,
            "call-1",
            null,
            now,
            now);
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
