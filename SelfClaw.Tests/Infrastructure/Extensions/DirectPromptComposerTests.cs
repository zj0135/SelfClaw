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
