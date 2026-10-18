using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders.Models;
using Xunit;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// I3/I4/I5/I6/I7: the loop compacts between provider round trips, before steer intake, and only ever changes
/// this turn's request context. Every test here takes "what the next request actually contained" as evidence.
/// </summary>
public sealed class DirectInTurnCompactionTests
{
    [Fact]
    public async Task Growth_past_the_threshold_compacts_once_and_the_next_request_replays_the_summary()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().ContainSingle("exactly one in-turn compaction is needed to get back under the threshold");
        var started = events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle().Subject;
        started.ThresholdTokens.Should().Be(200);
        var completed = events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle().Subject;
        completed.CoveredUnitCount.Should().Be(2, "the two oldest units fall outside the retention target");
        completed.SourceTokens.Should().Be(64);
        completed.Model.Should().Be(factory.Profile.Model);
        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty("an in-turn summary is never persisted");

        // Started -> Completed -> Requesting keeps the activity text overwritten by the request it precedes.
        events.IndexOf(started).Should().BeLessThan(events.IndexOf(completed));
        events.IndexOf(completed).Should()
            .BeLessThan(IndexOfStatus(events, AgentRunStatus.Requesting, from: events.IndexOf(completed)));
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();

        client.Requests.Should().HaveCount(2);
        var replayed = client.Requests[1];
        replayed[0].Role.Should().Be(ChatRole.System);
        replayed[0].Text.Should().Contain("selfclaw-context-summary").And.Contain("early history summarized");
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Should().NotContain(message => message.Text == "prior answer");
        replayed.Should().Contain(message => message.Text == "user prompt", "the retained tail stays verbatim");
        replayed.Should().Contain(message => message.Text == "AA");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Growth_below_the_threshold_sends_the_full_history_without_compaction()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(100_000));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().BeEmpty();
        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();
        client.Requests.Should().HaveCount(3);
        client.Requests.Should().OnlyContain(request => request.Any(message => message.Text == "prior prompt"));
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_units_within_half_the_threshold()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(300));
        // Threshold 300 retains at most 150 tokens: "BB" (42) + "AA" (42) + "user prompt" (31) + "x4" (22)
        // = 137, and the next unit would cross it, so exactly the three oldest units are covered.
        var history = new (MessageRole Role, string Text)[]
        {
            (MessageRole.User, "x1"),
            (MessageRole.User, "x2"),
            (MessageRole.User, "x3"),
            (MessageRole.User, "x4")
        };

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id, history)));

        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(3);
        var replayed = client.Requests[2];
        replayed.Should().NotContain(message => message.Text == "x1");
        replayed.Should().NotContain(message => message.Text == "x2");
        replayed.Should().NotContain(message => message.Text == "x3");
        replayed.Should().Contain(message => message.Text == "x4");
        replayed.Should().Contain(message => message.Text == "user prompt");
        replayed.Should().Contain(message => message.Text == "BB");
    }

    [Fact]
    public async Task Retention_always_keeps_the_newest_unit_even_when_it_alone_exceeds_the_target()
    {
        var longAnswer = new string('A', 150);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1, answerLength: 150));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        // The round trip alone is 190 tokens, so only it survives even though the target is 100.
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(3);
        var replayed = client.Requests[1];
        replayed.Should().Contain(message => message.Text == longAnswer);
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Should().NotContain(message => message.Text == "user prompt");
    }

    [Fact]
    public async Task A_tool_call_and_its_result_are_removed_together()
    {
        // 100-character answers make each round trip cost 140 tokens, so the retention target keeps only the
        // newest two and the oldest round trip falls into the prefix.
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 3, answerLength: 100));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(600));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(4, "the oldest round trip is covered with the replayed history");
        client.Requests.Should().HaveCount(4);
        var replayed = client.Requests[3];
        replayed.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId).Should().Equal("call-2", "call-3");
        AssertNoOrphanedCalls(replayed);
    }

    [Fact]
    public async Task The_turn_stops_compacting_after_three_attempts_and_keeps_sending()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 5));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().HaveCount(DirectConversationLoop.MaximumInTurnCompactionsPerTurn);
        events.OfType<ContextCompactionStartedEvent>().Should().HaveCount(3);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().HaveCount(3);
        client.Requests.Should().HaveCount(6, "after the cap the turn keeps issuing provider requests");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);

        // Chained compactions hand the bare summary back and the compressor wraps it once, so the section is
        // replaced instead of nested.
        client.SummaryCalls[1].Should().Contain(message => message.Role == ChatRole.User &&
            message.Text == $"<selfclaw-context-summary version=\"1\">\n{client.SummaryText}\n</selfclaw-context-summary>");
        var lastSystemText = client.Requests[^1][0].Text ?? string.Empty;
        lastSystemText.Split("<selfclaw-context-summary").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_summary_keeps_the_full_history_and_the_turn_continues()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1)) { SummaryText = null };
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle();
        var failed = events.OfType<ContextCompactionFailedEvent>().Should().ContainSingle().Which;
        failed.Reason.Should().Be(DirectAgentChatRuntime.CompactionFailedReason);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().BeEmpty();
        client.Requests.Should().HaveCount(2);
        client.Requests[1].Should().Contain(message => message.Text == "prior prompt");
        client.Requests[1].Should().Contain(message => message.Text == "prior answer");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Unavailable_counting_never_compacts_in_turn()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, counter: UnavailableTokenCounter.Instance);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().BeEmpty();
        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().BeEmpty();
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();
        client.Requests.Should().HaveCount(3);
        client.Requests.Should().OnlyContain(request => request.Any(message => message.Text == "prior prompt"));
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Disabled_compaction_leaves_the_turn_untouched_and_never_counts()
    {
        var counter = new CountingTokenCounter();
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: new DirectCompressionPolicy(Enabled: false), counter: counter);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        counter.Calls.Should().Be(0, "with no declared window and compaction off, neither setup nor the checkpoint counts");
        client.SummaryCalls.Should().BeEmpty();
        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().BeEmpty();
        client.Requests.Should().HaveCount(3);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_continuation_keeps_its_prompt_and_completion_batch_after_the_summary()
    {
        var longAnswer = new string('A', 400);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1, answerLength: 400));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(700));
        var request = CreateRequest(
            factory.Profile.Id,
            history: [(MessageRole.User, "prior prompt"), (MessageRole.Assistant, "prior answer")],
            priorTurnStatus: ConversationTurnStatus.Truncated,
            execution: new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CompletionBatch()),
            checkpoint: new NoOpCheckpoint(),
            currentTurnInputs: []);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2);
        var replayed = client.Requests[1].ToList();
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Should().Contain(message => message.Text == longAnswer);
        var continuationIndex = replayed.FindIndex(message => message.Text == DirectPromptComposer.ContinuationPrompt);
        var batchIndex = replayed.FindIndex(message =>
            message.Text?.Contains("<selfclaw-subagent-results", StringComparison.Ordinal) == true);
        continuationIndex.Should().BeGreaterThan(0, "the continuation prompt is protected, not compacted");
        batchIndex.Should().BeGreaterThan(continuationIndex, "the protected tail keeps its own order");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_consumed_steer_survives_compaction_in_place()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);
        var request = CreateRequest(factory.Profile.Id);
        var session = new ScriptedInputSession(request.ConversationId, request.TurnId, [["steer text"]]);
        request = request with { InputSession = session };

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        session.Consumed.Should().Be(1);
        session.HoldReason.Should().BeNull();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2, "a protected steer is never counted as a unit");
        var replayed = client.Requests[1].ToList();
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Select(message => message.Text)
            .Should().ContainInOrder("user prompt", "steer text", "AA");
    }

    [Fact]
    public async Task Compaction_runs_before_steer_intake_so_a_batch_that_did_not_fit_is_accepted()
    {
        var steer = new string('s', 70);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2, answerLength: 300));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 700)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = CreateRuntime(factory, policy: new DirectCompressionPolicy(Enabled: true, ThresholdRatio: 0.9d));
        var request = CreateRequest(factory.Profile.Id);
        var session = new ScriptedInputSession(request.ConversationId, request.TurnId, [null, null, [steer]]);
        request = request with { InputSession = session };

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        session.HoldReason.Should().BeNull("the checkpoint runs before intake, so the freed room admits the batch");
        session.Consumed.Should().Be(1);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle();
        client.Requests[2].Should().Contain(message => message.Text == steer, "the accepted steer reaches the compacted request");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_batch_that_still_does_not_fit_is_held_after_compaction()
    {
        var steer = new string('s', 300);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2, answerLength: 300));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 700)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = CreateRuntime(factory, policy: new DirectCompressionPolicy(Enabled: true, ThresholdRatio: 0.9d));
        var request = CreateRequest(factory.Profile.Id);
        var session = new ScriptedInputSession(request.ConversationId, request.TurnId, [null, null, [steer]]);
        request = request with { InputSession = session };

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        session.HoldReason.Should().Be(ConversationInputReason.ContextBudgetExceeded);
        session.Consumed.Should().Be(0);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle("compaction is attempted before the batch is refused");
        client.Requests.Should().HaveCount(2);
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Which;
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("ContextBudgetExceeded");
    }

    [Fact]
    public async Task A_prompt_still_over_the_window_after_compaction_fails_readably()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 3));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 300)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = CreateRuntime(factory, policy: new DirectCompressionPolicy(Enabled: true, ThresholdRatio: 0.9d));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle();
        client.Requests.Should().HaveCount(2, "the known over-window prompt is never sent");
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Which;
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("300").And.Contain("larger context window");
        terminal.ErrorMessage.Should().NotContain("prior prompt", "the error must not leak prompt content");
    }

    private static DirectAgentChatRuntime CreateRuntime(
        DirectAgentChatRuntimeTests.FakeChatClientFactory factory,
        DirectCompressionPolicy? policy = null,
        IDirectTokenCounter? counter = null)
        => DirectAgentChatRuntimeTests.CreateRuntime(
            factory,
            new FixedResolver(Lease()),
            new FixedPolicySource(policy ?? Policy(200)),
            counter ?? new CountingTokenCounter());

    private static DirectAgentChatRuntimeTests.FakeChatClientFactory CreateFactory(ScriptedClient client)
        => new(client) { Options = new ChatOptions { MaxOutputTokens = 50 } };

    private static DirectCompressionPolicy Policy(int absoluteThresholdTokens)
        => new(Enabled: true, AbsoluteThresholdTokens: absoluteThresholdTokens);

    private static DirectChatTurnRequest CreateRequest(
        Guid modelProfileId,
        IReadOnlyList<(MessageRole Role, string Text)>? history = null,
        ConversationTurnStatus priorTurnStatus = ConversationTurnStatus.Succeeded,
        DirectTurnExecutionContext? execution = null,
        IToolExecutionCheckpoint? checkpoint = null,
        IReadOnlyList<string>? currentTurnInputs = null,
        IDirectTurnInputSession? inputSession = null)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var priorTurnId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var turns = new[]
        {
            new ConversationTurnRecord(priorTurnId, conversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, priorTurnStatus, now.AddMinutes(-1), now),
            new ConversationTurnRecord(turnId, conversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now)
        };
        var messages = new List<MessageRecord>();
        long sequence = 0;
        foreach (var (role, text) in history ??
                 [(MessageRole.User, "prior prompt"), (MessageRole.Assistant, "prior answer")])
        {
            messages.Add(CreateMessage(conversationId, priorTurnId, ++sequence, role, text, now));
        }

        foreach (var text in currentTurnInputs ?? ["user prompt"])
        {
            messages.Add(CreateMessage(conversationId, turnId, ++sequence, MessageRole.User, text, now));
        }

        return new DirectChatTurnRequest(turnId, conversationId, null,
            new AgentRuntimeDefinition("direct-test", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], "Follow project instructions."),
            messages, turns, modelProfileId, ToolPermissionMode.FullAccess, null,
            execution ?? new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null),
            ToolExecutionCheckpoint: checkpoint,
            InputSession: inputSession);
    }

    private static MessageRecord CreateMessage(Guid conversationId, Guid turnId, long sequence,
        MessageRole role, string text, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        return new MessageRecord(id, conversationId, turnId, sequence, role, text, MessageStatus.Sealed, now, now,
            Segments: role == MessageRole.Assistant
                ? [new MessageSegmentRecord(id, 0, MessageSegmentKind.Text, text, null)]
                : null);
    }

    private static DirectTurnCapabilityLease Lease()
        => new([], [new DirectToolBinding(Probe(), new DirectToolDescriptor(ToolCallKind.Other))],
            new Dictionary<Guid, string>(), []);

    private static AIFunction Probe()
        => AIFunctionFactory.Create(
            () => new DirectToolResult(ToolCallStatus.Completed, "ok", JsonSerializer.SerializeToElement("ok"), "ok"),
            new AIFunctionFactoryOptions
            {
                Name = "probe",
                Description = "Synthetic probe tool.",
                ExcludeResultSchema = true,
                MarshalResult = (value, _, _) => ValueTask.FromResult(value)
            });

    /// <summary>
    /// One tool round trip per script, then a final answer. Each round's unit costs a fixed
    /// <see cref="CountingTokenCounter.MessageCost"/> per message plus its answer text, so a test can size the
    /// growth by the answer length.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> ToolRoundsThenFinal(int rounds, int answerLength = 2)
    {
        var scripts = new List<IReadOnlyList<ChatResponseUpdate>>();
        for (var index = 1; index <= rounds; index++)
        {
            scripts.Add([Finish($"tool-{index}", ChatFinishReason.ToolCalls,
                new TextContent(new string((char)('A' + index - 1), answerLength)),
                new FunctionCallContent($"call-{index}", "probe", new Dictionary<string, object?>()))]);
        }

        scripts.Add([Finish("final", ChatFinishReason.Stop, new TextContent("done"))]);
        return scripts;
    }

    private static ChatResponseUpdate Finish(string messageId, ChatFinishReason finishReason, params AIContent[] contents)
        => new(ChatRole.Assistant, contents) { MessageId = messageId, FinishReason = finishReason };

    private static SubagentCompletionBatch CompletionBatch()
    {
        var now = DateTimeOffset.UtcNow;
        return new SubagentCompletionBatch(
        [
            new SubagentCompletionEnvelope(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                new SubagentIdentity("worker", "Worker"), "Check the workspace.", SubagentTaskStatus.Succeeded, 1,
                new SubagentCompletionResult("done", false, null, null),
                new TurnUsage(InputTokens: 10, OutputTokens: 10), new SubagentTiming(now, now, now, 0))
        ]);
    }

    private static void AssertNoOrphanedCalls(IReadOnlyList<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in messages.SelectMany(message => message.Contents))
        {
            switch (content)
            {
                case FunctionCallContent call:
                    pending.Add(call.CallId).Should().BeTrue($"call {call.CallId} must not repeat");
                    break;
                case FunctionResultContent result:
                    pending.Remove(result.CallId).Should().BeTrue($"result {result.CallId} must follow its call");
                    break;
            }
        }

        pending.Should().BeEmpty("every replayed call keeps its result");
    }

    private static int IndexOfStatus(IReadOnlyList<AgentStreamEvent> events, AgentRunStatus status, int from = 0)
    {
        for (var index = from; index < events.Count; index++)
        {
            if (events[index] is RunStatusEvent runStatus && runStatus.Status == status)
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class FixedPolicySource(DirectCompressionPolicy policy) : IDirectCompressionPolicySource
    {
        public Task<DirectCompressionPolicy> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(policy);
    }

    private sealed class FixedResolver(DirectTurnCapabilityLease lease) : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class NoOpCheckpoint : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic token arithmetic: every message costs <see cref="MessageCost"/> plus the length of its text
    /// content. Compaction boundaries therefore depend only on the texts a test chose.
    /// </summary>
    private sealed class CountingTokenCounter : IDirectTokenCounter
    {
        internal const int MessageCost = 20;

        internal int Calls { get; private set; }

        public bool IsAvailable(AiModelProfile? model) => true;

        public int? CountText(string? text, AiModelProfile? model)
        {
            Calls++;
            return text?.Length ?? 0;
        }

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model)
        {
            Calls++;
            return messages.Sum(message =>
                MessageCost + message.Contents.OfType<TextContent>().Sum(text => text.Text?.Length ?? 0));
        }

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model)
        {
            Calls++;
            return 0;
        }
    }

    private sealed class UnavailableTokenCounter : IDirectTokenCounter
    {
        internal static UnavailableTokenCounter Instance { get; } = new();

        public bool IsAvailable(AiModelProfile? model) => false;

        public int? CountText(string? text, AiModelProfile? model) => null;

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model) => null;

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model) => null;
    }

    /// <summary>
    /// Serves scripted boundary reads: a null entry means nothing is pending, a list means a claimable batch.
    /// The plan lets a test decide on which loop iteration the steer arrives.
    /// </summary>
    private sealed class ScriptedInputSession : IDirectTurnInputSession
    {
        private readonly Guid _conversationId;
        private readonly Guid _turnId;
        private readonly Queue<IReadOnlyList<string>?> _reads;
        private long _sequence;

        internal ScriptedInputSession(Guid conversationId, Guid turnId, IReadOnlyList<IReadOnlyList<string>?> reads)
        {
            _conversationId = conversationId;
            _turnId = turnId;
            _reads = new Queue<IReadOnlyList<string>?>(reads);
        }

        internal int Consumed { get; private set; }

        internal string? HoldReason { get; private set; }

        public Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Claim());

        public Task<ConversationInputBatch?> ReadFinalBoundaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationInputBatch?>(null);

        public Task<ConversationInputConsumption> WaitForCommitAsync(ConversationInputBatch batch,
            CancellationToken cancellationToken = default)
        {
            Consumed++;
            var now = DateTimeOffset.UtcNow;
            var messages = batch.Inputs
                .Select(input => new MessageRecord(input.MessageId, _conversationId, _turnId, 1000 + ++_sequence,
                    MessageRole.User, input.Prompt, MessageStatus.Sealed, now, now))
                .ToArray();
            var turn = new ConversationTurnRecord(_turnId, _conversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now, now);
            return Task.FromResult(new ConversationInputConsumption(batch.ClaimId, turn, messages));
        }

        public Task HoldUnconsumedAsync(string reasonCode, CancellationToken cancellationToken = default)
        {
            HoldReason = reasonCode;
            return Task.CompletedTask;
        }

        private ConversationInputBatch? Claim()
        {
            if (_reads.Count == 0 || _reads.Dequeue() is not { } prompts)
            {
                return null;
            }

            var items = prompts
                .Select((prompt, index) =>
                    new ConversationInputItem(Guid.NewGuid(), index + 1, 1, Guid.NewGuid(), prompt))
                .ToArray();
            return new ConversationInputBatch(_conversationId, _turnId, Guid.NewGuid(), Guid.NewGuid(), items);
        }
    }

    /// <summary>
    /// Serves the turn with scripted streaming updates and the summarization call with one fixed summary,
    /// because both calls go through the same chat client lease.
    /// </summary>
    private sealed class ScriptedClient : IChatClient
    {
        private readonly IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> _scripts;

        internal ScriptedClient(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> scripts) => _scripts = scripts;

        /// <summary>Null makes the summarization call fail the way an unsupported client would.</summary>
        internal string? SummaryText { get; init; } = "early history summarized";

        internal List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        internal List<IReadOnlyList<ChatMessage>> SummaryCalls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("the compressor summarizes over the streaming path");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var captured = messages.ToList();
            if (captured.Count > 0 && captured[0].Role == ChatRole.System && captured[0].Text == CompressionInstruction.Text)
            {
                SummaryCalls.Add(captured);
                if (SummaryText is null)
                {
                    throw new NotSupportedException();
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(SummaryText)]);
                yield break;
            }

            var index = Math.Min(Requests.Count, _scripts.Count - 1);
            Requests.Add(captured);
            foreach (var update in _scripts[index])
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
