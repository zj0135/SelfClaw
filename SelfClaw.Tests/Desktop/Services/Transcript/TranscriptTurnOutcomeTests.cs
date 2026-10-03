using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Views;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptTurnOutcomeTests
{
    [Fact]
    public void No_output_turn_projects_a_distinct_outcome_without_an_assistant_message()
    {
        var turn = Turn(ConversationTurnStatus.Failed) with { ErrorMessage = "no response", Usage = new(InputTokens: 3, OutputTokens: 4) };
        var user = Message(turn, 1, MessageRole.User, "question");
        var state = Projection().Build(Request([user], [turn])) ?? throw new InvalidOperationException();
        state.Items.Should().HaveCount(2);
        state.Items.Should().NotContain(item => item.Role == "assistant");
        var outcome = state.Items[1];
        outcome.Id.Should().Be($"turn-outcome:{turn.Id:D}");
        outcome.Kind.Should().Be("turn-outcome");
        outcome.Segments.Should().BeEmpty();
        outcome.TurnOutcome?.ErrorMessage.Should().Be("no response");
        outcome.TurnOutcome?.Usage?.TotalTokens.Should().BeNull();
    }

    [Fact]
    public void Turn_only_changes_invalidate_outcome_cache_but_not_the_message_truth()
    {
        var turn = Turn(ConversationTurnStatus.Running);
        var assistant = Message(turn, 1, MessageRole.Assistant, "sealed output");
        var projection = Projection();
        var request = Request([assistant], [turn]);
        var before = projection.Build(request) ?? throw new InvalidOperationException();
        var completed = turn with { Status = ConversationTurnStatus.Failed, ErrorMessage = "later failure", CompletedAtUtc = turn.StartedAtUtc.AddSeconds(2), Usage = new(TotalTokens: 7) };
        var after = projection.Build(request with { Turns = [completed] }) ?? throw new InvalidOperationException();
        after.Items.Single().Should().NotBeSameAs(before.Items.Single());
        after.Items.Single().Status.Should().Be("sealed");
        after.Items.Single().TurnOutcome?.Status.Should().Be("failed");
        after.Items.Single().TurnOutcome?.DurationMs.Should().Be(2000);
        after.Items.Single().TurnOutcome?.Usage?.TotalTokens.Should().Be(7);
        projection.Build(request with { Turns = [completed] }).Should().BeNull();
    }

    [Fact]
    public void Footer_moves_to_the_last_actual_assistant_and_sequence_controls_interleaving()
    {
        var turn = Turn(ConversationTurnStatus.Running);
        var user0 = Message(turn, 1, MessageRole.User, "U0");
        var assistant0 = Message(turn, 2, MessageRole.Assistant, "A0");
        var user1 = Message(turn, 3, MessageRole.User, "U1");
        var assistant1 = Message(turn, 4, MessageRole.Assistant, "A1");
        var projection = Projection();
        var before = projection.Build(Request([assistant0, user0], [turn])) ?? throw new InvalidOperationException();
        before.Items.Last().TurnOutcome.Should().NotBeNull();
        var after = projection.Build(Request([assistant1, user1, user0, assistant0], [turn])) ?? throw new InvalidOperationException();
        after.Items.Select(item => item.Id).Should().Equal(user0.Id.ToString("D"), assistant0.Id.ToString("D"), user1.Id.ToString("D"), assistant1.Id.ToString("D"));
        after.Items.Take(3).Should().OnlyContain(item => item.TurnOutcome == null);
        after.Items.Last().TurnOutcome?.TurnId.Should().Be(turn.Id.ToString("D"));
    }

    [Fact]
    public void Adding_first_output_removes_the_outcome_only_item_without_creating_a_placeholder()
    {
        var turn = Turn(ConversationTurnStatus.Running);
        var projection = Projection();
        var before = projection.Build(Request([], [turn])) ?? throw new InvalidOperationException();
        before.Items.Single().Kind.Should().Be("turn-outcome");
        var message = Message(turn, 1, MessageRole.Assistant, "actual");
        var after = projection.Build(Request([message], [turn])) ?? throw new InvalidOperationException();
        after.Items.Single().Id.Should().Be(message.Id.ToString("D"));
        after.Items.Single().Kind.Should().Be("message");
    }

    private static ConversationTurnRecord Turn(ConversationTurnStatus status)
        => new(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, status, DateTimeOffset.UtcNow);

    private static MessageRecord Message(ConversationTurnRecord turn, long sequence, MessageRole role, string text)
    {
        var id = Guid.NewGuid();
        return new(id, turn.ConversationId, turn.Id, sequence, role, text, MessageStatus.Sealed, turn.StartedAtUtc, turn.StartedAtUtc,
            Segments: role == MessageRole.Assistant ? [new(id, 0, MessageSegmentKind.Text, text, null)] : null);
    }

    private static TranscriptProjectionRequest Request(IReadOnlyList<MessageRecord> messages, IReadOnlyList<ConversationTurnRecord> turns)
        => new(messages, turns, [], [], [], turns.FirstOrDefault()?.ConversationId, false, false, null, "direct", "build", "Build", 0, "requireApproval");

    private static TranscriptProjection Projection() => new(StoragePathDefaults.CreateDefault());
}
