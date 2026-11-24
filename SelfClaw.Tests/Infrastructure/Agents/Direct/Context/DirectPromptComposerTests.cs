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
    public void Prompt_replays_text_and_structured_tool_calls_from_prior_turns()
    {
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "Here you go."));

        AddToolCallSegment(messages, 1, toolRuns, CreateRun());
        var result = DirectPromptAssembly.Flatten(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

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
    public void Prompt_replays_the_result_before_the_answer_that_used_it()
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

        var replay = DirectPromptAssembly.Flatten(messages, turns, runs, "", [], NoAdjustments(), Interactive());

        replay.Select(message => message.Role).Should().Equal(ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        replay[0].Text.Should().BeEmpty();
        replay[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Which.CallId.Should().Be(run.CorrelationId);
        replay[1].Contents.OfType<FunctionResultContent>().Should().ContainSingle().Which.Result.Should().Be("file body");
        replay[2].Text.Should().Be("answer");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Using the first result.")]
    public void Prompt_preserves_sequential_calls_in_segment_order(string betweenCalls)
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

        var replay = DirectPromptAssembly.Flatten(messages, turns, runs, "", [], NoAdjustments(), Interactive());

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
    public void Prompt_marks_failed_tool_results_as_errors()
    {
        var (messages, turns, toolRuns) = CreateHistory(("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, ""));
        var failedRun = CreateRun() with
        {
            Status = ToolExecutionStatus.Failed,
            ResultSummary = "permission denied",
            ResultContent = null
        };
        AddToolCallSegment(messages, 0, toolRuns, failedRun);

        var result = DirectPromptAssembly.Flatten(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        var functionResult = result.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>().Should().ContainSingle().Which;
        functionResult.Exception.Should().NotBeNull();
        functionResult.Result.Should().Be("permission denied");
    }

    [Fact]
    public void Prompt_skips_thinking_blocks_and_unresolved_tool_calls()
    {
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

        var result = DirectPromptAssembly.Flatten(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        // The thinking block and the tool call without a matching run are transcript-only; the
        // replayed assistant message carries just the text, with no dangling tool message.
        result.Should().ContainSingle().Which.Should().Match<ChatMessage>(message =>
            message.Role == ChatRole.Assistant && message.Text == "answer");
        result.SelectMany(message => message.Contents).Should().NotContain(item => item is FunctionCallContent);
        result.SelectMany(message => message.Contents).Should().NotContain(item => item is FunctionResultContent);
    }

    [Fact]
    public void Prompt_does_not_replay_unstructured_assistant_markdown()
    {
        var (messages, turns, tools) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "not a structured block"),
            ("user", MessageRole.User, ConversationTurnStatus.Running, "next question"));
        messages[0] = messages[0] with { Segments = null };
        var result = DirectPromptAssembly.Flatten(messages, turns, tools, "", [], NoAdjustments(), Interactive());
        result.Should().ContainSingle().Which.Text.Should().Be("next question");
    }

    [Fact]
    public void Prompt_replays_a_truncated_answer_without_any_runtime_continuation_prompt()
    {
        var (messages, turns, toolRuns) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "full answer"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial answer"));

        var result = DirectPromptAssembly.Flatten(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        result.Select(message => message.Text).Should().Equal("full answer", "partial answer");
    }

    [Fact]
    public void Prompt_sends_the_full_history_when_there_is_no_compression()
    {
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "first"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "second"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "third"));

        var result = DirectPromptAssembly.Flatten(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive());

        result.Select(message => message.Text).Should().Equal("first", "second", "third");
    }

    [Fact]
    public void Prompt_rejects_a_completion_batch_that_exceeds_the_size_cap()
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

        var action = () => DirectPromptAssembly.Flatten([], [], [], "", [], NoAdjustments(), context);

        action.Should().Throw<InvalidDataException>().WithMessage("*64 KiB*");
    }

    [Fact]
    public void Prompt_injects_the_summary_after_the_fixed_system_sections()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        var compression = CreateCompaction(messages[0].Sequence);

        var result = DirectPromptAssembly.Flatten(messages, turns, runs, "agent instructions",
            ["capability instructions"], NoAdjustments(), Interactive(), compression: compression);

        var system = result[0];
        system.Role.Should().Be(ChatRole.System);
        system.Text.Should().StartWith(
            "agent instructions\n\ncapability instructions\n\nThis is a compressed summary of earlier conversation",
            "the instructions keep their bytes whether or not a summary follows them");
        system.Text.Should().EndWith("<selfclaw-context-summary version=\"1\">\nearlier decisions\n</selfclaw-context-summary>");
        result.Skip(1).Select(message => message.Text).Should().Equal("latest");
    }

    [Fact]
    public void Prompt_keeps_the_fixed_sections_byte_identical_when_a_summary_is_added()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        var compression = CreateCompaction(messages[0].Sequence);
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CreateCompletionBatch());

        var uncompacted = DirectPromptAssembly.Flatten(messages, turns, runs, "agent instructions",
            ["capability instructions"], NoAdjustments(), context)[0].Text;
        var compacted = DirectPromptAssembly.Flatten(messages, turns, runs, "agent instructions",
            ["capability instructions"], NoAdjustments(), context, compression: compression)[0].Text;

        uncompacted.IndexOf("completed Subagent results", StringComparison.Ordinal).Should().BePositive();
        uncompacted.Should().EndWith("capability instructions");
        compacted.Should().StartWith(uncompacted + "\n\n",
            "a compaction appends its summary and never rewrites the prefix a provider may have cached");
        compacted.Should().Contain("<selfclaw-context-summary");
    }

    [Fact]
    public void BuildUnits_replays_only_what_follows_the_anchored_tool_call()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "first step"));
        AddToolCallSegment(messages, 1, runs, CreateRun());
        AddTextSegment(messages, 1, "second step");
        AddToolCallSegment(messages, 1, runs, CreateRun() with { CorrelationId = "call-2" });
        AddTextSegment(messages, 1, "final answer");
        // The boundary ends at the user message; the anchor reaches into the assistant message after it.
        var compression = CreateCompaction(messages[0].Sequence) with { ThroughToolRunId = runs[0].Id };

        var units = DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments(), compression: compression);

        var unit = units.Should().HaveCount(2).And.Subject.Last();
        unit.SourceSequence.Should().Be(messages[1].Sequence, "the shortened message is still one unit");
        unit.Messages.Select(message => message.Role)
            .Should().Equal(ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant);
        unit.Messages[0].Text.Should().Be("second step", "the text and the call before the anchor are summarized");
        unit.Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle()
            .Which.CallId.Should().Be("call-2");
        unit.Messages[2].Text.Should().Be("final answer");
    }

    [Fact]
    public void BuildUnits_drops_a_message_the_anchor_covers_to_its_end()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "working"));
        AddToolCallSegment(messages, 1, runs, CreateRun());
        var compression = CreateCompaction(messages[0].Sequence) with { ThroughToolRunId = runs[0].Id };

        var units = DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments(), compression: compression);

        units.Should().ContainSingle().Which.SourceSequence.Should().Be(messages[0].Sequence);
    }

    [Fact]
    public void BuildUnits_replays_the_whole_message_when_the_anchor_resolves_to_no_call()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "working"));
        AddToolCallSegment(messages, 1, runs, CreateRun());
        var compression = CreateCompaction(messages[0].Sequence) with { ThroughToolRunId = Guid.NewGuid() };

        var units = DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments(), compression: compression);

        // A dangling anchor is a soft reference like the sequence boundary: it costs a longer replay, never a turn.
        units.Should().HaveCount(2);
        units[1].Messages.Should().HaveCount(2, "the call and its result replay as if no anchor were set");
    }

    [Fact]
    public void Prompt_replays_only_messages_after_the_compression_boundary()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "middle"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));

        var result = DirectPromptAssembly.Flatten(messages, turns, runs, "", [], NoAdjustments(),
            Interactive(), compression: CreateCompaction(messages[1].Sequence));

        result.Skip(1).Select(message => message.Text).Should().Equal("latest");
    }

    [Fact]
    public void Prompt_keeps_the_completion_batch_after_the_summary_without_any_continuation_prompt()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial answer"));
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CreateCompletionBatch());

        var result = DirectPromptAssembly.Flatten(messages, turns, runs, "", [], NoAdjustments(), context,
            compression: CreateCompaction(messages[0].Sequence));

        var texts = result.Select(message => message.Text ?? string.Empty).ToArray();
        texts[0].Should().Contain("<selfclaw-context-summary");
        texts[1].Should().Be("partial answer");
        texts.Should().HaveCount(3, "a truncated answer is resumed by the user, never by a runtime prompt");
        texts[2].Should().StartWith("<selfclaw-subagent-results version=\"1\">");
    }

    [Fact]
    public void BuildUnits_produces_one_unit_per_stored_message()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "list the files"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "working"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "thanks"));
        AddToolCallSegment(messages, 1, runs, CreateRun());

        var units = DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments());

        // The compaction record's SourceMessageCount is PrefixUnits.Count, so a unit may never stand for
        // more than one stored message: the tool round trip stays inside its assistant message's unit.
        units.Should().HaveCount(3);
        units.Select(unit => unit.SourceSequence).Should().Equal(messages.Select(message => message.Sequence));
        units[1].Messages.Should().HaveCount(2, "the call and its result replay inside one unit");
        units[1].Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle();
        units[1].Messages[1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>();
    }

    [Theory]
    [InlineData(3600, true)]
    [InlineData(3400, false)]
    public void PlanCompression_caps_the_trigger_at_the_usable_input_space(int countedTokens, bool expected)
    {
        var units = CountedUnits(countedTokens - 2000, 2000);

        // The window asks for 6000 (8000 * 0.75), but the default reserve of 4096 leaves only 3904 of it for
        // input and the trigger stays at 90% of that. A prompt below 3513 still fits; one above it has to be
        // compacted although it is nowhere near the configured share.
        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), new DirectPromptBudget(8000, null), countedTokens);

        plan.ShouldCompress.Should().Be(expected);
        plan.ThresholdTokens.Should().Be(3513);
    }

    // The two profiles the repeated compaction was reported with: an output cap that is more than a third of the
    // window. Adding it to the count put the trigger at 13% and 11% of the window, below the 25% a compaction
    // retains, so a compacted prompt was compacted again on every round trip.
    [Theory]
    [InlineData(1_048_576, 384_000, 524_288)]
    [InlineData(1_000_000, 393_216, 500_000)]
    public void PlanCompression_keeps_the_trigger_at_the_configured_share_whatever_the_output_cap(
        int window, int maxOutputTokens, int expectedTrigger)
    {
        var policy = new DirectCompressionPolicy(ThresholdRatio: 0.5d);
        var budget = new DirectPromptBudget(window, maxOutputTokens);
        var units = CountedUnits(Enumerable.Repeat(9_000, 30).ToArray());

        // 270,000 of history and 15,000 of instructions: a quarter of the window, far below the trigger.
        var plan = DirectCompactionPlanner.PlanCompression(units, null, policy, budget, countedTokens: 285_000);

        plan.ShouldCompress.Should().BeFalse();
        plan.ThresholdTokens.Should().Be(expectedTrigger);
    }

    [Fact]
    public void PlanCompression_leaves_a_compacted_prompt_room_to_grow()
    {
        var policy = new DirectCompressionPolicy(ThresholdRatio: 0.5d);
        var budget = new DirectPromptBudget(1_048_576, 384_000);
        const int Instructions = 15_000;
        const int SummaryTokens = 4_096;
        var units = CountedUnits(Enumerable.Repeat(9_000, 60).ToArray());

        var plan = DirectCompactionPlanner.PlanCompression(units, null, policy, budget,
            countedTokens: Instructions + (60 * 9_000));

        // The prompt is brought back to half the trigger as a whole: 262,144 less the instructions leaves
        // 247,144 for history, which is 27 units.
        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Should().HaveCount(33);
        var retained = units.Skip(33).ToArray();

        // Three more round trips arrive. The prompt is at 289,096, well under the trigger of 524,288.
        var grown = retained.Concat(CountedUnits(9_000, 9_000, 9_000)
                .Select((unit, index) => unit with { SourceSequence = 1_000 + index }))
            .ToArray();
        var next = DirectCompactionPlanner.PlanCompression(grown, null, policy, budget,
            countedTokens: Instructions + SummaryTokens + (30 * 9_000));

        next.ShouldCompress.Should().BeFalse("a compaction has to buy room, not leave the prompt at the trigger");
    }

    [Fact]
    public void PlanCompression_takes_what_a_summary_cannot_replace_out_of_the_retained_history()
    {
        var units = CountedUnits(Enumerable.Repeat(50, 8).ToArray());

        // trigger = 300, so the compacted prompt may weigh 150. The count carries 60 tokens beyond the units
        // (instructions, tool definitions), which stay in the prompt: 90 remain for history, a single unit.
        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 460);

        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Should().HaveCount(7);
        plan.ThroughSequence.Should().Be(units[6].SourceSequence);
    }

    [Fact]
    public void PlanCompression_does_not_compress_below_the_threshold()
    {
        var units = CountedUnits(100, 100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 299);

        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
        plan.ThroughSequence.Should().Be(0, "a decline replaces no unit");
        plan.SourceTokens.Should().BeNull();
    }

    [Fact]
    public void PlanCompression_compresses_the_prefix_and_retains_the_newest_units()
    {
        var units = CountedUnits(100, 100, 100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // threshold = 300, keepTarget = 150: the newest unit fits, the second would exceed the target.
        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Select(unit => unit.SourceSequence).Should().Equal(units[0].SourceSequence, units[1].SourceSequence, units[2].SourceSequence);
        plan.ThroughSequence.Should().Be(units[2].SourceSequence, "the boundary is the last compressed unit");
        plan.SourceTokens.Should().Be(300);
    }

    [Fact]
    public void PlanCompression_always_retains_the_newest_unit()
    {
        var units = CountedUnits(500, 500);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 1000);

        plan.ShouldCompress.Should().BeTrue();
        plan.PrefixUnits.Select(unit => unit.SourceSequence).Should().Equal(units[0].SourceSequence);
        plan.SourceTokens.Should().Be(500);
    }

    [Fact]
    public void PlanCompression_ignores_units_the_existing_boundary_already_covers()
    {
        var units = CountedUnits(100, 100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, CreateCompaction(units[1].SourceSequence),
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Only the unit after the boundary may be compacted again, and compacting it alone would leave
        // nothing to replay, so the plan declines.
        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
        plan.ThroughSequence.Should().Be(0, "a decline replaces no unit");
    }

    [Fact]
    public void PlanCompression_declines_when_units_were_never_counted()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "first"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "second"));
        var units = DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments());

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300);

        // Without per-unit counts nothing can be sized, so compaction degrades instead of estimating.
        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
    }

    [Fact]
    public void PlanCompression_declines_when_the_policy_is_disabled()
    {
        var units = CountedUnits(100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(Enabled: false), Window(400), countedTokens: 300);

        plan.ShouldCompress.Should().BeFalse();
    }

    [Fact]
    public void PlanCompression_falls_back_to_the_absolute_threshold_without_a_context_window()
    {
        var units = CountedUnits(100, 100, 100, 100);
        var policy = new DirectCompressionPolicy(AbsoluteThresholdTokens: 250);

        var plan = DirectCompactionPlanner.PlanCompression(units, null, policy, Window(null), countedTokens: 250);

        plan.ShouldCompress.Should().BeTrue();
        plan.ThroughSequence.Should().Be(units[2].SourceSequence);
    }

    [Fact]
    public void PlanCompression_starts_at_a_provider_measurement_the_local_count_has_not_reached()
    {
        var units = CountedUnits(100, 100, 100, 100);

        // threshold = 300: the local count (200) would decline, the provider's own measurement would not.
        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 200, observedTokens: 300);

        // The measurement only decides whether to compact. What the summary replaces is still sized by the
        // unit counts: keepTarget = 150 retains the newest unit alone.
        plan.ShouldCompress.Should().BeTrue();
        plan.ThroughSequence.Should().Be(units[2].SourceSequence);
        plan.SourceTokens.Should().Be(300);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(299)]
    public void PlanCompression_declines_when_neither_the_count_nor_the_measurement_reaches_the_threshold(
        int? observedTokens)
    {
        var units = CountedUnits(100, 100, 100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 200, observedTokens);

        plan.ShouldCompress.Should().BeFalse();
    }

    [Fact]
    public void PlanCompression_never_lets_a_lower_measurement_cancel_the_local_trigger()
    {
        var units = CountedUnits(100, 100, 100, 100);

        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 300, observedTokens: 10);

        plan.ShouldCompress.Should().BeTrue("the measurement is a floor for the trigger, never a replacement");
    }

    [Fact]
    public void PlanCompression_declines_when_a_measurement_triggers_but_every_unit_fits_the_retention_target()
    {
        var units = CountedUnits(50, 50);

        // The measurement can run ahead of the local count (content the next request no longer replays). The
        // prefix is sized locally, so nothing is summarized and no summarization call is ever made.
        var plan = DirectCompactionPlanner.PlanCompression(units, null,
            new DirectCompressionPolicy(), Window(400), countedTokens: 100, observedTokens: 300);

        plan.ShouldCompress.Should().BeFalse();
        plan.PrefixUnits.Should().BeEmpty();
    }

    private static IReadOnlyList<DirectPromptHistoryUnit> CountedUnits(params int[] tokensPerUnit)
    {
        var (messages, turns, runs) = CreateHistory(tokensPerUnit
            .Select(tokens => ($"unit-{tokens}", MessageRole.User, ConversationTurnStatus.Succeeded, tokens.ToString()))
            .ToArray());
        return DirectHistoryReplay.BuildUnits(messages, turns, runs, NoAdjustments())
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

    private static void AddTextSegment(List<MessageRecord> messages, int messageIndex, string text)
    {
        var message = messages[messageIndex];
        var segments = (message.Segments ?? []).ToList();
        segments.Add(new MessageSegmentRecord(message.Id, segments.Count, MessageSegmentKind.Text, text, null));
        messages[messageIndex] = message with { Segments = segments };
    }
}
