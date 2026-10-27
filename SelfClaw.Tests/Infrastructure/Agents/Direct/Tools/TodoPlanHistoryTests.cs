using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct.Tools;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class TodoPlanHistoryTests
{
    [Fact]
    public void FindLatest_only_accepts_completed_todo_writes()
    {
        var history = new HistoryBuilder();
        var message = history.AddAssistantMessage(1);
        history.AddRun(message, "running", ToolExecutionStatus.Running);
        history.AddRun(message, "awaiting approval", ToolExecutionStatus.AwaitingApproval);
        history.AddRun(message, "failed", ToolExecutionStatus.Failed);
        history.AddRun(message, "cancelled", ToolExecutionStatus.Cancelled);
        var completed = history.AddRun(message, "completed");

        var snapshot = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(snapshot);
        snapshot.Run.Id.Should().Be(completed.Id);
        snapshot.SourceSequence.Should().Be(1);
        snapshot.Plan.Items.Should().ContainSingle().Which.Content.Should().Be("completed");
    }

    [Fact]
    public void FindLatest_falls_back_to_the_previous_plan_when_the_newest_call_failed()
    {
        var history = new HistoryBuilder();
        var older = history.AddRun(history.AddAssistantMessage(1), "kept");
        history.AddRun(history.AddAssistantMessage(2), "rejected", ToolExecutionStatus.Failed);
        history.AddRun(history.AddAssistantMessage(3), "cancelled", ToolExecutionStatus.Cancelled);

        var snapshot = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(snapshot);
        snapshot.Run.Id.Should().Be(older.Id);
        snapshot.Plan.Items.Should().ContainSingle().Which.Content.Should().Be("kept");
    }

    [Fact]
    public void FindLatest_skips_a_completed_call_whose_arguments_do_not_parse()
    {
        var history = new HistoryBuilder();
        var older = history.AddRun(history.AddAssistantMessage(1), "kept");
        history.AddRun(history.AddAssistantMessage(2), "ignored", arguments: "{ not json at all");

        var snapshot = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(snapshot);
        snapshot.Run.Id.Should().Be(older.Id);
    }

    [Fact]
    public void FindLatest_orders_calls_by_message_sequence_then_by_segment_ordinal()
    {
        var history = new HistoryBuilder();
        // The same message holds two calls; the second segment wins even though it is listed first here.
        var message = history.AddAssistantMessage(1);
        var second = history.AddRun(message, "second", segmentOrdinal: 2);
        history.AddRun(message, "first", segmentOrdinal: 1);
        var laterMessage = history.AddAssistantMessage(2);
        var later = history.AddRun(laterMessage, "later");

        var snapshot = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(snapshot);
        snapshot.Run.Id.Should().Be(later.Id);
        snapshot.SourceSequence.Should().Be(2);

        // Drop the later message: the higher ordinal inside the first message becomes the newest plan.
        var withoutLater = TodoPlanHistory.FindLatest(
            history.Messages.Where(item => item.Id != laterMessage.Id).ToArray(),
            history.Runs);

        Assert.NotNull(withoutLater);
        withoutLater.Run.Id.Should().Be(second.Id);
    }

    [Fact]
    public void FindLatest_ignores_runs_that_belong_to_a_call_the_message_cannot_place()
    {
        var history = new HistoryBuilder();
        var message = history.AddAssistantMessage(1);
        var placed = history.AddRun(message, "placed", segmentOrdinal: 0);
        // A run whose call segment is gone sorts last inside its message, so it wins only if it is alone.
        history.AddRun(message, "orphan", segmentOrdinal: null);

        var orphanWins = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(orphanWins);
        orphanWins.Plan.Items.Single().Content.Should().Be("orphan");

        var withoutOrphan = TodoPlanHistory.FindLatest(history.Messages,
            history.Runs.Where(run => run.Id != orphanWins.Run.Id).ToArray());

        Assert.NotNull(withoutOrphan);
        withoutOrphan.Run.Id.Should().Be(placed.Id);
    }

    [Fact]
    public void FindLatest_ignores_runs_that_do_not_belong_to_the_given_messages()
    {
        var history = new HistoryBuilder();
        history.AddRun(history.AddAssistantMessage(1), "known");
        var foreignConversation = Guid.NewGuid();
        history.AddRun(
            history.AddAssistantMessage(2),
            "foreign",
            messageId: Guid.NewGuid(),
            conversationId: foreignConversation);

        var snapshot = TodoPlanHistory.FindLatest(history.Messages, history.Runs);

        Assert.NotNull(snapshot);
        snapshot.Plan.Items.Single().Content.Should().Be("known");
    }

    [Fact]
    public void FindLatest_returns_null_without_any_todo_write()
    {
        var history = new HistoryBuilder();
        var message = history.AddAssistantMessage(1);
        history.AddRun(message, "read_file", arguments: """{"relativePath":"README.md"}""");

        TodoPlanHistory.FindLatest(history.Messages, history.Runs).Should().BeNull();
        TodoPlanHistory.FindLatest([], []).Should().BeNull();
    }

    private sealed class HistoryBuilder
    {
        private readonly Guid _conversationId = Guid.NewGuid();
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

        public List<MessageRecord> Messages { get; } = [];

        public List<ToolExecutionRecord> Runs { get; } = [];

        public MessageRecord AddAssistantMessage(long sequence)
        {
            var message = new MessageRecord(Guid.NewGuid(), _conversationId, Guid.NewGuid(), sequence,
                MessageRole.Assistant, string.Empty, MessageStatus.Sealed, _now, _now);
            Messages.Add(message);
            return message;
        }

        public ToolExecutionRecord AddRun(
            MessageRecord message,
            string content,
            ToolExecutionStatus status = ToolExecutionStatus.Completed,
            int? segmentOrdinal = 0,
            Guid? messageId = null,
            Guid? conversationId = null,
            string? arguments = null)
        {
            var run = new ToolExecutionRecord(
                Guid.NewGuid(),
                conversationId ?? _conversationId,
                TodoPlanRules.ToolName,
                arguments ?? Arguments(content),
                status,
                "Updated plan.",
                $"call-{Guid.NewGuid():N}",
                1,
                _now,
                _now,
                MessageId: messageId ?? message.Id);
            Runs.Add(run);
            if (segmentOrdinal is not int ordinal || messageId is not null)
            {
                return run;
            }

            var index = Messages.FindIndex(item => item.Id == message.Id);
            var segments = (Messages[index].Segments ?? []).ToList();
            segments.Add(new MessageSegmentRecord(message.Id, ordinal, MessageSegmentKind.ToolCall, null, run.Id));
            Messages[index] = Messages[index] with { Segments = segments };
            return run;
        }

        private static string Arguments(string content)
            => $$"""{"todos":[{"content":"{{content}}","status":"pending"}]}""";
    }
}
