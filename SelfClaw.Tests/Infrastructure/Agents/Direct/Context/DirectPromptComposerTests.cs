using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using FluentAssertions;
using Microsoft.Extensions.AI;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class DirectPromptComposerTests
{
    private readonly ITestOutputHelper _output;

    public DirectPromptComposerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BuildMessages_replays_text_and_structured_tool_calls_from_prior_turns()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "Here you go."));

        AddToolCallSegment(messages, 1, toolRuns, CreateRun());
        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        result.Should().HaveCount(3);
        result[0].Role.Should().Be(ChatRole.User);
        result[0].Text.Should().Be("list the files");
        result[1].Role.Should().Be(ChatRole.Assistant);
        result[1].Contents.OfType<TextContent>().Should().ContainSingle()
            .Which.Text.Should().Be("Here you go.");
        var call = result[1].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which;
        call.CallId.Should().Be("call-1");
        call.Name.Should().Be("read_file");
        Assert.NotNull(call.Arguments);
        call.Arguments["relativePath"]?.ToString().Should().Be("README.md");
        result[2].Role.Should().Be(ChatRole.Tool);
        var functionResult = result[2].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which;
        functionResult.CallId.Should().Be("call-1");
        functionResult.Result.Should().Be("file body");
        functionResult.Exception.Should().BeNull();
    }

    [Fact]
    public void BuildMessages_replays_the_result_before_the_answer_that_used_it()
    {
        var (messages, turns, runs) = CreateHistory(("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "answer"));
        var run = CreateRun() with { MessageId = messages[0].Id, ConversationId = messages[0].ConversationId };
        runs.Add(run);
        messages[0] = messages[0] with
        {
            Segments =
            [
                new(messages[0].Id, 0, MessageSegmentKind.ToolCall, null, run.Id),
                new(messages[0].Id, 1, MessageSegmentKind.Text, "answer", null)
            ]
        };

        var replay = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive());

        replay.Select(message => message.Role).Should().Equal(ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        replay[0].Text.Should().BeEmpty();
        replay[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which.CallId.Should().Be(run.CorrelationId);
        replay[1].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which.Result.Should().Be("file body");
        replay[2].Text.Should().Be("answer");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Using the first result.")]
    public void BuildMessages_preserves_sequential_calls_in_segment_order(string betweenCalls)
    {
        var (messages, turns, runs) = CreateHistory(("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "done"));
        var first = CreateRun() with { MessageId = messages[0].Id, ConversationId = messages[0].ConversationId };
        var second = CreateRun() with { MessageId = messages[0].Id, ConversationId = messages[0].ConversationId, CorrelationId = "call-2", ArgumentsJson = "{\"query\":\"file body\"}" };
        runs.AddRange([first, second]);
        messages[0] = messages[0] with
        {
            Segments =
            [
                new(messages[0].Id, 4, MessageSegmentKind.Text, "done", null),
                new(messages[0].Id, 3, MessageSegmentKind.ToolCall, null, second.Id),
                new(messages[0].Id, 1, MessageSegmentKind.ToolCall, null, first.Id),
                new(messages[0].Id, 0, MessageSegmentKind.Text, "Checking.", null),
                new(messages[0].Id, 2, MessageSegmentKind.Text, betweenCalls, null)
            ]
        };

        var replay = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive());

        replay.Select(message => message.Role).Should().Equal(
            ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        replay[0].Text.Should().Be("Checking.");
        replay[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which.CallId.Should().Be("call-1");
        replay[1].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which.CallId.Should().Be("call-1");
        replay[2].Text.Should().Be(betweenCalls);
        replay[2].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which.CallId.Should().Be("call-2");
        replay[3].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which.CallId.Should().Be("call-2");
        replay[4].Text.Should().Be("done");
    }

    [Fact]
    public void BuildMessages_marks_failed_tool_results_as_errors()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, ""));
        var failedRun = CreateRun() with
        {
            Status = ToolExecutionStatus.Failed,
            ResultSummary = "permission denied",
            ResultContent = null
        };
        AddToolCallSegment(messages, 0, toolRuns, failedRun);

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        var functionResult = result.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Should().ContainSingle().Which;
        functionResult.Exception.Should().NotBeNull();
        functionResult.Result.Should().Be("permission denied");
    }

    [Fact]
    public void BuildMessages_skips_thinking_blocks_and_unresolved_tool_calls()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "answer"));
        messages[0] = messages[0] with
        {
            Segments =
            [
                new MessageSegmentRecord(messages[0].Id, 0, MessageSegmentKind.Thinking, "private reasoning", null),
                new MessageSegmentRecord(messages[0].Id, 1, MessageSegmentKind.Text, "answer", null),
                new MessageSegmentRecord(messages[0].Id, 2, MessageSegmentKind.ToolCall, null, Guid.NewGuid())
            ]
        };

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        // The thinking block and the tool call without a matching run are transcript-only; the
        // replayed assistant message carries just the text, with no dangling tool message.
        result.Should().ContainSingle().Which.Should().Match<ChatMessage>(message =>
            message.Role == ChatRole.Assistant && message.Text == "answer");
        result.SelectMany(message => message.Contents).Should().NotContain(item => item is FunctionCallContent);
        result.SelectMany(message => message.Contents).Should().NotContain(item => item is FunctionResultContent);
    }

    [Fact]
    public void BuildMessages_does_not_replay_unstructured_assistant_markdown()
    {
        var (messages, turns, tools) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "not a structured block"),
            ("user", MessageRole.User, ConversationTurnStatus.Running, "next question"));
        messages[0] = messages[0] with { Segments = null };
        var result = new DirectPromptComposer().BuildMessages(messages, turns, tools, "", [], NoAdjustments(), Interactive());
        result.Should().ContainSingle().Which.Text.Should().Be("next question");
    }

    [Fact]
    public void BuildMessages_appends_the_continuation_prompt_after_a_truncated_answer()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "full answer"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial answer"));

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        result.Last().Role.Should().Be(ChatRole.User);
        result.Last().Text.Should().Contain("Continue exactly where you left off");
    }

    [Fact]
    public void BuildMessages_sends_the_full_history_when_there_is_no_compression()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "first"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "second"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "third"));

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        result.Select(message => message.Text).Should().Equal("first", "second", "third");
    }

    [Fact]
    public void BuildMessages_rejects_a_completion_batch_that_exceeds_the_size_cap()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new SubagentCompletionBatch(
        [
            new SubagentCompletionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new SubagentIdentity("worker", "Worker"), "Check the workspace.", SubagentTaskStatus.Succeeded, 1,
                new SubagentCompletionResult(new string('r', 70 * 1024), false, null, null), new TurnUsage(InputTokens: 10, OutputTokens: 10),
                new SubagentTiming(now, now, now, 0))
        ]);
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, batch);

        var action = () => new DirectPromptComposer().BuildMessages([], [], [], "", [], NoAdjustments(), context);

        action.Should().Throw<InvalidDataException>().WithMessage("*64 KiB*");
    }

    [Fact]
    public void BuildMessages_injects_the_summary_as_the_third_system_section()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        var compression = CreateCompaction(messages[0].Sequence);

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, "agent instructions",
            ["capability instructions"], NoAdjustments(), Interactive(), compression: compression);

        var system = result[0];
        system.Role.Should().Be(ChatRole.System);
        system.Text.Should().StartWith("agent instructions\n\nThis is a compressed summary of earlier conversation");
        system.Text.Should().Contain("<selfclaw-context-summary version=\"1\">\nearlier decisions\n</selfclaw-context-summary>");
        system.Text.Should().EndWith("capability instructions", "capability instructions stay behind the summary");
        result.Skip(1).Select(message => message.Text).Should().Equal("latest");
    }

    [Fact]
    public void BuildMessages_places_the_summary_after_the_completion_instruction()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        var compression = CreateCompaction(messages[0].Sequence);
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CreateCompletionBatch());

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, "agent instructions",
            ["capability instructions"], NoAdjustments(), context, compression: compression);

        var system = result[0].Text;
        system.IndexOf("completed Subagent results", StringComparison.Ordinal).Should().BePositive();
        system.IndexOf("<selfclaw-context-summary", StringComparison.Ordinal)
            .Should().BeGreaterThan(system.IndexOf("completed Subagent results", StringComparison.Ordinal));
        system.IndexOf("capability instructions", StringComparison.Ordinal)
            .Should().BeGreaterThan(system.IndexOf("<selfclaw-context-summary", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMessages_replays_only_messages_after_the_compression_boundary()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "middle"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(),
            Interactive(), compression: CreateCompaction(messages[1].Sequence));

        result.Skip(1).Select(message => message.Text).Should().Equal("latest");
    }

    [Fact]
    public void BuildMessages_keeps_the_continuation_prompt_and_completion_batch_after_the_summary()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial answer"));
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CreateCompletionBatch());

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), context,
            compression: CreateCompaction(messages[0].Sequence));

        var texts = result.Select(message => message.Text ?? string.Empty).ToArray();
        texts[0].Should().Contain("<selfclaw-context-summary");
        texts[1].Should().Be("partial answer");
        texts[2].Should().Be(DirectPromptComposer.ContinuationPrompt);
        texts[3].Should().StartWith("<selfclaw-subagent-results version=\"1\">");
    }

    [Fact]
    public void BuildUnits_produces_one_unit_per_stored_message()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "working"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "thanks"));
        AddToolCallSegment(messages, 1, runs, CreateRun());

        var units = new DirectPromptComposer().BuildUnits(messages, turns, runs, NoAdjustments());

        // The compaction record's SourceMessageCount is PrefixUnits.Count, so a unit may never stand for
        // more than one stored message: the tool round trip stays inside its assistant message's unit.
        units.Should().HaveCount(3);
        units.Select(unit => unit.SourceSequence).Should().Equal(messages.Select(message => message.Sequence));
        units[1].Messages.Should().HaveCount(2, "the call and its result replay inside one unit");
        units[1].Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle();
        units[1].Messages[1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>();
    }

    [Fact]
    public void PlanCompression_counts_the_output_reserve_toward_the_trigger()
    {
        var units = CountedUnits(3000, 2000);

        // threshold = 6000 (8000 * 0.75) and the default reserve is 4096: the prompt alone (5000) sits under
        // the threshold, but 5000 + 4096 would exceed the 8000 window, so compaction has to start here.
        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), new DirectPromptBudget(8000, null), countedTokens: 5000);

        plan.ShouldCompress.Should().BeTrue();
        plan.ThroughSequence.Should().Be(units[0].SourceSequence);
        plan.SourceTokens.Should().Be(3000);
    }

    [Fact]
    public void PlanCompression_does_not_compress_below_the_threshold()
    {
        var units = CountedUnits(100, 100, 100);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 299);

        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
        plan.ThroughSequence.Should().BeNull();
        plan.SourceTokens.Should().BeNull();
    }

    [Fact]
    public void PlanCompression_compresses_the_prefix_and_retains_the_newest_units()
    {
        var units = CountedUnits(100, 100, 100, 100);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // threshold = 300, keepTarget = 150: the newest unit fits, the second would exceed the target.
        plan.ShouldCompress.Should().BeTrue();
        plan.RetainedUnitCount.Should().Be(1);
        plan.PrefixUnits.Should().HaveCount(3);
        plan.ThroughSequence.Should().Be(units[2].SourceSequence, "the boundary is the last compressed unit");
        plan.SourceTokens.Should().Be(300);
    }

    [Fact]
    public void PlanCompression_always_retains_the_newest_unit()
    {
        var units = CountedUnits(500, 500);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 1000);

        plan.ShouldCompress.Should().BeTrue();
        plan.RetainedUnitCount.Should().Be(1);
        plan.PrefixUnits.Select(unit => unit.SourceSequence).Should().Equal(units[0].SourceSequence);
        plan.SourceTokens.Should().Be(500);
    }

    [Fact]
    public void PlanCompression_ignores_units_the_existing_boundary_already_covers()
    {
        var units = CountedUnits(100, 100, 100);

        var plan = new DirectPromptComposer().PlanCompression(units, CreateCompaction(units[1].SourceSequence),
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Only the unit after the boundary may be compacted again, and compacting it alone would leave
        // nothing to replay, so the plan declines.
        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
        plan.ThroughSequence.Should().BeNull();
    }

    [Fact]
    public void PlanCompression_declines_when_units_were_never_counted()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "first"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "second"));
        var units = new DirectPromptComposer().BuildUnits(messages, turns, runs, NoAdjustments());

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Without per-unit counts nothing can be sized, so compaction degrades instead of estimating.
        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
    }

    [Fact]
    public void PlanCompression_declines_when_the_policy_is_disabled()
    {
        var units = CountedUnits(100, 100);

        var plan = new DirectPromptComposer().PlanCompression(units, null,
            new DirectCompressionPolicy(Enabled: false), Window(400), countedTokens: 300);

        plan.ShouldCompress.Should().BeFalse();
    }

    [Fact]
    public void PlanCompression_falls_back_to_the_absolute_threshold_without_a_context_window()
    {
        var units = CountedUnits(100, 100, 100, 100);
        var policy = new DirectCompressionPolicy(AbsoluteThresholdTokens: 250);

        var plan = new DirectPromptComposer().PlanCompression(units, null, policy, Window(null), countedTokens: 250);

        plan.ShouldCompress.Should().BeTrue();
        plan.ThroughSequence.Should().Be(units[2].SourceSequence);
    }

    private static IReadOnlyList<DirectPromptHistoryUnit> CountedUnits(params int[] tokensPerUnit)
    {
        var (messages, turns, runs) = CreateHistory(tokensPerUnit
            .Select(tokens => ($"unit-{tokens}", MessageRole.User, ConversationTurnStatus.Succeeded, tokens.ToString()))
            .ToArray());
        return new DirectPromptComposer().BuildUnits(messages, turns, runs, NoAdjustments())
            .Select((unit, index) => unit with { Tokens = tokensPerUnit[index] })
            .ToArray();
    }

    private static DirectPromptBudget Window(int? contextWindowTokens) => new(contextWindowTokens, 0);

    private static ConversationCompactionRecord CreateCompaction(long throughSequence, string summary = "earlier decisions")
    {
        var now = DateTimeOffset.UtcNow;
        return new ConversationCompactionRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), throughSequence,
            summary, "test-model", 1, 10, 5, now);
    }

    private static SubagentCompletionBatch CreateCompletionBatch()
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentCompletionBatch(
        [
            new SubagentCompletionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new SubagentIdentity("worker", "Worker"), "Check the workspace.", SubagentTaskStatus.Succeeded, 1,
                new SubagentCompletionResult("done", false, null, null), new TurnUsage(InputTokens: 10, OutputTokens: 10),
                new SubagentTiming(now, now, now, 0))
        ]);
    }

    private static IReadOnlyDictionary<Guid, string> NoAdjustments() =>
        new Dictionary<Guid, string>();

    private static DirectTurnExecutionContext Interactive() =>
        new(DirectTurnOrigin.Interactive, null, null);

    private static (List<MessageRecord> Messages, List<ConversationTurnRecord> Turns, List<ToolExecutionRecord> ToolRuns) CreateHistory(
        params (string Key, MessageRole Role, ConversationTurnStatus Status, string Text)[] entries)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MessageRecord>();
        var turns = new List<ConversationTurnRecord>();
        foreach (var (key, role, status, text) in entries)
        {
            var messageId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            turns.Add(new(turnId, conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, status, now, now));
            var segments = role == MessageRole.Assistant && text.Length > 0
                ? new List<MessageSegmentRecord> { new(messageId, 0, MessageSegmentKind.Text, text, null) }
                : null;
            messages.Add(new MessageRecord(
                messageId, conversationId, turnId, messages.Count + 1, role, text, MessageStatus.Sealed, now, now, Segments: segments));
        }

        return (messages, turns, []);
    }

    private static ToolExecutionRecord CreateRun()
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "read_file",
            "{\"relativePath\":\"README.md\"}",
            ToolExecutionStatus.Completed,
            "Read README.md.",
            "call-1",
            5,
            now,
            now,
            ResultContent: "file body");
    }

    private static ToolExecutionRecord AddToolCallSegment(
        List<MessageRecord> messages,
        int messageIndex,
        List<ToolExecutionRecord> toolRuns,
        ToolExecutionRecord run)
    {
        var message = messages[messageIndex];
        run = run with { MessageId = message.Id, ConversationId = message.ConversationId };
        toolRuns.Add(run);
        var segments = (message.Segments ?? []).ToList();
        segments.Add(new MessageSegmentRecord(message.Id, segments.Count, MessageSegmentKind.ToolCall, null, run.Id));
        messages[messageIndex] = message with { Segments = segments };
        return run;
    }
}
