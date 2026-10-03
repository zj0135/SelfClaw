using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationCompletionNotifierTests
{
    [Theory]
    [InlineData(ConversationTurnStatus.Blocked, "回合已被阻止")]
    [InlineData(ConversationTurnStatus.Failed, "会话失败")]
    [InlineData(ConversationTurnStatus.Cancelled, "会话已取消")]
    [InlineData(ConversationTurnStatus.Truncated, "回答已截断")]
    [InlineData(ConversationTurnStatus.Interrupted, "会话已中断")]
    public void A_terminal_turn_without_output_reports_its_own_reason(ConversationTurnStatus status, string headline)
    {
        var turn = Turn(status) with { ErrorMessage = "current reason" };
        var older = Assistant(turn with { Id = Guid.NewGuid() }, 1, "old answer");
        ConversationCompletionNotifier.BuildMessage(turn, [older])
            .Should().Be($"{headline}：current reason");
    }

    [Fact]
    public void A_successful_no_output_turn_never_leaks_an_older_preview()
    {
        var turn = Turn(ConversationTurnStatus.Succeeded);
        var older = Assistant(turn with { Id = Guid.NewGuid() }, 1, "old answer");
        ConversationCompletionNotifier.BuildMessage(turn, [older]).Should().Be("Programming session completed.");
    }

    [Fact]
    public void Preview_uses_the_last_nonempty_sealed_output_by_sequence_in_the_exact_turn()
    {
        var turn = Turn(ConversationTurnStatus.Succeeded);
        var first = Assistant(turn, 1, "first");
        var last = Assistant(turn, 3, "**last**");
        var empty = Assistant(turn, 4, "");
        var unrelated = Assistant(turn with { Id = Guid.NewGuid() }, 5, "unrelated");
        ConversationCompletionNotifier.BuildMessage(turn, [empty, last, unrelated, first])
            .Should().Be("Programming session completed.\nlast");
    }

    [Fact]
    public void A_running_turn_cannot_generate_a_completion_message()
    {
        var action = () => ConversationCompletionNotifier.BuildMessage(Turn(ConversationTurnStatus.Running), []);
        action.Should().Throw<ArgumentException>();
    }

    private static ConversationTurnRecord Turn(ConversationTurnStatus status)
        => new(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct, DirectTurnOrigin.Interactive,
            status, DateTimeOffset.UtcNow);

    private static MessageRecord Assistant(ConversationTurnRecord turn, long sequence, string text)
        => new(Guid.NewGuid(), turn.ConversationId, turn.Id, sequence, MessageRole.Assistant, text,
            MessageStatus.Sealed, turn.StartedAtUtc, turn.StartedAtUtc);
}
