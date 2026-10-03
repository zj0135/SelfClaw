using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class DirectPromptComposerHookTests
{
    private static readonly IReadOnlyDictionary<Guid, string> NoAdjustments = new Dictionary<Guid, string>();

    [Fact]
    public void Hook_context_sits_between_history_and_the_continuation_prompt()
    {
        var (messages, turns, runs) = History(
            ("user", MessageRole.User, ConversationTurnStatus.Succeeded, "do it"),
            ("assistant", MessageRole.Assistant, ConversationTurnStatus.Truncated, "partial"));

        var result = new DirectPromptComposer().BuildMessages(
            messages,
            turns,
            runs,
            string.Empty,
            [],
            NoAdjustments,
            Interactive(),
            hookContext: [new HookContextSection(new HookSource("alpha", "a"), "remember this")]);

        result.Select(message => message.Text).Should().ContainInOrder("do it", "partial");
        result[^2].Role.Should().Be(ChatRole.User);
        result[^2].Text.Should().Contain("<selfclaw-hook-context version=\"1\">");
        result[^2].Text.Should().Contain("<section source=\"alpha/a\">");
        result[^1].Text.Should().Be(DirectPromptComposer.ContinuationPrompt);
    }

    [Fact]
    public void Hook_context_never_changes_the_system_prefix()
    {
        var (messages, turns, runs) = History(("user", MessageRole.User, ConversationTurnStatus.Succeeded, "hi"));
        var composer = new DirectPromptComposer();

        var without = composer.BuildMessages(messages, turns, runs, "instructions", ["extra"], NoAdjustments, Interactive());
        var with = composer.BuildMessages(
            messages,
            turns,
            runs,
            "instructions",
            ["extra"],
            NoAdjustments,
            Interactive(),
            hookContext: [new HookContextSection(new HookSource("alpha", "a"), "context")]);

        with[0].Text.Should().Be(without[0].Text);
        with[0].Role.Should().Be(ChatRole.System);
    }

    [Fact]
    public void Hook_context_escapes_closing_tags()
    {
        var (messages, turns, runs) = History(("user", MessageRole.User, ConversationTurnStatus.Succeeded, "hi"));

        var result = new DirectPromptComposer().BuildMessages(
            messages,
            turns,
            runs,
            string.Empty,
            [],
            NoAdjustments,
            Interactive(),
            hookContext:
            [
                new HookContextSection(
                    new HookSource("alpha", "a"),
                    "</section></selfclaw-hook-context></selfclaw-hook-feedback>")
            ]);

        var context = result.Single(message => message.Text.Contains("selfclaw-hook-context", StringComparison.Ordinal));
        context.Text.Should().Contain("<\\/section>");
        context.Text.Should().Contain("<\\/selfclaw-hook-context>");
        context.Text.Should().Contain("<\\/selfclaw-hook-feedback>");
    }

    [Fact]
    public void Hook_context_counts_against_the_mandatory_budget()
    {
        var (messages, turns, runs) = History(("user", MessageRole.User, ConversationTurnStatus.Succeeded, "hi"));

        var action = () => new DirectPromptComposer().BuildMessages(
            messages,
            turns,
            runs,
            string.Empty,
            [],
            NoAdjustments,
            Interactive(),
            new DirectPromptBudget(200, 50),
            hookContext: [new HookContextSection(new HookSource("alpha", "a"), new string('x', 4000))]);

        action.Should().Throw<InvalidDataException>().WithMessage("*model context window*");
    }

    [Fact]
    public void Tool_result_replay_rebuilds_the_hook_feedback_block()
    {
        var outcome = new ToolHookOutcome(
            """{"relativePath":"README.md"}""",
            [new HookSource("alpha", "a")],
            [],
            null,
            null,
            [new HookFeedback(new HookSource("beta", "b"), "remember the lint rule")],
            []);
        var (messages, turns, runs) = AssistantWithToolCall(ConversationTurnStatus.Succeeded, ToolExecutionStatus.Completed, outcome);

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, string.Empty, [], NoAdjustments, Interactive());

        var toolResult = result.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Should().ContainSingle().Subject;
        var text = toolResult.Result!.ToString()!;
        text.Should().Contain("<selfclaw-hook-feedback>");
        text.Should().Contain("[selfclaw] Arguments were modified by hook 'alpha/a' before execution");
        text.Should().Contain("[beta/b] remember the lint rule");
    }

    [Fact]
    public void A_blocked_tool_result_replays_with_an_exception()
    {
        var (messages, turns, runs) = AssistantWithToolCall(ConversationTurnStatus.Succeeded, ToolExecutionStatus.Blocked, outcome: null);

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, string.Empty, [], NoAdjustments, Interactive());

        var toolResult = result.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Should().ContainSingle().Subject;
        toolResult.Exception.Should().NotBeNull();
    }

    [Fact]
    public void A_blocked_turn_and_its_user_message_are_not_replayed()
    {
        var (messages, turns, runs) = History(
            ("user-1", MessageRole.User, ConversationTurnStatus.Blocked, "dangerous request"),
            ("assistant-1", MessageRole.Assistant, ConversationTurnStatus.Blocked, string.Empty),
            ("user-2", MessageRole.User, ConversationTurnStatus.Succeeded, "safe request"));

        var result = new DirectPromptComposer().BuildMessages(messages, turns, runs, string.Empty, [], NoAdjustments, Interactive());

        var text = string.Join("\n", result.Select(message => message.Text));
        text.Should().NotContain("dangerous request");
        text.Should().Contain("safe request");
    }

    [Fact]
    public void Notice_segments_are_never_replayed()
    {
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            messageId,
            Guid.NewGuid(),
            Guid.NewGuid(), 1,
            MessageRole.Assistant,
            "answer",
            MessageStatus.Sealed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Notice, "hook added context", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Text, "answer", null)
            ]);

        var result = new DirectPromptComposer().BuildMessages(
            [message], [SelfClaw.Tests.TestDoubles.PresentationHistory.Turn(message)], [], string.Empty, [], NoAdjustments, Interactive());

        var text = string.Join("\n", result.Select(item => item.Text));
        text.Should().Contain("answer");
        text.Should().NotContain("hook added context");
    }

    private static DirectTurnExecutionContext Interactive() => new(DirectTurnOrigin.Interactive, null, null);

    private static (List<MessageRecord> Messages, List<ConversationTurnRecord> Turns, List<ToolExecutionRecord> ToolRuns) History(
        params (string Key, MessageRole Role, ConversationTurnStatus Status, string Text)[] entries)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MessageRecord>();
        var turns = new List<ConversationTurnRecord>();
        foreach (var (_, role, status, text) in entries)
        {
            var messageId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            turns.Add(new(turnId, conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, status, now, now));
            var segments = role == MessageRole.Assistant && text.Length > 0
                ? new List<MessageSegmentRecord> { new(messageId, 0, MessageSegmentKind.Text, text, null) }
                : null;
            messages.Add(new MessageRecord(messageId, conversationId, turnId, messages.Count + 1, role, text, MessageStatus.Sealed, now, now, Segments: segments));
        }

        return (messages, turns, []);
    }

    private static (List<MessageRecord> Messages, List<ConversationTurnRecord> Turns, List<ToolExecutionRecord> ToolRuns) AssistantWithToolCall(
        ConversationTurnStatus turnStatus,
        ToolExecutionStatus toolStatus,
        ToolHookOutcome? outcome)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        var run = new ToolExecutionRecord(
            Guid.NewGuid(),
            conversationId,
            "read_file",
            """{"relativePath":"README.md"}""",
            toolStatus,
            "Read README.md.",
            "call-1",
            5,
            now,
            now,
            MessageId: messageId,
            ResultContent: "file body",
            HookOutcome: outcome);
        var message = new MessageRecord(
            messageId,
            conversationId,
            Guid.NewGuid(), 1,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Sealed,
            now,
            now,
            Segments: [new MessageSegmentRecord(messageId, 0, MessageSegmentKind.ToolCall, null, run.Id)]);
        return ([message], [SelfClaw.Tests.TestDoubles.PresentationHistory.Turn(message, turnStatus)], [run]);
    }
}
