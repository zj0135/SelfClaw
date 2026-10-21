using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class DirectPromptTurnHistoryTests
{
    [Fact]
    public void Interleaved_fragments_replay_by_sequence_even_with_equal_timestamps_and_shuffled_input()
    {
        var turn = Turn(ConversationTurnStatus.Succeeded);
        var messages = new[] { Message(turn, 4, MessageRole.Assistant, "A1"), Message(turn, 2, MessageRole.Assistant, "A0"),
            Message(turn, 3, MessageRole.User, "U1"), Message(turn, 1, MessageRole.User, "U0") };
        Replay(messages, [turn]).Select(message => message.Text).Should().Equal("U0", "A0", "U1", "A1");
        messages.Should().OnlyContain(message => message.Id != turn.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blocked_turn_excludes_every_input_with_or_without_an_assistant(bool hasAssistant)
    {
        var blocked = Turn(ConversationTurnStatus.Blocked);
        var current = blocked with { Id = Guid.NewGuid(), Status = ConversationTurnStatus.Running };
        var messages = new List<MessageRecord> { Message(blocked, 1, MessageRole.User, "blocked first"),
            Message(blocked, 2, MessageRole.User, "blocked steer"), Message(current, 4, MessageRole.User, "safe") };
        if (hasAssistant) messages.Add(Message(blocked, 3, MessageRole.Assistant, "blocked output"));
        Replay(messages, [blocked, current]).Should().ContainSingle().Which.Text.Should().Be("safe");
    }

    [Theory]
    [InlineData(ConversationTurnStatus.Failed)]
    [InlineData(ConversationTurnStatus.Cancelled)]
    [InlineData(ConversationTurnStatus.Interrupted)]
    public void Unsuccessful_turn_replays_every_settled_fragment_including_the_interrupted_one(ConversationTurnStatus status)
    {
        var turn = Turn(status);
        var sealedMessage = Message(turn, 1, MessageRole.Assistant, "complete");
        var interrupted = Message(turn, 2, MessageRole.Assistant, "cut off") with { Status = MessageStatus.Interrupted };
        Replay([interrupted, sealedMessage], [turn]).Select(message => message.Text)
            .Should().Equal("complete", "cut off");
    }

    [Fact]
    public void Fragment_that_never_settled_is_not_replayed_even_when_its_turn_still_looks_running()
    {
        var turn = Turn(ConversationTurnStatus.Running);
        var streaming = Message(turn, 1, MessageRole.Assistant, "half written") with { Status = MessageStatus.Streaming };
        Replay([streaming], [turn]).Should().BeEmpty();
    }

    [Fact]
    public void Interrupted_fragment_replays_its_finished_tool_units_and_skips_the_unfinished_call()
    {
        var turn = Turn(ConversationTurnStatus.Failed);
        var message = Message(turn, 1, MessageRole.Assistant, "working") with { Status = MessageStatus.Interrupted };
        var finished = Tool(message, ToolExecutionStatus.Completed);
        var unfinished = Tool(message, ToolExecutionStatus.Running);
        message = message with
        {
            Segments =
            [
                new(message.Id, 0, MessageSegmentKind.Text, "working", null),
                new(message.Id, 1, MessageSegmentKind.ToolCall, null, finished.Id),
                new(message.Id, 2, MessageSegmentKind.ToolCall, null, unfinished.Id)
            ]
        };
        var result = Replay([message], [turn], [finished, unfinished]);
        result.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Should()
            .ContainSingle().Which.CallId.Should().Be(finished.CorrelationId);
        result.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Should()
            .ContainSingle().Which.CallId.Should().Be(finished.CorrelationId);
        result.Select(message => message.Text).Should().Equal("working", string.Empty);
    }

    [Fact]
    public void Only_terminal_tools_anchored_to_the_actual_message_and_conversation_are_replayed()
    {
        var turn = Turn(ConversationTurnStatus.Failed);
        var message = Message(turn, 1, MessageRole.Assistant, "complete");
        var good = Tool(message, ToolExecutionStatus.Failed);
        var running = Tool(message, ToolExecutionStatus.Running);
        var foreignMessage = Tool(message, ToolExecutionStatus.Completed) with { MessageId = Guid.NewGuid() };
        var foreignConversation = Tool(message, ToolExecutionStatus.Completed) with { ConversationId = Guid.NewGuid() };
        var tools = new[] { good, running, foreignMessage, foreignConversation };
        message = message with { Segments = tools.Select((tool, index) => new MessageSegmentRecord(message.Id,
            index, MessageSegmentKind.ToolCall, null, tool.Id)).ToArray() };
        var result = Replay([message], [turn], tools);
        result.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Should().ContainSingle()
            .Which.CallId.Should().Be(good.CorrelationId);
        result.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Should().ContainSingle()
            .Which.CallId.Should().Be(good.CorrelationId);
    }

    [Fact]
    public void Truncated_turn_replays_its_fragments_and_never_injects_a_continuation_prompt()
    {
        var turn = Turn(ConversationTurnStatus.Truncated);
        var first = Message(turn, 1, MessageRole.Assistant, "first");
        var last = Message(turn, 2, MessageRole.Assistant, "last");
        Replay([last, first], [turn]).Select(message => message.Text).Should().Equal("first", "last");
    }

    [Fact]
    public void Missing_turn_and_duplicate_sequence_are_data_errors_not_legacy_fallbacks()
    {
        var turn = Turn(ConversationTurnStatus.Succeeded);
        var first = Message(turn, 1, MessageRole.User, "first");
        var missing = () => Replay([first], []);
        missing.Should().Throw<InvalidDataException>();
        var duplicated = () => Replay([first, Message(turn, 1, MessageRole.User, "second")], [turn]);
        duplicated.Should().Throw<InvalidDataException>();
    }

    private static IReadOnlyList<ChatMessage> Replay(IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ConversationTurnRecord> turns, IReadOnlyList<ToolExecutionRecord>? tools = null)
        => new DirectPromptComposer().BuildMessages(messages, turns, tools ?? [], "", [], new Dictionary<Guid, string>(),
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));

    private static ConversationTurnRecord Turn(ConversationTurnStatus status)
        => new(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Interactive,
            status, DateTimeOffset.UtcNow);

    private static MessageRecord Message(ConversationTurnRecord turn, long sequence, MessageRole role, string text)
    {
        var id = Guid.NewGuid();
        return new(id, turn.ConversationId, turn.Id, sequence, role, text, MessageStatus.Sealed,
            turn.StartedAtUtc, turn.StartedAtUtc, Segments: role == MessageRole.Assistant
                ? [new(id, 0, MessageSegmentKind.Text, text, null)] : null);
    }

    private static ToolExecutionRecord Tool(MessageRecord message, ToolExecutionStatus status)
        => new(Guid.NewGuid(), message.ConversationId, "read_file", "{}", status, "result", Guid.NewGuid().ToString("D"),
            0, message.CreatedAtUtc, message.UpdatedAtUtc, MessageId: message.Id, ResultContent: "result");
}
