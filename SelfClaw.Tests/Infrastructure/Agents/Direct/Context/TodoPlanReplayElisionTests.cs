using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct.Context;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class TodoPlanReplayElisionTests
{
    [Fact]
    public void Only_the_newest_valid_plan_replays_its_arguments()
    {
        var history = new ComposerHistory();
        var older = history.AddTodoCall(TodoArguments("Plan the tool"));
        var newest = history.AddTodoCall(TodoArguments("Wire the transcript"));

        var olderCall = CallOf(history, older);
        var newerCall = CallOf(history, newest);

        Assert.NotNull(olderCall.Arguments);
        olderCall.Arguments.Should().ContainKey("elided");
        Assert.NotNull(newerCall.Arguments);
        newerCall.Arguments.Should().ContainKey("todos");
        Pair(history, older).Result.Should().Be(DirectPromptComposer.ElidedTodoResultText);
        Pair(history, newest).Result.Should().Be(ComposerHistory.ResultText, "the newest plan replays as stored");
    }

    [Fact]
    public void A_failed_latest_call_is_elided_and_the_previous_plan_stays_complete()
    {
        var history = new ComposerHistory();
        var valid = history.AddTodoCall(TodoArguments("Plan the tool"));
        var failed = history.AddTodoCall("""{"todos":[{"content":"","status":"pending"}]}""", ToolExecutionStatus.Failed);

        var validCall = CallOf(history, valid);
        var failedCall = CallOf(history, failed);

        Assert.NotNull(validCall.Arguments);
        validCall.Arguments.Should().ContainKey("todos");
        Assert.NotNull(failedCall.Arguments);
        failedCall.Arguments.Should().ContainKey("elided");
        var failedResult = Pair(history, failed);
        failedResult.Result.Should().Be(DirectPromptComposer.ElidedTodoResultText);
        failedResult.Exception.Should().NotBeNull("a failed call still replays as an error");
        Pair(history, valid).Exception.Should().BeNull();
    }

    [Fact]
    public void A_cleared_plan_replays_in_full()
    {
        var history = new ComposerHistory();
        var older = history.AddTodoCall(TodoArguments("Plan the tool"));
        var cleared = history.AddTodoCall("""{"todos":[]}""");

        var clearedCall = CallOf(history, cleared);

        Assert.NotNull(clearedCall.Arguments);
        clearedCall.Arguments.Should().ContainKey("todos", "an empty list is a legitimate current plan");
        Pair(history, cleared).Result.Should().Be(ComposerHistory.ResultText);
        Assert.NotNull(CallOf(history, older).Arguments);
        CallOf(history, older).Arguments.Should().ContainKey("elided");
    }

    [Fact]
    public void A_tool_that_is_not_a_plan_keeps_its_arguments()
    {
        var history = new ComposerHistory();
        var read = history.AddToolCall("read_file", """{"relativePath":"README.md"}""");
        history.AddTodoCall(TodoArguments("Plan the tool"));

        var readCall = CallOf(history, read);

        Assert.NotNull(readCall.Arguments);
        readCall.Arguments["relativePath"]?.ToString().Should().Be("README.md");
        Pair(history, read).Result.Should().Be(ComposerHistory.ResultText);
    }

    private static FunctionCallContent CallOf(ComposerHistory history, ToolExecutionRecord run)
        => Replay(history).SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Single(call => call.CallId == run.CorrelationId);

    // A replay that cannot find exactly one result for the call would break the call/result pairing.
    private static FunctionResultContent Pair(ComposerHistory history, ToolExecutionRecord run)
        => Replay(history).SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Single(result => result.CallId == run.CorrelationId);

    private static IReadOnlyList<ChatMessage> Replay(ComposerHistory history)
        => new DirectPromptComposer()
            .BuildUnits(history.Messages, history.Turns, history.Runs, new Dictionary<Guid, string>())
            .SelectMany(unit => unit.Messages)
            .ToArray();

    private static string TodoArguments(string content)
        => $$"""{"todos":[{"content":"{{content}}","status":"in_progress"}]}""";

    private sealed class ComposerHistory
    {
        private readonly Guid _conversationId = Guid.NewGuid();
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private int _callCount;

        public List<MessageRecord> Messages { get; } = [];

        public List<ConversationTurnRecord> Turns { get; } = [];

        public List<ToolExecutionRecord> Runs { get; } = [];

        internal const string ResultText = "Plan updated: 1 tasks (0 completed, 1 in progress).";

        public ToolExecutionRecord AddTodoCall(
            string argumentsJson,
            ToolExecutionStatus status = ToolExecutionStatus.Completed)
            => AddToolCall(TodoPlanRules.ToolName, argumentsJson, status);

        public ToolExecutionRecord AddToolCall(
            string toolName,
            string argumentsJson,
            ToolExecutionStatus status = ToolExecutionStatus.Completed)
        {
            var turnId = Guid.NewGuid();
            Turns.Add(new ConversationTurnRecord(turnId, _conversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Interactive, ConversationTurnStatus.Succeeded, _now, _now));
            var messageId = Guid.NewGuid();
            var sequence = Messages.Count + 1L;
            var callId = $"call-{++_callCount}";
            var run = new ToolExecutionRecord(
                Guid.NewGuid(), _conversationId, toolName, argumentsJson, status, "Plan updated.", callId, 1,
                _now, _now, MessageId: messageId, ResultContent: ResultText);
            Runs.Add(run);
            Messages.Add(new MessageRecord(messageId, _conversationId, turnId, sequence, MessageRole.Assistant,
                string.Empty, MessageStatus.Sealed, _now, _now,
                Segments: [new MessageSegmentRecord(messageId, 0, MessageSegmentKind.ToolCall, null, run.Id)]));
            return run;
        }
    }
}
