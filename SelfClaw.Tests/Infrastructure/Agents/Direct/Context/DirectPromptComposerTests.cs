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
    public void Fixed_budget_does_not_materialize_discarded_old_tool_history()
    {
        var small = CreateLongHistory(100);
        var large = CreateLongHistory(1000);
        var composer = new DirectPromptComposer();
        var budget = new DirectPromptBudget(4096, 512);
        Measure(small);
        Measure(large);

        var smallAllocation = Measure(small);
        var largeAllocation = Measure(large);
        _output.WriteLine($"Fixed 4096/512 token budget: 100 old tool messages = {smallAllocation} allocated bytes; 1000 = {largeAllocation} allocated bytes.");
        largeAllocation.Should().BeLessThan(smallAllocation * 2 + 32_000,
            "ten times more discarded tool results must not allocate ten times more SDK history");

        long Measure((List<MessageRecord> Messages, List<ConversationTurnRecord> Turns, List<ToolExecutionRecord> Runs) history)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = composer.BuildMessages(history.Messages, history.Turns, history.Runs, "", [], NoAdjustments(), Interactive(), budget);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            result.Should().HaveCount(3);
            return allocated;
        }

        static (List<MessageRecord>, List<ConversationTurnRecord>, List<ToolExecutionRecord>) CreateLongHistory(int count)
        {
            var (messages, turns, runs) = CreateHistory(Enumerable.Range(0, count)
                .Select(index => ($"assistant-{index}", MessageRole.Assistant, ConversationTurnStatus.Succeeded, ""))
                .Append(("user", MessageRole.User, ConversationTurnStatus.Succeeded, "continue")).ToArray());
            for (var index = 0; index < count; index++)
                AddToolCallSegment(messages, index, runs, CreateRun() with { ResultContent = new string('x', 8192) });
            return (messages, turns, runs);
        }
    }

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
    public void BuildMessages_trims_a_whole_multi_call_turn_including_its_answer()
    {
        var (messages, turns, runs) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "Checking."),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "next"));
        AddToolCallSegment(messages, 0, runs, CreateRun());
        AddToolCallSegment(messages, 0, runs, CreateRun() with { CorrelationId = "call-2" });
        messages[0] = messages[0] with
        {
            Segments = [.. messages[0].Segments ?? [], new(messages[0].Id, 3, MessageSegmentKind.Text, "done", null)]
        };

        var replay = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 60, MaxOutputTokens: 0));

        replay.Should().ContainSingle().Which.Text.Should().Be("next");
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
    public void BuildMessages_sends_the_full_history_without_a_context_window()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "first"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "second"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "third"));

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive(), default);

        result.Select(message => message.Text).Should().Equal("first", "second", "third");
    }

    [Fact]
    public void BuildMessages_trims_old_history_within_the_context_budget_and_keeps_the_latest_turn()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "oldest"),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, new string('b', 3000)),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        // 3000 ASCII bytes estimate roughly 1000 tokens; the budget only fits the latest turn.
        var budget = new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 50);

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive(), budget);

        result.Select(message => message.Text).Should().Equal("latest");
    }

    [Fact]
    public void BuildMessages_keeps_tool_call_result_pairs_together_when_trimming()
    {
        var composer = new DirectPromptComposer();
        var (messages, turns, toolRuns) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "working"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Succeeded, "done"));
        AddToolCallSegment(messages, 0, toolRuns, CreateRun());
        AddToolCallSegment(messages, 1, toolRuns, CreateRun() with
        {
            CorrelationId = "call-2",
            ResultContent = "second result"
        });
        var budget = new DirectPromptBudget(ContextWindowTokens: 60, MaxOutputTokens: 0);

        var result = composer.BuildMessages(messages, turns, toolRuns, "", [], NoAdjustments(), Interactive(), budget);

        var calls = result.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
        var results = result.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
        // The older pair was trimmed as one unit, and the surviving pair stays matched.
        results.Should().HaveCount(calls.Count);
        results.Should().ContainSingle().Which.CallId.Should().Be("call-2");
    }

    [Theory]
    [InlineData("description")]
    [InlineData("schema")]
    [InlineData("metadata")]
    public void BuildMessages_rejects_tool_definitions_that_exhaust_the_context_window(string largeField)
    {
        var largeText = new string('x', 6000);
        AITool tool = largeField == "metadata"
            ? AIFunctionFactory.Create((Func<string>)(() => "ok"), new AIFunctionFactoryOptions
            {
                Name = "lookup",
                AdditionalProperties = new Dictionary<string, object?> { ["examples"] = largeText }
            })
            : AIFunctionFactory.CreateDeclaration(
                "lookup",
                largeField == "description" ? largeText : "Lookup a value.",
                JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = largeField == "schema" ? largeText : "Query." }
                    }
                }));

        var action = () => new DirectPromptComposer().BuildMessages([], [], [], "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 50), [tool]);

        action.Should().Throw<InvalidDataException>().WithMessage("*tool definitions*model context window of 400 tokens*");
    }

    [Fact]
    public void BuildMessages_reserves_tool_definitions_before_selecting_history()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, new string('o', 900)),
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));
        var tool = AIFunctionFactory.CreateDeclaration("lookup", new string('d', 180),
            JsonSerializer.SerializeToElement(new { type = "object" }));
        var composer = new DirectPromptComposer();
        var budget = new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 50);

        var withoutTools = composer.BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(), budget);
        var withTools = composer.BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(), budget, [tool]);

        withoutTools.Should().HaveCount(2);
        withTools.Should().ContainSingle().Which.Text.Should().Be("latest");
    }

    [Theory]
    [InlineData('a')]
    [InlineData('\u4e2d')]
    public void BuildMessages_rejects_the_latest_user_message_when_it_cannot_fit(char character)
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, new string(character, 24000)));

        var action = () => new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 50));

        action.Should().Throw<InvalidDataException>().WithMessage("*latest conversation message*model context window*");
    }

    [Theory]
    [InlineData(1200, 50)]
    [InlineData(0, 401)]
    [InlineData(0, null)]
    public void BuildMessages_rejects_mandatory_input_and_output_that_exceed_the_window(
        int instructionLength,
        int? maxOutputTokens)
    {
        var action = () => new DirectPromptComposer().BuildMessages([], [], [], new string('s', instructionLength), [],
            NoAdjustments(), Interactive(), new DirectPromptBudget(ContextWindowTokens: 400, maxOutputTokens));

        action.Should().Throw<InvalidDataException>().WithMessage("*output reserve*model context window of 400 tokens*");
    }

    [Fact]
    public void BuildMessages_rejects_latest_input_when_the_output_reserve_consumes_the_window()
    {
        var (messages, turns, runs) = CreateHistory(("user", MessageRole.User, ConversationTurnStatus.Succeeded, "latest"));

        var action = () => new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 400));

        action.Should().Throw<InvalidDataException>().WithMessage("*latest conversation message*only 0 tokens remain*");
    }

    [Fact]
    public void BuildMessages_reserves_the_truncated_answer_continuation_before_selecting_history()
    {
        var (messages, turns, runs) = CreateHistory(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, new string('o', 600)),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial answer"));

        var replay = new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 300, MaxOutputTokens: 50));

        replay.Select(message => message.Text).Should().Equal("partial answer", DirectPromptComposer.ContinuationPrompt);
    }

    [Fact]
    public void BuildMessages_rejects_a_continuation_that_cannot_fit_with_its_latest_answer()
    {
        var (messages, turns, runs) = CreateHistory(
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, new string('a', 180)));

        var action = () => new DirectPromptComposer().BuildMessages(messages, turns, runs, "", [], NoAdjustments(), Interactive(),
            new DirectPromptBudget(ContextWindowTokens: 100, MaxOutputTokens: 0));

        action.Should().Throw<InvalidDataException>().WithMessage("*latest conversation message*model context window*");
    }

    [Fact]
    public void BuildMessages_rejects_a_completion_batch_that_exhausts_the_context_window()
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new SubagentCompletionBatch(
        [
            new SubagentCompletionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new SubagentIdentity("worker", "Worker"), "Check the workspace.", SubagentTaskStatus.Succeeded, 1,
                new SubagentCompletionResult(new string('r', 6000), false, null, null), new TurnUsage(InputTokens: 10, OutputTokens: 10),
                new SubagentTiming(now, now, now, 0))
        ]);
        var context = new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, batch);

        var action = () => new DirectPromptComposer().BuildMessages([], [], [], "", [], NoAdjustments(), context,
            new DirectPromptBudget(ContextWindowTokens: 400, MaxOutputTokens: 50));

        action.Should().Throw<InvalidDataException>().WithMessage("*continuation input*model context window of 400 tokens*");
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
