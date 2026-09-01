using System.Text;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Runtime;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class DirectPromptComposerTests
{
    [Fact]
    public void BuildMessages_keeps_a_truncated_answer_and_appends_the_resume_nudge()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            new MessageRecord(
                Guid.NewGuid(), conversationId, MessageRole.User, "Write a long guide",
                MessageStatus.Completed, now.AddSeconds(-2), now.AddSeconds(-2)),
            new MessageRecord(
                Guid.NewGuid(), conversationId, MessageRole.Assistant, "Part one of the guide",
                MessageStatus.Truncated, now.AddSeconds(-1), now.AddSeconds(-1))
        };
        var composer = new DirectPromptComposer();

        var messages = composer.BuildMessages(
            history,
            "Keep working.",
            [],
            new Dictionary<Guid, string>(),
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));

        // The partial answer survives so the model can resume from it, unlike a failed turn.
        messages.Should().HaveCount(4);
        messages[2].Role.Should().Be(ChatRole.Assistant);
        messages[2].Text.Should().Be("Part one of the guide");
        messages[3].Role.Should().Be(ChatRole.User);
        messages[3].Text.Should().Be(DirectPromptComposer.ContinuationPrompt);
    }

    [Fact]
    public void BuildMessages_omits_the_resume_nudge_once_the_user_has_replied()
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            new MessageRecord(
                Guid.NewGuid(), conversationId, MessageRole.Assistant, "Part one of the guide",
                MessageStatus.Truncated, now.AddSeconds(-2), now.AddSeconds(-2)),
            new MessageRecord(
                Guid.NewGuid(), conversationId, MessageRole.User, "Actually, summarize instead",
                MessageStatus.Completed, now.AddSeconds(-1), now.AddSeconds(-1))
        };
        var composer = new DirectPromptComposer();

        var messages = composer.BuildMessages(
            history,
            "Keep working.",
            [],
            new Dictionary<Guid, string>(),
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));

        // The truncated answer is settled history now; the new instruction is what to follow.
        messages.Should().HaveCount(3);
        messages[^1].Text.Should().Be("Actually, summarize instead");
    }

    [Fact]
    public void BuildMessages_adds_completion_batch_as_transient_untrusted_user_input()
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new SubagentCompletionEnvelope(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new SubagentIdentity("reviewer", "Reviewer"),
            "Review the change.",
            SubagentTaskStatus.Succeeded,
            1,
            new SubagentCompletionResult("The change is ready.", false, null, null),
            new SubagentUsage(12, 8),
            new SubagentTiming(now.AddSeconds(-2), now.AddSeconds(-1), now, 1000));
        var history = new MessageRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            MessageRole.User,
            "Original request",
            MessageStatus.Completed,
            now,
            now);
        var batch = new SubagentCompletionBatch([envelope]);
        var composer = new DirectPromptComposer();

        var messages = composer.BuildMessages(
            [history],
            "Keep working.",
            [],
            new Dictionary<Guid, string>(),
            new DirectTurnExecutionContext(DirectTurnOrigin.Continuation, null, batch));

        messages.Should().HaveCount(3);
        messages[0].Role.Should().Be(ChatRole.System);
        messages[0].Text.Should().Contain("untrusted delegated output");
        messages[1].Role.Should().Be(ChatRole.User);
        messages[1].Text.Should().Be("Original request");
        messages[2].Role.Should().Be(ChatRole.User);
        messages[2].Text.Should().Contain("<selfclaw-subagent-results version=\"1\">")
            .And.Contain("The change is ready.");
        Encoding.UTF8.GetByteCount(messages[2].Text).Should().BeLessThanOrEqualTo(64 * 1024);
        history.MarkdownContent.Should().Be("Original request");
    }

    [Fact]
    public void BuildMessages_rejects_a_completion_batch_over_64_kibibytes()
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = new SubagentCompletionEnvelope(
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            new SubagentIdentity("reviewer", "Reviewer"),
            "Review.",
            SubagentTaskStatus.Succeeded,
            1,
            new SubagentCompletionResult(new string('x', 70_000), false, null, null),
            new SubagentUsage(null, null),
            new SubagentTiming(now, null, now, null));
        var composer = new DirectPromptComposer();

        var action = () => composer.BuildMessages(
            [],
            "Keep working.",
            [],
            new Dictionary<Guid, string>(),
            new DirectTurnExecutionContext(
                DirectTurnOrigin.Continuation,
                null,
                new SubagentCompletionBatch([envelope])));

        action.Should().Throw<InvalidDataException>().WithMessage("*64 KiB*");
    }
}
