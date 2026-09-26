using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationCompletionNotifierTests
{
    [Fact]
    public void A_blocked_turn_reports_the_hook_reason_instead_of_completing()
        => ConversationCompletionNotifier.BuildMessage(
                [Assistant(MessageStatus.Blocked, "Blocked by hook 'alpha/a': no.")])
            .Should().Be("已被插件 hook 阻止：Blocked by hook 'alpha/a': no.");

    [Fact]
    public void A_failed_turn_reports_the_failure_reason()
        => ConversationCompletionNotifier.BuildMessage(
                [Assistant(MessageStatus.Failed, "provider exploded")])
            .Should().Be("会话失败：provider exploded");

    [Fact]
    public void A_completed_turn_keeps_the_preview_text()
        => ConversationCompletionNotifier.BuildMessage(
                [Assistant(MessageStatus.Completed, "all done")])
            .Should().Be("Programming session completed.\nall done");

    private static MessageRecord Assistant(MessageStatus status, string text)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MessageRole.Assistant,
            text,
            status,
            now,
            now,
            ErrorMessage: status == MessageStatus.Completed ? null : text);
    }
}
