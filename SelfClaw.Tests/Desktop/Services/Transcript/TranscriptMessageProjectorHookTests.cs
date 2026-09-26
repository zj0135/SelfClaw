using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
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

    [Fact]
    public void A_tool_hook_outcome_projects_into_the_tool_segment()
    {
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            messageId,
            conversationId,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Completed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.ToolCall, null, toolRunId)
            ]);
        var modifier = new HookSource("shell-guard", "rewrite");
        var blockedBy = new HookSource("shell-guard", "deny-dangerous");
        var toolRun = new ToolExecutionRecord(
            toolRunId,
            conversationId,
            "run_shell_command",
            "{\"command\":\"rm -rf .\"}",
            ToolExecutionStatus.Blocked,
            "Blocked by hook 'shell-guard/deny-dangerous': no.",
            "call-1",
            5,
            now,
            now,
            MessageId: messageId,
            ResultContent: "Blocked by hook 'shell-guard/deny-dangerous': no.",
            HookOutcome: new ToolHookOutcome(
                "{\"command\":\"ls\"}",
                [modifier],
                [],
                blockedBy,
                "no.",
                [new HookFeedback(modifier, "Rewritten.")],
                [new HookFailureNotice(modifier, "timedOut", "too slow")]));

        var item = Projector().Build(message, [toolRun]);

        var hook = item.Segments.Single().Hook;
        hook.Should().NotBeNull();
        hook!.BlockedBy.Should().Be("shell-guard/deny-dangerous");
        hook.BlockReason.Should().Be("no.");
        hook.ArgumentsModifiedBy.Should().Equal("shell-guard/rewrite");
        hook.EffectiveArgumentsText.Should().Contain("ls");
        hook.OriginalArgumentsText.Should().Contain("rm -rf .");
        hook.Feedback.Should().ContainSingle()
            .Which.Should().Be(new SelfClaw.Desktop.Services.Transcript.Views.TranscriptHookNoteView("shell-guard/rewrite", "Rewritten."));
        hook.IgnoredFailures.Should().ContainSingle()
            .Which.Text.Should().Be("timedOut: too slow");
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
