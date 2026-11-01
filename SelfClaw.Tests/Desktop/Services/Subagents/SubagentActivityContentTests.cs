using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentActivityContentTests
{
    [Fact]
    public void A_no_output_terminal_is_complete_and_turn_only_changes_invalidate_the_content_version()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, ConversationTurnStatus.Running, now);
        var running = SubagentActivityContent.Create(SubagentTaskStatus.Running, "task", turn, null, []);
        var failed = SubagentActivityContent.Create(SubagentTaskStatus.Failed, "task", turn with
        {
            Status = ConversationTurnStatus.Failed,
            CompletedAtUtc = now,
            ErrorMessage = "No output",
            Usage = new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: null)
        }, null, []);

        running.Message.Should().BeNull();
        failed.Message.Should().BeNull();
        failed.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Complete);
        failed.ContentVersion.Should().NotBe(running.ContentVersion);
        failed.Turn?.Usage?.TotalTokens.Should().BeNull();
    }

    [Fact]
    public void Tools_must_reference_the_actual_assistant_not_the_logical_turn()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, ConversationTurnStatus.Running, now);
        var message = new MessageRecord(Guid.NewGuid(), turn.ConversationId, turn.Id, 1,
            MessageRole.Assistant, "output", MessageStatus.Streaming, now, now);
        var tool = new ToolExecutionRecord(Guid.NewGuid(), turn.ConversationId, "read_file", "{}",
            ToolExecutionStatus.Running, null, null, null, now, now, MessageId: turn.Id);

        var create = () => SubagentActivityContent.Create(SubagentTaskStatus.Running, "task", turn, message, [tool]);

        create.Should().Throw<ArgumentException>().WithParameterName("tools");
    }
}
