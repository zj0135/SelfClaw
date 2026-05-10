using Microsoft.Extensions.AI;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Runtime.Compaction;
using SelfClaw.Infrastructure.Agents.Runtime.Execution;
using SelfClaw.Infrastructure.Agents.Runtime.Orchestration;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

public sealed class ConversationContextCompactionServiceTests : IDisposable
{
    private readonly string _rootPath;

    public ConversationContextCompactionServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawCompactionTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task PrepareMessages_does_not_call_model_when_context_is_below_threshold()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("unused");
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = new[]
        {
            CreateMessage(conversation.Id, 0, MessageRole.User, "short user prompt"),
            CreateMessage(conversation.Id, 1, MessageRole.Assistant, "short answer")
        };

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 1_000,
            modelAutoCompactTokenLimit: 800);

        prepared.Should().Equal(messages);
        execution.RunCount.Should().Be(0);
    }

    [Fact]
    public async Task PrepareMessages_compacts_old_history_and_persists_summary_when_threshold_is_reached()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Summary v1");
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = CreateLongMessages(conversation.Id, 10);

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 120);

        prepared.Should().HaveCount(7);
        prepared[0].Role.Should().Be(MessageRole.System);
        prepared[0].MarkdownContent.Should().Contain("Summary v1");
        prepared.Skip(1).Should().Equal(messages.Skip(4));
        execution.RunCount.Should().Be(1);

        var persisted = await repository.GetConversationContextSummaryAsync(conversation.Id);
        persisted.Should().NotBeNull();
        persisted!.SummaryMarkdown.Should().Be("Summary v1");
        persisted.CoveredThroughMessageId.Should().Be(messages[3].Id);
    }

    [Fact]
    public async Task PrepareMessages_compacts_when_provider_reported_usage_reaches_threshold()
    {
        // This test verifies that the two-point delta calibration correctly amplifies
        // the local estimate when the real tokenizer uses more tokens than char/4.
        //
        // Setup: 10 short messages where local estimate is BELOW the threshold,
        // but two assistant messages have InputTokens showing the real tokenizer
        // uses ~2x more tokens. The calibration ratio should push the estimate
        // above the threshold, triggering compaction.
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Measured summary");
        var service = new ConversationContextCompactionService(repository, execution);

        // Each message: "msg-X" = 5 chars, about 2 tokens + 4 overhead = 6 tokens/message.
        // 10 messages means local estimate is about 60 tokens. Threshold = 100.
        // Without calibration, 60 < 100, so no compaction.
        var messages = Enumerable.Range(0, 10)
            .Select(index => CreateMessage(
                conversation.Id,
                index,
                index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                $"msg-{index}"))
            .ToArray();

        // Point A: assistant at index 1. InputTokens = 200 (includes overhead).
        // Messages before index 1: just msg-0, local estimate is about 6 tokens.
        messages[1] = messages[1] with { InputTokens = 200, OutputTokens = 10 };

        // Point B: assistant at index 9. InputTokens = 320 (includes overhead).
        // Messages before index 9: msg-0..msg-8, local estimate is about 54 tokens.
        // Delta: InputTokens_B - InputTokens_A = 320 - 200 = 120.
        // Local delta (messages[1..8]): 8 messages * 6 = 48 tokens.
        // Calibration ratio = 120 / 48 = 2.5.
        // Calibrated total = 60 * 2.5 = 150 > threshold 100, so it triggers compaction.
        messages[9] = messages[9] with { InputTokens = 320, OutputTokens = 20 };

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 100);

        prepared.Should().HaveCount(7);
        prepared[0].Role.Should().Be(MessageRole.System);
        prepared[0].MarkdownContent.Should().Contain("Measured summary");
        prepared.Skip(1).Should().Equal(messages.Skip(4));
        execution.RunCount.Should().Be(1);

        var persisted = await repository.GetConversationContextSummaryAsync(conversation.Id);
        persisted.Should().NotBeNull();
        persisted!.CoveredThroughMessageId.Should().Be(messages[3].Id);
    }

    [Fact]
    public void EstimateMeasuredEffectiveTokens_includes_output_tokens_adjustment()
    {
        // Verifies that EstimateMeasuredEffectiveTokens adds the delta between
        // real OutputTokens and the local estimate for the assistant message.
        var conversationId = Guid.NewGuid();
        var messages = Enumerable.Range(0, 10)
            .Select(index => CreateMessage(
                conversationId,
                index,
                index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                string.Format("msg-{0}", index)))
            .ToArray();

        // Single measurement: assistant at index 9.
        // Local estimate per message = 6 tokens. Total = 60.
        // InputTokens = 54 (messages before index 9 = 9 * 6 = 54, overhead = 0).
        // OutputTokens = 30 (much larger than local estimate of 6).
        // Expected: calibratedTotal = 60, outputDelta = 30 - 6 = 24, final = 84.
        messages[9] = messages[9] with { InputTokens = 54, OutputTokens = 30 };

        var measured = ConversationContextTokens.EstimateMeasuredEffectiveTokens(null, messages);

        // Without OutputTokens adjustment it would be 60; with it should be 84.
        measured.Should().BeGreaterThan(60, "OutputTokens adjustment should increase the estimate");
        measured.Should().Be(84);
    }

    [Fact]
    public async Task PrepareMessages_compacts_when_real_output_tokens_push_estimate_above_threshold()
    {
        // Verifies that real OutputTokens on the latest assistant message are used
        // to adjust the calibrated total upward, triggering compaction.
        // Need > MinimumRecentMessages (6) so SelectRecentTail leaves some for historyToSummarize.
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Output summary");
        var service = new ConversationContextCompactionService(repository, execution);

        // 10 short messages: local estimate = 6 tokens each = 60 tokens total.
        // Threshold = 80. Without OutputTokens adjustment: 60 < 80 = no compaction.
        var messages = Enumerable.Range(0, 10)
            .Select(index => CreateMessage(
                conversation.Id,
                index,
                index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                $"msg-{index}"))
            .ToArray();

        // Single measurement point: assistant at index 9.
        // InputTokens = 54 (messages before index 9: 9 * 6 = 54, overhead = 0).
        // OutputTokens = 30 (much larger than local estimate of 6).
        // calibratedTotal = 60 (ratio=1.0 with single point).
        // outputDelta = 30 - ceil(6 * 1.0) = 30 - 6 = 24.
        // final = 60 + 24 = 84 > threshold 80 = triggers compaction.
        messages[9] = messages[9] with { InputTokens = 54, OutputTokens = 30 };

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 80);

        execution.RunCount.Should().Be(1);
        prepared[0].Role.Should().Be(MessageRole.System);
        prepared[0].MarkdownContent.Should().Contain("Output summary");
    }

    [Fact]
    public async Task PrepareMessages_compacts_when_summary_plus_real_output_tokens_reaches_threshold()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Summary output update");
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = Enumerable.Range(0, 14)
            .Select(index => CreateMessage(
                conversation.Id,
                index,
                index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                $"msg-{index}"))
            .ToArray();

        var coveredMessage = messages[3];
        var summaryMarkdown = new string('s', 320);
        var summary = new ConversationContextSummaryRecord(
            conversation.Id,
            summaryMarkdown,
            coveredMessage.Id,
            coveredMessage.CreatedAtUtc,
            SourceTokenEstimate: 100,
            SummaryTokenEstimate: ConversationContextTokens.EstimateTextTokens(summaryMarkdown),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await repository.UpsertConversationContextSummaryAsync(summary);

        // Covered messages 0..3 are replaced by a summary worth 84 tokens.
        // Uncovered messages 4..13 are 10 short messages worth 60 local tokens.
        // Latest real output adds 24 tokens over its local estimate.
        // Local effective estimate is 144. Measured without summary would be 84.
        // Summary + measured output is 168, which crosses the 150 threshold.
        messages[13] = messages[13] with { InputTokens = 138, OutputTokens = 30 };

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 150);

        execution.RunCount.Should().Be(1);
        prepared[0].Role.Should().Be(MessageRole.System);
        prepared[0].MarkdownContent.Should().Contain("Summary output update");
    }

    [Fact]
    public async Task PrepareMessages_uses_existing_summary_without_re_summarizing_covered_history()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Summary v1", "Summary v2");
        var service = new ConversationContextCompactionService(repository, execution);
        var initialMessages = CreateLongMessages(conversation.Id, 10);
        await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            initialMessages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 120);
        var nextMessages = CreateLongMessages(conversation.Id, 14);

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            nextMessages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 120);

        prepared[0].MarkdownContent.Should().Contain("Summary v2");
        execution.RunCount.Should().Be(2);
        execution.Payloads.Last().Should().Contain("Summary v1");
        execution.Payloads.Last().Should().Contain("msg-4");
        execution.Payloads.Last().Should().NotContain("msg-0");

        var persisted = await repository.GetConversationContextSummaryAsync(conversation.Id);
        persisted.Should().NotBeNull();
        persisted!.CoveredThroughMessageId.Should().Be(nextMessages[7].Id);
    }

    [Fact]
    public async Task PrepareMessages_continues_without_new_summary_when_soft_compaction_fails()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("unused") { ThrowOnRun = true };
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = CreateLongMessages(conversation.Id, 8);

        var prepared = await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 500,
            modelAutoCompactTokenLimit: 80);

        prepared.Should().Equal(messages);
        execution.RunCount.Should().Be(1);
        (await repository.GetConversationContextSummaryAsync(conversation.Id)).Should().BeNull();
    }

    [Fact]
    public async Task PrepareMessages_fails_when_hard_window_is_exceeded_and_compaction_fails()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("unused") { ThrowOnRun = true };
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = CreateLongMessages(conversation.Id, 10);

        Func<Task> act = async () => await service.PrepareMessagesAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 120,
            modelAutoCompactTokenLimit: 80);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*model_context_window*compaction failed*");
    }

    [Fact]
    public async Task CompactNow_forces_compaction_and_passes_focus()
    {
        var (repository, conversation, profile) = await CreateRepositoryAsync();
        var execution = new FakeCompactionExecutionService("Manual summary");
        var service = new ConversationContextCompactionService(repository, execution);
        var messages = new[]
        {
            CreateMessage(conversation.Id, 0, MessageRole.User, "first short prompt"),
            CreateMessage(conversation.Id, 1, MessageRole.Assistant, "first short answer"),
            CreateMessage(conversation.Id, 2, MessageRole.User, "second short prompt")
        };

        var summary = await service.CompactNowAsync(
            conversation.Id,
            profile,
            "test-key",
            messages,
            modelContextWindow: 1_000,
            focus: "重点关注第二部分");

        summary.Should().NotBeNull();
        summary!.SummaryMarkdown.Should().Be("Manual summary");
        execution.RunCount.Should().Be(1);
        execution.Instructions.Should().Contain(instructions => instructions.Contains("重点关注第二部分", StringComparison.Ordinal));

        var persisted = await repository.GetConversationContextSummaryAsync(conversation.Id);
        persisted.Should().NotBeNull();
        persisted!.SummaryMarkdown.Should().Be("Manual summary");
        persisted.CoveredThroughMessageId.Should().Be(messages[2].Id);

        var storedMessages = await repository.ListMessagesAsync(conversation.Id);
        storedMessages.Should().BeEmpty("manual compaction stores only the summary, not slash command messages");
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

    private async Task<(SqliteConversationRepository Repository, ConversationRecord Conversation, ProviderProfile Profile)> CreateRepositoryAsync()
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
        var profile = new ProviderProfile(
            Guid.NewGuid(),
            "Local",
            "https://api.example.com/v1",
            "gpt-test",
            false,
            0.7,
            false,
            0.7,
            ApiStyle.OpenAICompatible,
            "secret:test",
            now,
            now);
        await profileRepository.UpsertProfileAsync(profile);

        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Chat",
            profile.Id,
            null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        await conversationRepository.UpsertConversationAsync(conversation);
        return (conversationRepository, conversation, profile);
    }

    private static MessageRecord[] CreateLongMessages(Guid conversationId, int count)
        => Enumerable.Range(0, count)
            .Select(index => CreateMessage(
                conversationId,
                index,
                index % 2 == 0 ? MessageRole.User : MessageRole.Assistant,
                $"msg-{index} " + new string('x', 120)))
            .ToArray();

    private static MessageRecord CreateMessage(
        Guid conversationId,
        int index,
        MessageRole role,
        string content)
    {
        var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(index);
        return new MessageRecord(
            Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
            conversationId,
            role,
            content,
            MessageStatus.Completed,
            timestamp,
            timestamp);
    }

    private sealed class FakeCompactionExecutionService : IAgentExecutionService
    {
        private readonly Queue<string> _summaries;

        public FakeCompactionExecutionService(params string[] summaries)
        {
            _summaries = new Queue<string>(summaries);
        }

        public int RunCount { get; private set; }

        public bool ThrowOnRun { get; init; }

        public List<string> Payloads { get; } = [];

        public List<string> Instructions { get; } = [];

        public Task<AgentExecutionResult> RunAsync(
            AgentExecutionRequest request,
            Func<string, CancellationToken, ValueTask>? onTextDelta,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Instructions.Add(request.Instructions);
            Payloads.Add(string.Join(
                "\n\n",
                request.Messages.Select(message => SelfClawAgentChatRuntime.ExtractTextFromContents(message.Contents))));

            if (ThrowOnRun)
            {
                throw new InvalidOperationException("Compaction failed.");
            }

            var summary = _summaries.Count == 0 ? $"Summary v{RunCount}" : _summaries.Dequeue();
            return Task.FromResult(new AgentExecutionResult(summary, null, null, TimeSpan.FromMilliseconds(1)));
        }
    }
}
