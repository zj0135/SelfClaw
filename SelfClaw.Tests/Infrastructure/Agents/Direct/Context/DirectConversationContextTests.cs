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
        context.AppendResponse(Response("answer one"), NoBindings());
        context.AppendInputs([Steer(sequence: 9, "steer")]);
        context.AppendResponse(Response("answer two"), NoBindings());

        context.Messages.Select(message => message.Text)
            .Should().Equal("replayed", "trailing prompt", "answer one", "steer", "answer two");
    }

    [Fact]
    public void A_response_and_its_tool_results_form_one_unit()
    {
        var context = new DirectConversationContext(null, [], []);
        context.AppendResponse(ToolCallResponse("call-1"), Bindings("probe"))
            .Should().ContainSingle().Which.CallId.Should().Be("call-1");
        context.AppendResults([new FunctionResultContent("call-1", "result")]);
        context.AppendResponse(Response("final answer"), NoBindings());

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
        context.AppendResponse(Response("newest"), NoBindings());
        context.AppendInputs([Steer(sequence: 2, "steer two")]);
        context.InTurnSummary.Should().BeNull();

        context.ApplyInTurnCompaction("turned into a summary", coveredUnitCount: 2);

        context.Messages.Select(message => message.Role)
            .Should().Equal(ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User);
        context.Messages.Select(message => message.Text).Should().Equal(
            $"{SystemText}\n\n{DirectPromptComposer.CreateSummarySection("turned into a summary")}",
            "steer one",
            "newest",
            "steer two");
        context.BuildUnits().Select(unit => unit.SourceSequence).Should().Equal(3);
        context.InTurnSummary.Should().Be("turned into a summary",
            "chained compaction hands the bare summary back to the compressor, which wraps it itself");
    }

    [Fact]
    public void A_second_compaction_replaces_the_summary_section_instead_of_stacking_it()
    {
        var context = new DirectConversationContext(SystemText, [Unit(1, "oldest")], []);
        context.AppendResponse(Response("middle"), NoBindings());
        context.AppendResponse(Response("newest"), NoBindings());

        context.ApplyInTurnCompaction("first summary", coveredUnitCount: 1);
        context.ApplyInTurnCompaction("second summary", coveredUnitCount: 1);

        var system = context.Messages[0];
        system.Role.Should().Be(ChatRole.System);
        system.Text.Should().StartWith(SystemText);
        system.Text.Should().EndWith(DirectPromptComposer.CreateSummarySection("second summary"),
            "the in-turn summary is appended at the end of the prompt's own system text");
        system.Text.Should().NotContain("first summary");
        system.Text.Split("<selfclaw-context-summary").Should().HaveCount(2, "only one in-turn section exists");
        context.Messages.Select(message => message.Text).Should().Equal(system.Text, "newest");
        context.InTurnSummary.Should().Be("second summary");
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
        context.AppendResponse(Response(new string('c', 30)), NoBindings());
        context.CountTokens(counter, profile).Should().Be(CountEverything(counter, profile, context));

        context.ApplyInTurnCompaction("summary", coveredUnitCount: 2);

        context.CountTokens(counter, profile).Should().Be(CountEverything(counter, profile, context),
            "the tally is rebuilt from the compacted messages instead of keeping the pre-compaction total");
        context.Messages[0].Text.Should().Contain(DirectPromptComposer.CreateSummarySection("summary"));
    }

    [Fact]
    public void In_turn_units_start_after_the_replayed_history_and_stay_positive()
    {
        var context = new DirectConversationContext(null, [Unit(7, "replayed")], []);
        context.AppendResponse(Response("first answer"), NoBindings());
        context.AppendResponse(Response("second answer"), NoBindings());

        context.BuildUnits().Select(unit => unit.SourceSequence).Should().Equal(7, 8, 9);

        var empty = new DirectConversationContext(null, [], []);
        empty.AppendResponse(Response("only answer"), NoBindings());

        // Compaction only considers units with a positive source sequence, so a fresh turn may never
        // number its own units from zero.
        empty.BuildUnits().Should().ContainSingle().Which.SourceSequence.Should().Be(1);
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

    private static DirectPromptHistoryUnit Unit(long sequence, string text)
        => new([new ChatMessage(ChatRole.User, text)], sequence);

    private static ChatResponse Response(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    private static ChatResponse ToolCallResponse(string callId)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, "probe")]));

    private static IReadOnlyDictionary<string, DirectToolBinding> NoBindings()
        => new Dictionary<string, DirectToolBinding>();

    private static IReadOnlyDictionary<string, DirectToolBinding> Bindings(string name)
        => new Dictionary<string, DirectToolBinding>
        {
            [name] = new(AIFunctionFactory.Create(() => "ok", name), new DirectToolDescriptor(ToolCallKind.Other))
        };

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
        public bool IsAvailable(AiModelProfile? model) => true;

        public int? CountText(string? text, AiModelProfile? model) => text?.Length ?? 0;

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model)
            => messages.Sum(message => (message.Text?.Length ?? 0) + 1);

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model) => 0;
    }
}
