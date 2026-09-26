using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptMessageProjectorHookTests
{
    [Fact]
    public void A_notice_segment_projects_as_a_notice_kind()
    {
        var messageId = Guid.NewGuid();
        var message = Message(
            messageId,
            MessageRole.Assistant,
            MessageStatus.Completed,
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Notice, "Hook added context (3 chars).", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Text, "answer", null)
            ]);

        var item = Projector().Build(message, []);

        item.Segments.Should().SatisfyRespectively(
            segment =>
            {
                segment.Kind.Should().Be("notice");
                segment.Markdown.Should().Be("Hook added context (3 chars).");
                segment.SegmentId.Should().Be($"{messageId:D}:notice:0");
            },
            segment => segment.Kind.Should().Be("content"));
    }

    [Fact]
    public void A_blocked_message_exposes_its_error_message()
    {
        var message = Message(Guid.NewGuid(), MessageRole.Assistant, MessageStatus.Blocked, null) with
        {
            ErrorMessage = "Blocked by hook 'alpha/a': no."
        };
        var item = Projector().Build(message, []);

        item.Status.Should().Be("blocked");
        item.ErrorMessage.Should().Be("Blocked by hook 'alpha/a': no.");
    }

    private static TranscriptMessageProjector Projector()
    {
        var root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        return new TranscriptMessageProjector(
            StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets")));
    }

    private static MessageRecord Message(
        Guid messageId,
        MessageRole role,
        MessageStatus status,
        IReadOnlyList<MessageSegmentRecord>? segments)
    {
        var now = DateTimeOffset.UtcNow;
        return new MessageRecord(
            messageId, Guid.NewGuid(), role, "answer", status, now, now, Segments: segments);
    }
}
