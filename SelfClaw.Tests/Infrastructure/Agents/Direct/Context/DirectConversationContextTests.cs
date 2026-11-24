using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

/// <summary>
/// I1: the turn's request history is a system text, an ordered list of compressible units and the protected
/// messages compaction may never cover. The flattened list must stay exactly the order it was added in,
/// because every provider replays it verbatim.
/// </summary>
public sealed class DirectConversationContextTests
{
    private const string SystemText = "system instructions";

    [Fact]
    public void Messages_expand_units_and_protected_messages_in_insertion_order()
    {
        var context = new DirectConversationContext(
            SystemText,
            [Unit(1, "first"), Unit(2, "second")],
            [new ChatMessage(ChatRole.User, "protected tail")]);

        context.Messages.Select(message => message.Role)
            .Should().Equal(ChatRole.System, ChatRole.User, ChatRole.User, ChatRole.User);
        context.Messages.Select(message => message.Text)
            .Should().Equal(SystemText, "first", "second", "protected tail");
    }

    [Fact]
    public void Messages_start_with_the_first_unit_when_the_turn_has_no_system_text()
    {
        var context = new DirectConversationContext(null, [Unit(1, "first")], []);

        context.Messages.Select(message => message.Text).Should().Equal("first");
    }

    [Fact]
    public void Protected_messages_keep_their_position_between_units()
    {
        var context = new DirectConversationContext(
            null,
            [Unit(1, "replayed")],
            [new ChatMessage(ChatRole.User, "trailing prompt")]);
        context.AppendResponse(Response("answer one"));
        context.AppendInputs([Steer(sequence: 9, "steer")]);
        context.AppendResponse(Response("answer two"));

        context.Messages.Select(message => message.Text)
            .Should().Equal("replayed", "trailing prompt", "answer one", "steer", "answer two");
    }

    [Fact]
    public void A_response_and_its_tool_results_form_one_unit()
    {
        var context = new DirectConversationContext(null, [], []);
        context.AppendResponse(ToolCallResponse("call-1"));
        context.AppendResults([new FunctionResultContent("call-1", "result")]);
        context.AppendResponse(Response("final answer"));

        var units = context.BuildUnits();
        units.Should().HaveCount(2);
        units[0].Messages.Select(message => message.Role).Should().Equal(ChatRole.Assistant, ChatRole.Tool);
        units[0].Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle();
        units[0].Messages[1].Contents.OfType<FunctionResultContent>().Should().ContainSingle()
            .Which.Result.Should().Be("result");
        units[1].Messages.Select(message => message.Text).Should().Equal("final answer");
        context.Messages.Select(message => message.Role)
            .Should().Equal([ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant],
                "the call and its result stay adjacent in the flattened history");
    }

    [Fact]
    public void Tool_results_require_a_preceding_response()
    {
        var context = new DirectConversationContext(null, [], []);

        var action = () => context.AppendResults([new FunctionResultContent("call-1", "result")]);

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Applying_compaction_removes_only_the_oldest_units_and_keeps_protected_messages_in_place()
    {
        var context = new DirectConversationContext(SystemText, [Unit(1, "oldest"), Unit(2, "older")], []);
        context.AppendInputs([Steer(sequence: 1, "steer one")]);
        context.AppendResponse(Response("newest"));
        context.AppendInputs([Steer(sequence: 2, "steer two")]);
        context.Summary.Should().BeNull();

        context.ApplyInTurnCompaction("turned into a summary", coveredUnitCount: 2);

        context.Messages.Select(message => message.Role)
            .Should().Equal(ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User);
        context.Messages.Select(message => message.Text).Should().Equal(
            $"{SystemText}\n\n{SummarySection("turned into a summary")}",
            "steer one",
            "newest",
            "steer two");
        context.BuildUnits().Select(unit => unit.SourceSequence).Should().Equal(3);
        context.Summary.Should().Be("turned into a summary",
            "chained compaction hands the bare summary back to the compressor, which wraps it itself");
    }

    [Fact]
    public void A_second_compaction_replaces_the_summary_section_instead_of_stacking_it()
    {
        var context = new DirectConversationContext(SystemText, [Unit(1, "oldest")], []);
        context.AppendResponse(Response("middle"));
        context.AppendResponse(Response("newest"));

        context.ApplyInTurnCompaction("first summary", coveredUnitCount: 1);
        context.ApplyInTurnCompaction("second summary", coveredUnitCount: 1);

        var system = context.Messages[0];
        system.Role.Should().Be(ChatRole.System);
        system.Text.Should().StartWith(SystemText);
        system.Text.Should().EndWith(SummarySection("second summary"),
            "the in-turn summary is appended at the end of the prompt's own system text");
        system.Text.Should().NotContain("first summary");
        system.Text.Split("<selfclaw-context-summary").Should().HaveCount(2, "only one in-turn section exists");
        context.Messages.Select(message => message.Text).Should().Equal(system.Text, "newest");
        context.Summary.Should().Be("second summary");
    }

    [Fact]
    public void Applying_compaction_resets_the_incremental_token_count()
    {
        var counter = new CharacterTokenCounter();
        var profile = CreateProfile();
        var context = new DirectConversationContext(
            SystemText,
            [Unit(1, new string('a', 10)), Unit(2, new string('b', 20))],
            []);
        context.AppendResponse(Response(new string('c', 30)));
        context.CountTokens(counter, profile).Should().Be(CountEverything(counter, profile, context));

        context.ApplyInTurnCompaction("summary", coveredUnitCount: 2);

        context.CountTokens(counter, profile).Should().Be(CountEverything(counter, profile, context),
            "the tally is rebuilt from the compacted messages instead of keeping the pre-compaction total");
        context.Messages[0].Text.Should().Contain(SummarySection("summary"));
    }

    [Fact]
    public void In_turn_units_start_after_the_replayed_history_and_stay_positive()
    {
        var context = new DirectConversationContext(null, [Unit(7, "replayed")], []);
        context.AppendResponse(Response("first answer"));
        context.AppendResponse(Response("second answer"));

        context.BuildUnits().Select(unit => unit.SourceSequence).Should().Equal(7, 8, 9);

        var empty = new DirectConversationContext(null, [], []);
        empty.AppendResponse(Response("only answer"));

        // Compaction only considers units with a positive source sequence, so a fresh turn may never
        // number its own units from zero.
        empty.BuildUnits().Should().ContainSingle().Which.SourceSequence.Should().Be(1);
    }

    [Fact]
    public void The_summary_the_turn_started_with_is_the_section_an_in_turn_compaction_replaces()
    {
        var context = new DirectConversationContext(
            SystemText, [Unit(4, "replayed")], [], planSection: "plan", summary: "stored summary");
        context.AppendResponse(Response("newest"));

        context.Summary.Should().Be("stored summary",
            "the first in-turn compaction has to fold the summary the request already carries into its own");
        context.Messages[0].Text.Should().Be($"{SystemText}\n\n{SummarySection("stored summary")}\n\nplan",
            "the summary follows the fixed text and precedes the plan");

        context.ApplyInTurnCompaction("folded summary", coveredUnitCount: 1);

        var text = context.Messages[0].Text ?? string.Empty;
        text.Should().Be($"{SystemText}\n\n{SummarySection("folded summary")}\n\nplan");
        text.Split("<selfclaw-context-summary").Should().HaveCount(2, "a request never carries two summaries");
    }

    [Fact]
    public void Covering_only_replayed_units_moves_the_boundary_to_the_last_of_them()
    {
        var context = new DirectConversationContext(
            null, [Unit(4, "oldest"), Unit(5, "older"), Unit(6, "newest")], [], historyThroughSequence: 6);

        var coverage = context.ApplyInTurnCompaction("summary", coveredUnitCount: 2, sourceTokens: 120);

        coverage.Should().Be(new DirectInTurnCoverage(5, AnchorCallId: null, MessageCount: 2, SourceTokens: 120));
    }

    [Fact]
    public void Covering_a_unit_of_the_turn_covers_the_stored_history_and_anchors_on_its_last_call()
    {
        // Sequence 7 stands for a stored message that forms no unit (a blocked turn): once the turn's own
        // units are covered, everything stored before the turn began is behind the boundary.
        var context = new DirectConversationContext(
            null, [Unit(4, "older"), Unit(5, "newest")], [], historyThroughSequence: 7);
        context.AppendResponse(ToolCallResponse("call-1", "call-2"));
        context.AppendResults([new FunctionResultContent("call-1", "one"), new FunctionResultContent("call-2", "two")]);
        context.AppendResponse(ToolCallResponse("call-3"));
        context.AppendResults([new FunctionResultContent("call-3", "three")]);
        context.AppendResponse(Response("newest"));

        var first = context.ApplyInTurnCompaction("first", coveredUnitCount: 3, sourceTokens: 300);
        var second = context.ApplyInTurnCompaction("second", coveredUnitCount: 1, sourceTokens: 50);

        first.Should().Be(new DirectInTurnCoverage(7, "call-2", MessageCount: 3, SourceTokens: 300),
            "two stored messages and the turn's own assistant message are covered");
        second.Should().Be(new DirectInTurnCoverage(7, "call-3", MessageCount: 3, SourceTokens: 350),
            "the coverage of one turn accumulates on one record");
    }

    [Fact]
    public void A_consumed_steer_ends_what_a_stored_boundary_can_cover()
    {
        var context = new DirectConversationContext(null, [Unit(4, "replayed")], [], historyThroughSequence: 4);
        context.AppendResponse(ToolCallResponse("call-1"));
        context.AppendResults([new FunctionResultContent("call-1", "one")]);
        context.AppendInputs([Steer(sequence: 6, "steer")]);
        context.AppendResponse(ToolCallResponse("call-2"));
        context.AppendResults([new FunctionResultContent("call-2", "two")]);
        context.AppendResponse(ToolCallResponse("call-3"));
        context.AppendResults([new FunctionResultContent("call-3", "three")]);

        var crossing = context.ApplyInTurnCompaction("crossing", coveredUnitCount: 3, sourceTokens: 90);
        var beyond = context.ApplyInTurnCompaction("beyond", coveredUnitCount: 1, sourceTokens: 30);

        crossing.Should().Be(new DirectInTurnCoverage(4, "call-1", MessageCount: 2, SourceTokens: 90),
            "the boundary stops in front of the steer, which stays in the prompt");
        beyond.Should().BeNull("a stored boundary cannot step over a message that is still replayed");
        context.Messages.Select(message => message.Text).Should().Contain("steer");
        context.Summary.Should().Be("beyond", "the request itself still carries the newest summary");
    }

    [Fact]
    public void The_completion_batch_does_not_end_the_stored_boundary()
    {
        var context = new DirectConversationContext(
            null, [Unit(4, "replayed")], [new ChatMessage(ChatRole.User, "completion batch")],
            historyThroughSequence: 4);
        context.AppendResponse(ToolCallResponse("call-1"));
        context.AppendResults([new FunctionResultContent("call-1", "one")]);
        context.AppendResponse(Response("newest"));

        var coverage = context.ApplyInTurnCompaction("summary", coveredUnitCount: 2);

        coverage.Should().Be(new DirectInTurnCoverage(4, "call-1", MessageCount: 2, SourceTokens: null),
            "the batch is never stored, so no stored message sits between the two covered units");
    }

    [Fact]
    public void A_compaction_that_moves_no_stored_boundary_reports_no_coverage()
    {
        var context = new DirectConversationContext(null, [Unit(4, "replayed")], [], historyThroughSequence: 4);
        context.AppendResponse(Response("an answer without a tool call"));
        context.AppendResponse(Response("newest"));
        context.ApplyInTurnCompaction("first", coveredUnitCount: 1).Should().NotBeNull();

        var coverage = context.ApplyInTurnCompaction("second", coveredUnitCount: 1);

        coverage.Should().BeNull("a response without a call has nothing to anchor on, so the next turn replays it");
    }

    [Fact]
    public void Each_turn_has_its_own_in_turn_compaction_identity()
    {
        var context = new DirectConversationContext(null, [], []);

        context.InTurnCompactionId.Should().NotBeEmpty();
        context.InTurnCompactionId.Should().Be(context.InTurnCompactionId, "every summary of the turn rewrites one record");
        new DirectConversationContext(null, [], []).InTurnCompactionId.Should().NotBe(context.InTurnCompactionId);
    }

    private static int CountEverything(CharacterTokenCounter counter, AiModelProfile profile,
        DirectConversationContext context)
    {
        var total = 0;
        foreach (var message in context.Messages)
        {
            total += counter.CountMessages([message], profile) ?? 0;
        }

        return total;
    }

    // The section alone, as the production composition wraps a bare summary.
    private static string SummarySection(string summary)
        => DirectSystemPrompt.ComposeSystemText(null, summary, null) ?? string.Empty;

    private static DirectPromptHistoryUnit Unit(long sequence, string text)
        => new([new ChatMessage(ChatRole.User, text)], sequence);

    private static ChatResponse Response(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse ToolCallResponse(params string[] callIds)
        => new(new ChatMessage(ChatRole.Assistant,
            [.. callIds.Select(callId => (AIContent)new FunctionCallContent(callId, "probe"))]));

    private static MessageRecord Steer(long sequence, string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), sequence, MessageRole.User, text,
            MessageStatus.Sealed, now, now);
    }

    private static AiModelProfile CreateProfile()
    {
        var now = DateTimeOffset.UnixEpoch;
        return new AiModelProfile(Guid.NewGuid(), Guid.NewGuid(), "test-model",
            AiProviderApiFormat.OpenAIChatCompletions, "test-model", new AiSamplingOptions(false, 0d, false, 0d),
            new Dictionary<string, System.Text.Json.JsonElement>(), now, now);
    }

    /// <summary>
    /// A counting stand-in whose value depends on the message text, so a stale incremental tally cannot
    /// accidentally equal the rebuilt one.
    /// </summary>
    private sealed class CharacterTokenCounter : IDirectTokenCounter
    {
        public int? CountText(string? text, AiModelProfile? model) => text?.Length ?? 0;

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model)
            => messages.Sum(message => (message.Text?.Length ?? 0) + 1);

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model) => 0;
    }
}
