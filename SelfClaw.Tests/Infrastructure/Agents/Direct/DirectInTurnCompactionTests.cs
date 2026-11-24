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
using SelfClaw.Tests.TestDoubles;
using Xunit;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct;

/// <summary>
/// I3/I4/I5/I6/I7: the loop compacts between provider round trips, before steer intake, and only ever changes
/// this turn's request context. Every test here takes "what the next request actually contained" as evidence.
/// </summary>
public sealed class DirectInTurnCompactionTests
{
    // 100-character history messages cost 120 tokens each, so the replayed history is large enough to be worth
    // summarizing while the current prompt and the round trips after it still fit the retained tail.
    private static readonly string LongPrompt = new('p', 100);
    private static readonly string LongAnswer = new('a', 100);

    [Fact]
    public async Task Growth_past_the_threshold_compacts_once_and_the_next_request_replays_the_summary()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1));
        var factory = CreateFactory(client);
        // The round trip brings the prompt to 361 (instructions 48, history 240, prompt 31, round trip 42),
        // past the trigger of 300. Half of it (150) less the instructions leaves 102 for history: the round
        // trip and the prompt (73) stay, either history message would cross it.
        var runtime = CreateRuntime(factory, policy: Policy(300));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id, LongHistory())));

        client.SummaryCalls.Should().ContainSingle("exactly one in-turn compaction is needed to get back under the threshold");
        var started = events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle().Subject;
        started.ThresholdTokens.Should().Be(300);
        var completed = events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle().Subject;
        completed.CoveredUnitCount.Should().Be(2, "the two oldest units fall outside the retention target");
        completed.SourceTokens.Should().Be(240);
        completed.Model.Should().Be(factory.Profile.Model);
        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty("the turn start had nothing to compact");
        // The record is what the next turn replays against: both covered units are stored messages, so the
        // boundary is the newer one's sequence and nothing inside this turn's own message needs an anchor.
        completed.AnchorCallId.Should().BeNull();
        completed.Compaction.Should().BeEquivalentTo(
            new { ThroughSequence = 2L, Summary = "early history summarized", SourceMessageCount = 2, SourceTokens = 240 });
        completed.Compaction!.ThroughToolRunId.Should().BeNull("only the recorder knows the stored tool run");
        // The one place a local estimate is shown: the context really shrank and no provider has measured
        // the compacted prompt yet, so keeping the pre-compaction size would be worse than an approximation.
        var estimate = events.OfType<UsageProgressEvent>().Should().ContainSingle().Which;
        estimate.IsEstimated.Should().BeTrue();
        estimate.Usage.LastRequestInputTokens.Should().BePositive();

        // Started -> Completed -> Requesting keeps the activity text overwritten by the request it precedes.
        events.IndexOf(started).Should().BeLessThan(events.IndexOf(completed));
        events.IndexOf(completed).Should()
            .BeLessThan(IndexOfStatus(events, AgentRunStatus.Requesting, from: events.IndexOf(completed)));
        events.OfType<ContextCompactionFailedEvent>().Should().BeEmpty();

        client.Requests.Should().HaveCount(2);
        var replayed = client.Requests[1];
        replayed[0].Role.Should().Be(ChatRole.System);
        replayed[0].Text.Should().Contain("selfclaw-context-summary").And.Contain("early history summarized");
        replayed.Should().NotContain(message => message.Text == LongPrompt);
        replayed.Should().NotContain(message => message.Text == LongAnswer);
        replayed.Should().Contain(message => message.Text == "user prompt", "the retained tail stays verbatim");
        replayed.Should().Contain(message => message.Text == "AA");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    /// <summary>
    /// The local count cannot see everything a provider bills (reasoning signatures, large images, a foreign
    /// tokenizer). The previous request's measured input is a prefix of the prompt being checked, so it may
    /// start the compaction the local count alone would have put off until the window overflowed.
    /// </summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(260, 1)]
    public async Task The_previous_requests_measured_input_can_start_compaction_the_local_count_missed(
        int? measuredInputTokens, int expectedCompactions)
    {
        IReadOnlyList<ChatResponseUpdate> toolRound =
        [
            Finish("tool-1", ChatFinishReason.ToolCalls, new TextContent("AA"),
                new FunctionCallContent("call-1", "probe", new Dictionary<string, object?>()))
        ];
        if (measuredInputTokens is int measured)
        {
            toolRound = [.. toolRound, new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = measured, OutputTokenCount = 5 })])];
        }

        var client = new ScriptedClient([toolRound, [Finish("final", ChatFinishReason.Stop, new TextContent("done"))]]);
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(260));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().HaveCount(expectedCompactions);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().HaveCount(expectedCompactions);
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
    public async Task Retention_brings_the_whole_prompt_back_to_half_the_trigger()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 4));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(330));
        // The fourth round trip brings the prompt to 335, past the trigger of 330. Half of it (165) is what the
        // compacted prompt may weigh, and the 48 tokens of instructions are part of that: 117 remain for
        // history. "DD" (42) + "CC" (42) = 84 fit, "BB" would cross it, so the seven oldest units are covered.
        var history = new (MessageRole Role, string Text)[]
        {
            (MessageRole.User, "x1"),
            (MessageRole.User, "x2"),
            (MessageRole.User, "x3"),
            (MessageRole.User, "x4")
        };

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id, history)));

        var completed = events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle().Subject;
        completed.CoveredUnitCount.Should().Be(7);
        // Four history messages, the prompt and two round trips of this turn's own assistant message: the stored
        // boundary is the prompt's sequence, and the anchor says how far into the assistant message it reaches.
        completed.AnchorCallId.Should().Be("call-2");
        completed.Compaction.Should().BeEquivalentTo(new { ThroughSequence = 5L, SourceMessageCount = 6 });
        var replayed = client.Requests[4];
        replayed.Should().NotContain(message => message.Text == "x4");
        replayed.Should().NotContain(message => message.Text == "user prompt");
        replayed.Should().NotContain(message => message.Text == "AA");
        replayed.Should().NotContain(message => message.Text == "BB");
        replayed.Should().Contain(message => message.Text == "CC");
        replayed.Should().Contain(message => message.Text == "DD");
    }

    [Fact]
    public async Task Retention_always_keeps_the_newest_unit_even_when_it_alone_exceeds_the_target()
    {
        var longAnswer = new string('A', 150);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1, answerLength: 150));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        // The round trip alone is 190 tokens, so only it survives even though 52 are left for history.
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
        // 100-character answers make each round trip cost 140 tokens. The fourth brings the prompt to 703, past
        // the trigger of 660; the 282 tokens left for history keep the newest two round trips, so the two
        // oldest fall into the prefix with the replayed history.
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 4, answerLength: 100));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(660));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(5, "the two oldest round trips are covered with the replayed history");
        client.Requests.Should().HaveCount(5);
        var replayed = client.Requests[4];
        replayed.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId).Should().Equal("call-3", "call-4");
        AssertNoOrphanedCalls(replayed);
    }

    [Fact]
    public async Task The_turn_stops_compacting_after_its_allowance_and_keeps_sending()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 14));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory);

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        client.SummaryCalls.Should().HaveCount(DirectCompactionStep.MaximumInTurnCompactionsPerTurn);
        events.OfType<ContextCompactionStartedEvent>().Should()
            .HaveCount(DirectCompactionStep.MaximumInTurnCompactionsPerTurn);
        events.OfType<InTurnContextCompactionCompletedEvent>().Should()
            .HaveCount(DirectCompactionStep.MaximumInTurnCompactionsPerTurn);
        client.Requests.Should().HaveCount(15, "after the cap the turn keeps issuing provider requests");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);

        // Chained compactions hand the bare summary back and the compressor wraps it once, so the section is
        // replaced instead of nested.
        client.SummaryCalls[1].Should().Contain(message => message.Role == ChatRole.User &&
            message.Text == $"<selfclaw-context-summary version=\"1\">\n{client.SummaryText}\n</selfclaw-context-summary>");
        var lastSystemText = client.Requests[^1][0].Text ?? string.Empty;
        lastSystemText.Split("<selfclaw-context-summary").Should().HaveCount(2);

        // However often the turn compacts, it rewrites one record, so the transcript shows one divider.
        var records = events.OfType<InTurnContextCompactionCompletedEvent>()
            .Select(completed => completed.Compaction).OfType<ConversationCompactionRecord>().ToArray();
        records.Should().NotBeEmpty();
        records.Select(record => record.Id).Distinct().Should().ContainSingle();
        records.Select(record => record.SourceMessageCount).Should().BeInAscendingOrder(
            "the coverage of one turn accumulates");
    }

    [Fact]
    public async Task The_first_in_turn_compaction_folds_the_summary_the_turn_started_with_into_its_own()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1, answerLength: 300));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(600));
        var request = CreateRequest(factory.Profile.Id, LongHistory());
        // The stored boundary already covers the first history message.
        var stored = new ConversationCompactionRecord(Guid.NewGuid(), request.ConversationId, Guid.NewGuid(), 1,
            "stored summary", "test-model", 1, 120, 14, DateTimeOffset.UtcNow);

        var events = await CollectAsync(runtime.StreamTurnAsync(request with { Compression = stored }));

        client.Requests[0][0].Text.Should().Contain("stored summary");
        var completed = events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle().Subject;
        client.SummaryCalls.Should().ContainSingle().Which.Should().Contain(message =>
            message.Role == ChatRole.User &&
            message.Text == "<selfclaw-context-summary version=\"1\">\nstored summary\n</selfclaw-context-summary>",
            "a stored summary has to stand on its own, so the new one is written on top of the old one");
        var system = client.Requests[1][0].Text ?? string.Empty;
        system.Should().StartWith("Follow project instructions.\n\n", "the instructions keep their bytes");
        system.Should().Contain("early history summarized").And.NotContain("stored summary");
        system.Split("<selfclaw-context-summary").Should().HaveCount(2, "a request carries one summary");
        completed.Compaction.Should().NotBeNull();
        completed.Compaction!.Id.Should().NotBe(stored.Id, "the stored record of an earlier turn is never rewritten");
        completed.Compaction.ThroughSequence.Should().BeGreaterThan(stored.ThroughSequence);
    }

    [Fact]
    public async Task The_next_turn_replays_only_the_summary_and_what_followed_the_anchor()
    {
        var first = new ScriptedClient(ToolRoundsThenFinal(rounds: 4, answerLength: 100));
        var factory = CreateFactory(first);
        var request = CreateRequest(factory.Profile.Id);
        var events = await CollectAsync(
            CreateRuntime(factory, policy: Policy(660)).StreamTurnAsync(request));
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.AnchorCallId.Should().Be("call-2");
        var followUp = CreateFollowUpRequest(request, events, keepAnchor: true);

        var second = new ScriptedClient([[Finish("final", ChatFinishReason.Stop, new TextContent("done"))]]);
        var secondFactory = CreateFactory(second);
        var secondEvents = await CollectAsync(CreateRuntime(secondFactory, policy: Policy(660))
            .StreamTurnAsync(followUp with { ModelProfileId = secondFactory.Profile.Id }));

        second.SummaryCalls.Should().BeEmpty("the turn starts from the context the previous turn ended with");
        secondEvents.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        var replayed = second.Requests.Should().ContainSingle().Subject;
        replayed[0].Text.Should().Contain("early history summarized");
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Should().NotContain(message => message.Text == "user prompt");
        replayed.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId).Should().Equal(["call-3", "call-4"], "the first two round trips are summarized");
        replayed.Select(message => message.Text).Should().ContainInOrder("done", "next prompt");
        AssertNoOrphanedCalls(replayed);
        secondEvents.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Without_its_anchor_the_stored_summary_makes_the_next_turn_replay_the_whole_message_and_compact_again()
    {
        var first = new ScriptedClient(ToolRoundsThenFinal(rounds: 4, answerLength: 100));
        var factory = CreateFactory(first);
        var request = CreateRequest(factory.Profile.Id);
        var events = await CollectAsync(
            CreateRuntime(factory, policy: Policy(660)).StreamTurnAsync(request));
        // What an unresolvable anchor degrades to: the boundary still holds, the assistant message replays whole.
        var followUp = CreateFollowUpRequest(request, events, keepAnchor: false);

        var second = new ScriptedClient([[Finish("final", ChatFinishReason.Stop, new TextContent("done"))]]);
        var secondFactory = CreateFactory(second);
        var secondEvents = await CollectAsync(CreateRuntime(secondFactory, policy: Policy(660))
            .StreamTurnAsync(followUp with { ModelProfileId = secondFactory.Profile.Id }));

        second.SummaryCalls.Should().ContainSingle("the whole assistant message is back in the prompt");
        secondEvents.OfType<ContextCompactionCompletedEvent>().Should().ContainSingle();
        secondEvents.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_failed_summary_keeps_the_full_history_and_the_turn_continues()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1)) { SummaryText = null };
        var factory = CreateFactory(client);
        // The round trip brings the prompt to 185, past this trigger.
        var runtime = CreateRuntime(factory, policy: Policy(180));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.OfType<ContextCompactionStartedEvent>().Should().ContainSingle();
        var failed = events.OfType<ContextCompactionFailedEvent>().Should().ContainSingle().Which;
        failed.Reason.Should().Be(DirectCompactionStep.CompactionFailedReason);
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

        // No window and no compaction: nothing is decided from a count, and the meter waits for the
        // provider's own measurement instead of paying for a local one.
        counter.Calls.Should().Be(0, "with no declared window and compaction off, neither setup nor the checkpoint counts");
        events.OfType<UsageProgressEvent>().Should().BeEmpty("no local estimate competes with measurement");
        client.SummaryCalls.Should().BeEmpty();
        events.OfType<ContextCompactionStartedEvent>().Should().BeEmpty();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().BeEmpty();
        client.Requests.Should().HaveCount(3);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_continuation_keeps_its_completion_batch_after_the_summary_without_a_continuation_prompt()
    {
        // The completion batch is counted from the turn start on, so the trigger sits well above the opening
        // prompt (about a thousand tokens with the batch) and the round trip alone carries the turn past it.
        var longAnswer = new string('A', 4_000);
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1, answerLength: 4_000));
        var factory = CreateFactory(client);
        var runtime = CreateRuntime(factory, policy: Policy(3_000));
        var request = CreateRequest(
            factory.Profile.Id,
            history: [(MessageRole.User, "prior prompt"), (MessageRole.Assistant, "prior answer")],
            priorTurnStatus: ConversationTurnStatus.Truncated,
            execution: new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, CompletionBatch()),
            checkpoint: new NoOpCheckpoint(),
            currentTurnInputs: []);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.OfType<ContextCompactionCompletedEvent>().Should().BeEmpty("the opening prompt is below the trigger");
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2);
        var replayed = client.Requests[1].ToList();
        replayed.Should().NotContain(message => message.Text == "prior prompt");
        replayed.Should().Contain(message => message.Text == longAnswer);
        var userTexts = replayed.Where(message => message.Role == ChatRole.User)
            .Select(message => message.Text ?? string.Empty);
        userTexts.Should().OnlyContain(text => text.StartsWith("<selfclaw-subagent-results", StringComparison.Ordinal),
            "resuming a truncated answer belongs to the user, so the batch is the only user message");
        var batchIndex = replayed.FindIndex(message =>
            message.Text?.Contains("<selfclaw-subagent-results", StringComparison.Ordinal) == true);
        batchIndex.Should().BeGreaterThan(0, "the completion batch is protected, not compacted");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task A_consumed_steer_survives_compaction_in_place()
    {
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 1));
        var factory = CreateFactory(client);
        // The steer (30) is protected, so it counts with the instructions against the retained history: of
        // the 155 a trigger of 310 leaves, 77 remain, which is the round trip and the prompt (73).
        var runtime = CreateRuntime(factory, policy: Policy(310));
        var request = CreateRequest(factory.Profile.Id, LongHistory());
        var session = new ScriptedInputSession(request.ConversationId, request.TurnId, [["steer text"]]);
        request = request with { InputSession = session };

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        session.Consumed.Should().Be(1);
        session.HoldReason.Should().BeNull();
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2, "a protected steer is never counted as a unit");
        var replayed = client.Requests[1].ToList();
        replayed.Should().NotContain(message => message.Text == LongPrompt);
        replayed.Select(message => message.Text)
            .Should().ContainInOrder("user prompt", "steer text", "AA");
    }

    [Fact]
    public async Task An_over_window_prompt_is_refused_every_round_not_only_after_a_summary()
    {
        // Compaction is off, so nothing can shrink the prompt. The turn must report the window itself instead
        // of sending the request and relaying whatever the provider says about it.
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 3, answerLength: 300));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 200)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var runtime = CreateRuntime(factory, policy: new DirectCompressionPolicy(Enabled: false));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Which;
        terminal.Status.Should().Be(RunCompletionStatus.Failed);
        terminal.ErrorMessage.Should().Contain("outgrown the model context window");
        terminal.ErrorMessage.Should().Contain("200");
        client.Requests.Should().ContainSingle("the over-window round is refused before it is sent");
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

    [Fact]
    public async Task An_image_on_the_opening_message_does_not_stop_in_turn_compaction()
    {
        // 300-character answers make each round trip cost 340 tokens. A 700 window at 0.9 asks for 630, but only
        // 650 of it is usable input and the trigger stays at 90% of that (585): the second round trip crosses
        // it. The image-bearing opening message is the oldest unit, so only a summary that covers it brings the
        // prompt back under the window.
        var client = new ScriptedClient(ToolRoundsThenFinal(rounds: 2, answerLength: 300));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client, contextWindowTokens: 700)
        {
            Options = new ChatOptions { MaxOutputTokens = 50 }
        };
        var request = CreateRequest(factory.Profile.Id, history: []);
        var reader = new ScriptedAttachmentContentReader().WithImage(request.Messages[0].Id, "photo.png");
        var runtime = CreateRuntime(factory,
            policy: new DirectCompressionPolicy(Enabled: true, ThresholdRatio: 0.9d), reader: reader);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2, "the opening message and the first round trip are the prefix");
        client.Requests.Should().HaveCount(3);
        Images(client.Requests[1]).Should().ContainSingle("the image is sent verbatim until a summary covers it");
        var summarized = client.SummaryCalls.Should().ContainSingle().Subject;
        Images(summarized).Should().BeEmpty("the summarizer never receives binary content");
        summarized.SelectMany(message => message.Contents).OfType<TextContent>()
            .Should().Contain(text => text.Text == "[image: photo.png]");
        Images(client.Requests[2]).Should().BeEmpty("the image left the context with the unit that carried it");
        client.Requests[2].Should().NotContain(message => message.Text == "user prompt");
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Theory]
    [InlineData(AiProviderApiFormat.AnthropicMessages)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions)]
    public async Task A_round_trip_that_read_an_image_is_summarized_once_it_leaves_the_retained_tail(
        AiProviderApiFormat format)
    {
        // The first round trip reads an image; the second is large enough to cross the trigger of 550 and to
        // fill the retained tail on its own. Both delivery shapes are covered: nested in the tool result
        // (Anthropic) and carried by the follow-up user message (Chat Completions).
        var client = new ScriptedClient(
        [
            [Finish("tool-1", ChatFinishReason.ToolCalls, new TextContent("AA"),
                new FunctionCallContent("call-1", "view", new Dictionary<string, object?>()))],
            [Finish("tool-2", ChatFinishReason.ToolCalls, new TextContent(new string('B', 400)),
                new FunctionCallContent("call-2", "probe", new Dictionary<string, object?>()))],
            [Finish("final", ChatFinishReason.Stop, new TextContent("done"))]
        ]);
        var factory = CreateFactory(client);
        factory.Profile = factory.Profile with
        {
            ApiFormat = format,
            Configuration = new AiModelConfiguration("test-model", "Test model",
                new AiSamplingOptions(false, 0.7, false, 0.7), IsMultimodal: true)
        };
        var runtime = CreateRuntime(factory, policy: Policy(550), lease: Lease(Probe(), ImageProbe()));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id, history: [])));

        client.Requests.Should().HaveCount(3);
        Images(client.Requests[1]).Should().ContainSingle("the image is delivered live after it was read")
            .Which.Name.Should().Be("shot.png");
        events.OfType<InTurnContextCompactionCompletedEvent>().Should().ContainSingle()
            .Which.CoveredUnitCount.Should().Be(2, "the opening message and the image round trip are the prefix");
        var summarized = client.SummaryCalls.Should().ContainSingle().Subject;
        Images(summarized).Should().BeEmpty("the summarizer never receives binary content");
        Flatten(summarized).OfType<TextContent>().Should().Contain(text => text.Text == "[image: shot.png]");

        var compacted = client.Requests[2];
        Images(compacted).Should().BeEmpty("the image left the context with the round trip that read it");
        compacted.SelectMany(message => message.Contents).OfType<FunctionCallContent>()
            .Select(call => call.CallId).Should().Equal("call-2");
        AssertNoOrphanedCalls(compacted);
        events.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    // A native tool result nests its parts, so an image is looked for one level down as well.
    private static IEnumerable<AIContent> Flatten(IReadOnlyList<ChatMessage> messages)
        => messages.SelectMany(message => message.Contents).SelectMany(content =>
            content is FunctionResultContent { Result: IEnumerable<AIContent> nested } ? nested : new[] { content });

    private static DataContent[] Images(IReadOnlyList<ChatMessage> messages)
        => Flatten(messages).OfType<DataContent>().ToArray();

    private static DirectAgentChatRuntime CreateRuntime(
        DirectAgentChatRuntimeTests.FakeChatClientFactory factory,
        DirectCompressionPolicy? policy = null,
        IDirectTokenCounter? counter = null,
        IAttachmentContentReader? reader = null,
        DirectTurnCapabilityLease? lease = null)
        => DirectAgentChatRuntimeTests.CreateRuntime(
            factory,
            new FixedResolver(lease ?? Lease(Probe())),
            new FixedPolicySource(policy ?? Policy(200)),
            counter ?? new CountingTokenCounter(),
            attachmentContentReader: reader);

    private static DirectAgentChatRuntimeTests.FakeChatClientFactory CreateFactory(ScriptedClient client)
        => new(client) { Options = new ChatOptions { MaxOutputTokens = 50 } };

    private static DirectCompressionPolicy Policy(int absoluteThresholdTokens)
        => new(Enabled: true, AbsoluteThresholdTokens: absoluteThresholdTokens);

    private static (MessageRole Role, string Text)[] LongHistory()
        => [(MessageRole.User, LongPrompt), (MessageRole.Assistant, LongAnswer)];

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

    /// <summary>
    /// The request the next turn is built from, stored the way the recorder stores a finished turn: one assistant
    /// message whose segments follow the event order, one tool run per call, and the turn's in-turn compaction
    /// with its anchor call resolved to that run.
    /// </summary>
    private static DirectChatTurnRequest CreateFollowUpRequest(
        DirectChatTurnRequest previous,
        IReadOnlyList<AgentStreamEvent> events,
        bool keepAnchor)
    {
        var now = DateTimeOffset.UtcNow;
        var assistantId = Guid.NewGuid();
        var sequence = previous.Messages.Max(message => message.Sequence);
        var segments = new List<MessageSegmentRecord>();
        var runs = new Dictionary<string, ToolExecutionRecord>(StringComparer.Ordinal);
        foreach (var streamEvent in events)
        {
            switch (streamEvent)
            {
                case AssistantTextDeltaEvent text when segments is [.., { Kind: MessageSegmentKind.Text } last]:
                    segments[^1] = last with { Text = last.Text + text.Delta };
                    break;
                case AssistantTextDeltaEvent text:
                    segments.Add(new MessageSegmentRecord(assistantId, segments.Count, MessageSegmentKind.Text, text.Delta, null));
                    break;
                case ToolCallStartedEvent started:
                    var run = new ToolExecutionRecord(Guid.NewGuid(), previous.ConversationId, started.ToolName,
                        started.ArgumentsJson, ToolExecutionStatus.Running, null, started.ToolCallId, null, now, now,
                        MessageId: assistantId);
                    runs.Add(started.ToolCallId, run);
                    segments.Add(new MessageSegmentRecord(assistantId, segments.Count, MessageSegmentKind.ToolCall, null, run.Id));
                    break;
                case ToolCallCompletedEvent completed:
                    runs[completed.ToolCallId] = runs[completed.ToolCallId] with
                    {
                        Status = ToolExecutionStatus.Completed,
                        ResultSummary = completed.ResultSummary,
                        ResultContent = completed.ResultContent
                    };
                    break;
            }
        }

        var inTurn = events.OfType<InTurnContextCompactionCompletedEvent>().Last();
        var compaction = inTurn.Compaction ?? throw new InvalidOperationException("The turn stored no compaction.");
        if (keepAnchor && inTurn.AnchorCallId is { } anchorCallId)
        {
            compaction = compaction with { ThroughToolRunId = runs[anchorCallId].Id };
        }

        var turnId = Guid.NewGuid();
        var turns = previous.Turns
            .Select(turn => turn.Id == previous.TurnId
                ? turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = now }
                : turn)
            .Append(new ConversationTurnRecord(turnId, previous.ConversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now.AddMinutes(1)))
            .ToArray();
        MessageRecord[] messages =
        [
            .. previous.Messages,
            new MessageRecord(assistantId, previous.ConversationId, previous.TurnId, sequence + 1, MessageRole.Assistant,
                string.Empty, MessageStatus.Sealed, now, now, Segments: segments),
            CreateMessage(previous.ConversationId, turnId, sequence + 2, MessageRole.User, "next prompt", now)
        ];
        return new DirectChatTurnRequest(turnId, previous.ConversationId, null, previous.Agent, messages, turns,
            previous.ModelProfileId, ToolPermissionMode.FullAccess, null, previous.ExecutionContext,
            ToolExecutions: runs.Values.ToArray(), Compression: compaction);
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

    private static DirectTurnCapabilityLease Lease(params AIFunction[] tools)
        => new([], tools.Select(tool => new DirectToolBinding(tool, new DirectToolDescriptor(ToolCallKind.Other))).ToArray(),
            new Dictionary<Guid, string>(), []);

    /// <summary>
    /// Answers like <c>read_file</c> does for an image: a text header plus the image as model content. The loop
    /// decides how the protocol carries it, so one tool serves both delivery shapes.
    /// </summary>
    private static AIFunction ImageProbe()
        => AIFunctionFactory.Create(
            () => new DirectToolResult(ToolCallStatus.Completed, "Read shot.png.",
                JsonSerializer.SerializeToElement("shot.png"), "Read shot.png.",
                [
                    new TextContent("Read image 'shot.png'."),
                    new DataContent(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A }, "image/png")
                    {
                        Name = "shot.png"
                    }
                ]),
            new AIFunctionFactoryOptions
            {
                Name = "view",
                Description = "Synthetic image tool.",
                ExcludeResultSchema = true,
                MarshalResult = (value, _, _) => ValueTask.FromResult(value)
            });

    /// <summary>
    /// Returns a fresh result per invocation: a tool that answers identically every time is exactly what the
    /// no-progress guard stops, and these cases are about compaction, not about a stuck loop.
    /// </summary>
    private static AIFunction Probe()
    {
        var invocations = 0;
        return AIFunctionFactory.Create(
            () =>
            {
                var result = $"ok-{++invocations}";
                return new DirectToolResult(ToolCallStatus.Completed, result,
                    JsonSerializer.SerializeToElement(result), result);
            },
            new AIFunctionFactoryOptions
            {
                Name = "probe",
                Description = "Synthetic probe tool.",
                ExcludeResultSchema = true,
                MarshalResult = (value, _, _) => ValueTask.FromResult(value)
            });
    }

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
        public Task<DirectTurnCapabilityLease> ResolveAsync(DirectChatTurnRequest request, ToolImageCapability imageCapability,
            CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private sealed class NoOpCheckpoint : IToolExecutionCheckpoint
    {
        public Task BeforeExecutionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic token arithmetic: every message costs <see cref="MessageCost"/> plus the length of its text
    /// content. Compaction boundaries therefore depend only on the texts a test chose. The turn-start tests
    /// share it for the same reason.
    /// </summary>
    internal sealed class CountingTokenCounter : IDirectTokenCounter
    {
        internal const int MessageCost = 20;

        internal int Calls { get; private set; }

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
