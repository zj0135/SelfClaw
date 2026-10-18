using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTurnRecorderTests
{
    [Fact]
    public async Task ApplyEventAsync_records_the_shared_event_protocol_and_commits_the_terminal_state()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new RunStartedEvent("session", "model", null));
        context.State.Messages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        await context.ApplyAsync(new AssistantThinkingDeltaEvent("thinking", "reason"));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "answer"));
        await context.ApplyAsync(new ToolCallStartedEvent("call-1", "mcp__git__status", "{}", ToolCallKind.Read,
            ToolSourceKind.Mcp, "git", "status"));
        await context.ApplyAsync(new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read 1 file", "contents"));
        await context.ApplyAsync(new UsageReportedEvent(new TurnUsage(InputTokens: 11, OutputTokens: 7, TotalTokens: 18)));
        await context.ApplyAsync(new RunStatusEvent(AgentRunStatus.Thinking));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "answer"));

        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Should().ContainSingle().Subject;
        turn.Id.Should().Be(context.Record.Id);
        turn.Status.Should().Be(ConversationTurnStatus.Succeeded);
        turn.Usage.Should().BeEquivalentTo(new TurnUsage(InputTokens: 11, OutputTokens: 7, TotalTokens: 18, UncachedInputTokens: 11));
        var message = (await context.Conversations.ListMessagesAsync(context.Conversation.Id)).Single(item => item.Role == MessageRole.Assistant);
        message.Id.Should().NotBe(turn.Id);
        message.TurnId.Should().Be(turn.Id);
        message.Status.Should().Be(MessageStatus.Sealed);
        message.MarkdownContent.Should().Be("answer");
        (message.Segments ?? []).Select(segment => segment.Kind).Should().Equal(MessageSegmentKind.Thinking, MessageSegmentKind.Text, MessageSegmentKind.ToolCall);
        var tool = (await context.Conversations.ListToolExecutionsAsync(context.Conversation.Id)).Should().ContainSingle().Subject;
        tool.MessageId.Should().Be(message.Id);
        tool.Status.Should().Be(ToolExecutionStatus.Completed);
        tool.SourceKind.Should().Be(ToolSourceKind.Mcp);
        tool.ResultContent.Should().Be("contents");
        context.State.ActivityText.Should().Be("正在思考...");
    }

    [Fact]
    public async Task Terminal_commit_persists_pending_compactions_together_with_the_turn_status()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var compaction = Compaction(context, throughSequence: 1, "compressed history");
        context.Turn.PendingCompactions.Add(compaction);
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "answer"));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "answer"));

        var stored = (await context.ListCompactionsAsync()).Should().ContainSingle().Subject;
        stored.Should().BeEquivalentTo(compaction);
        (await context.LatestCompactionAsync()).Should().BeEquivalentTo(compaction);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Status.Should().Be(ConversationTurnStatus.Succeeded);
    }

    [Fact]
    public async Task Replaying_a_terminal_event_keeps_a_single_compaction_row()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        context.Turn.PendingCompactions.Add(Compaction(context, throughSequence: 1, "compressed history"));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));

        (await context.ListCompactionsAsync()).Should().ContainSingle();
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyEventAsync_coalesces_consecutive_thinking_deltas_into_one_internal_block()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantThinkingDeltaEvent("thinking", "first "));
        await context.ApplyAsync(new AssistantThinkingDeltaEvent("thinking", "second"));
        var message = context.State.Messages.Single(item => item.Role == MessageRole.Assistant);
        message.Segments.Should().ContainSingle().Which.Text.Should().Be("first second");
        message.MarkdownContent.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyEventAsync_keeps_whitespace_only_text_deltas_and_ignores_empty_ones()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "   "));
        context.State.Messages.Should().ContainSingle();
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "line one"));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "\n  "));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", string.Empty));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "line two"));
        context.State.Messages.Last().MarkdownContent.Should().Be("line one\n  line two");
    }

    [Fact]
    public async Task ApplyEventAsync_publishes_the_first_visible_delta_immediately_and_coalesces_the_rest()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var immediates = new List<bool>();
        context.State.TranscriptChanged += immediates.Add;
        await context.ApplyAsync(new RunStatusEvent(AgentRunStatus.Requesting));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "hello"));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", " world"));
        await context.ApplyAsync(new AssistantThinkingDeltaEvent("thinking", "hmm"));
        immediates.Should().Equal(false, true, false, false);
    }

    [Fact]
    public async Task ApplyEventAsync_limits_tool_result_content_before_persistence()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new ToolCallStartedEvent("call-1", "read_file", "{}", ToolCallKind.Read));
        await context.ApplyAsync(new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read file",
            new string('x', TranscriptToolResultLimiter.MaximumStoredCharacters + 1000)));
        var tool = (await context.Conversations.ListToolExecutionsAsync(context.Conversation.Id)).Single();
        tool.ResultContent.Should().HaveLength(TranscriptToolResultLimiter.MaximumStoredCharacters)
            .And.EndWith("[SelfClaw truncated the stored tool result at 64 KiB.]");
        var message = (await context.Conversations.ListMessagesAsync(context.Conversation.Id)).Single(item => item.Id == tool.MessageId);
        message.Status.Should().Be(MessageStatus.Streaming, "progress persists the real fragment together with its tool");
    }

    [Fact]
    public async Task FinalizeInterruptedAsync_preserves_partial_text_and_closes_running_tools()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "partial"));
        await context.ApplyAsync(new ToolCallStartedEvent("call-1", "run_shell_command", "{}", ToolCallKind.Run));
        await context.Recorder.FinalizeInterruptedAsync(context.State, context.Turn, TurnFinalizationKind.Cancelled,
            "Generation stopped.", context.Finalizer);
        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Status.Should().Be(ConversationTurnStatus.Cancelled);
        turn.ErrorMessage.Should().Be("Generation stopped.");
        var message = context.State.Messages.Last();
        message.Status.Should().Be(MessageStatus.Interrupted);
        message.MarkdownContent.Should().Be("partial");
        context.State.ToolRuns.Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task ApplyEventAsync_ignores_a_duplicate_terminal_event()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Failed, null, "late failure"));
        context.State.Turns.Single().Status.Should().Be(ConversationTurnStatus.Succeeded);
        context.State.Messages.Should().HaveCount(2);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyEventAsync_reloads_the_persisted_terminal_state_when_the_commit_loses_its_cas()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "losing candidate"));
        var now = DateTimeOffset.UtcNow;
        var winnerId = Guid.NewGuid();
        var winner = new MessageRecord(winnerId, context.Conversation.Id, context.Record.Id,
            await context.Turns.ReserveMessageSequenceAsync(context.Conversation.Id), MessageRole.Assistant,
            "persisted winner", MessageStatus.Interrupted, now, now,
            Segments: [new MessageSegmentRecord(winnerId, 0, MessageSegmentKind.Text, "persisted winner", null)]);
        var turn = context.Record with { Status = ConversationTurnStatus.Failed, CompletedAtUtc = now, ErrorMessage = "already finalized" };
        (await context.Turns.TryFinalizeTurnAsync(new ConversationTurnCommit(turn, [winner], []))).Should().BeTrue();
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "losing candidate"));
        context.State.Messages.Where(item => item.Role == MessageRole.Assistant).Should().ContainSingle().Which.Id.Should().Be(winnerId);
        context.State.Turns.Single().ErrorMessage.Should().Be("already finalized");
        context.Turn.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyEventAsync_records_a_blocked_turn_without_creating_an_assistant()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Blocked, null, "Blocked by the content filter."));
        context.State.Messages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Status.Should().Be(ConversationTurnStatus.Blocked);
        turn.ErrorMessage.Should().Be("Blocked by the content filter.");
        turn.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task No_output_failure_persists_usage_on_the_turn_and_keeps_unknown_total()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new UsageReportedEvent(new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: null)));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Failed, null, "no response"));
        context.State.Messages.Should().ContainSingle();
        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Usage?.TotalTokens.Should().BeNull();
        turn.Usage?.InputTokens.Should().Be(3);
        turn.Status.Should().Be(ConversationTurnStatus.Failed);
        (await context.ScalarAsync("SELECT total_tokens FROM turn_usage")).Should().Be(DBNull.Value);
    }

    [Fact]
    public async Task Compaction_started_only_republishes_the_activity_text()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var immediates = new List<bool>();
        context.State.TranscriptChanged += immediates.Add;

        await context.ApplyAsync(new ContextCompactionStartedEvent(EstimatedTokens: 90_000, ThresholdTokens: 96_000));

        context.State.ActivityText.Should().Be("正在压缩上下文...");
        immediates.Should().Equal(true);
        context.State.Compactions.Should().BeEmpty();
        context.Turn.PendingCompactions.Should().BeEmpty();
        context.State.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Compaction_completed_publishes_the_divider_immediately_and_commits_the_record()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var compaction = Compaction(context, throughSequence: 1, "compressed history");
        var immediates = new List<bool>();
        context.State.TranscriptChanged += immediates.Add;

        await context.ApplyAsync(new ContextCompactionStartedEvent(EstimatedTokens: 90_000, ThresholdTokens: 96_000));
        await context.ApplyAsync(new ContextCompactionCompletedEvent(compaction));

        immediates.Should().Equal(true, true);
        context.State.Compactions.Should().ContainSingle().Which.Should().BeEquivalentTo(compaction);
        context.Turn.PendingCompactions.Should().ContainSingle().Which.Should().BeEquivalentTo(compaction);

        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));

        (await context.ListCompactionsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(compaction);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Status.Should().Be(ConversationTurnStatus.Succeeded);
    }

    [Fact]
    public async Task Compaction_failed_updates_the_activity_text_without_touching_the_transcript()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new ContextCompactionStartedEvent(EstimatedTokens: 90_000, ThresholdTokens: 96_000));

        await context.ApplyAsync(new ContextCompactionFailedEvent(
            "Context compaction failed; the turn continues with the uncompacted history."));

        context.State.ActivityText.Should().Be("上下文压缩失败，本回合继续使用未压缩的上下文");
        context.State.Compactions.Should().BeEmpty();
        context.Turn.PendingCompactions.Should().BeEmpty();
        context.State.Messages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);

        // The C7 status mapping wins again once the run moves on, so the diagnostic cannot linger.
        await context.ApplyAsync(new RunStatusEvent(AgentRunStatus.Compacting));
        context.State.ActivityText.Should().Be("正在压缩上下文...");
    }

    [Fact]
    public async Task In_turn_compaction_completion_is_observation_only()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new RunStatusEvent(AgentRunStatus.Requesting));
        var activity = context.State.ActivityText;
        var immediates = new List<bool>();
        context.State.TranscriptChanged += immediates.Add;

        await context.ApplyAsync(new InTurnContextCompactionCompletedEvent(CoveredUnitCount: 4, SourceTokens: 88_000,
            SummaryTokens: 700, Model: "summary-model"));

        context.Turn.PendingCompactions.Should().BeEmpty();
        context.State.Compactions.Should().BeEmpty();
        context.State.ActivityText.Should().Be(activity);
        immediates.Should().BeEmpty("an in-turn compaction leaves the transcript untouched");

        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));

        (await context.ListCompactionsAsync()).Should().BeEmpty("an in-turn compaction is never persisted");
    }

    private static ConversationCompactionRecord Compaction(ConversationTurnTestContext context, long throughSequence, string summary)
        => new(Guid.NewGuid(), context.Conversation.Id, context.Record.Id, throughSequence, summary, "test-model",
            SourceMessageCount: 3, SourceTokens: 4_200, SummaryTokens: 120, DateTimeOffset.UtcNow);
}
