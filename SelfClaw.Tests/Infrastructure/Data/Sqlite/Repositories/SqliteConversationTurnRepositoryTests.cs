using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteConversationTurnRepositoryTests
{
    [Theory]
    [InlineData(AgentExecutionMode.Direct)]
    [InlineData(AgentExecutionMode.Cli)]
    public async Task Start_persists_independent_running_turn_and_only_its_user(AgentExecutionMode mode)
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync(mode: mode);
        var user = start.Messages.Should().ContainSingle().Subject;
        user.Id.Should().NotBe(start.Turn.Id);
        user.TurnId.Should().Be(start.Turn.Id);
        user.Sequence.Should().Be(1);
        user.Role.Should().Be(MessageRole.User);
        user.Status.Should().Be(MessageStatus.Sealed);
        (await fixture.Turns.ListTurnsAsync(start.Turn.ConversationId)).Should().Equal(start.Turn);
        (await fixture.Conversations.ListMessagesAsync(start.Turn.ConversationId)).Should().Equal(user);
        (await fixture.Conversations.ListToolExecutionsAsync(start.Turn.ConversationId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Start_failure_rolls_back_conversation_turn_user_and_allocator()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.ExecuteAsync("CREATE TRIGGER fail_user BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, 'user fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.StartAsync());
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversations;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_turns;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_message_sequences;")).Should().Be(0L);
    }

    [Fact]
    public async Task Reserved_sequences_are_independent_monotonic_and_allow_gaps()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var sequences = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            fixture.Turns.ReserveMessageSequenceAsync(start.Turn.ConversationId)));
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Order().Should().Equal(Enumerable.Range(2, 20).Select(value => (long)value));
        var message = await fixture.AssistantAsync(start.Turn);
        message.Sequence.Should().Be(22);
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(start.Turn, [message], []));
        (await fixture.Conversations.ListMessagesAsync(start.Turn.ConversationId)).Select(item => item.Sequence).Should().Equal(1, 22);
    }

    [Fact]
    public async Task Progress_commits_actual_fragment_tool_and_call_atomically_then_finalizes_once()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn, "partial");
        var tool = CreateTool(message);
        message = WithTool(message, tool);
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(start.Turn, [message], [tool]));
        (await fixture.Conversations.ListToolExecutionsAsync(message.ConversationId)).Should().Equal(tool);
        var terminal = new ConversationTurnCommit(start.Turn with
        {
            Status = ConversationTurnStatus.Cancelled, CompletedAtUtc = DateTimeOffset.UtcNow, ErrorMessage = "stopped"
        }, [message with { Status = MessageStatus.Interrupted }], [tool with { Status = ToolExecutionStatus.Cancelled }]);
        (await fixture.Turns.TryFinalizeTurnAsync(terminal)).Should().BeTrue();
        (await fixture.Turns.TryFinalizeTurnAsync(terminal with
        {
            Turn = terminal.Turn with { Status = ConversationTurnStatus.Failed, ErrorMessage = "late" }
        })).Should().BeFalse();
        (await fixture.Turns.ListTurnsAsync(message.ConversationId)).Should().ContainSingle().Which.Status.Should().Be(ConversationTurnStatus.Cancelled);
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Last().Status.Should().Be(MessageStatus.Interrupted);
        (await fixture.Conversations.ListToolExecutionsAsync(message.ConversationId)).Should().ContainSingle().Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task Progress_fault_leaves_no_orphan_fragment_tool_or_usage()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn);
        var tool = CreateTool(message);
        message = WithTool(message, tool);
        await fixture.ExecuteAsync("CREATE TRIGGER fail_tool BEFORE INSERT ON tool_runs BEGIN SELECT RAISE(ABORT, 'tool fault'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(
            start.Turn with { Usage = new TurnUsage(InputTokens: 12) }, [message], [tool])));
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        (await fixture.Conversations.ListToolExecutionsAsync(message.ConversationId)).Should().BeEmpty();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM turn_usage;")).Should().Be(0L);
    }

    [Fact]
    public async Task Finalization_fault_restores_running_turn_and_streaming_fragment()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn, "partial");
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(start.Turn, [message], []));
        await fixture.ExecuteAsync("CREATE TRIGGER fail_usage BEFORE INSERT ON turn_usage BEGIN SELECT RAISE(ABORT, 'usage fault'); END;");
        var finalization = new ConversationTurnCommit(start.Turn with
        {
            Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow, Usage = new TurnUsage(OutputTokens: 7)
        }, [message with { MarkdownContent = "final", Status = MessageStatus.Sealed }], []);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Turns.TryFinalizeTurnAsync(finalization));
        (await fixture.Turns.ListTurnsAsync(message.ConversationId)).Should().Equal(start.Turn);
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Last().MarkdownContent.Should().Be("partial");
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Last().Status.Should().Be(MessageStatus.Streaming);
    }

    [Theory]
    [InlineData(ConversationTurnStatus.Failed)]
    [InlineData(ConversationTurnStatus.Blocked)]
    [InlineData(ConversationTurnStatus.Truncated)]
    [InlineData(ConversationTurnStatus.Cancelled)]
    [InlineData(ConversationTurnStatus.Interrupted)]
    public async Task No_output_terminal_persists_turn_usage_with_unknown_total_and_no_assistant(ConversationTurnStatus status)
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var terminal = start.Turn with
        {
            Status = status, CompletedAtUtc = DateTimeOffset.UtcNow, ErrorMessage = "terminal",
            Usage = new TurnUsage(InputTokens: 14, OutputTokens: 3, TotalTokens: null)
        };
        (await fixture.Turns.TryFinalizeTurnAsync(new ConversationTurnCommit(terminal, [], []))).Should().BeTrue();
        (await fixture.Conversations.ListMessagesAsync(terminal.ConversationId)).Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        var loaded = (await fixture.Turns.ListTurnsAsync(terminal.ConversationId)).Single();
        loaded.Should().Be(terminal);
        loaded.Usage.Should().NotBeNull();
        loaded.Usage?.TotalTokens.Should().BeNull();
        (await fixture.ScalarAsync("SELECT total_tokens IS NULL FROM turn_usage;")).Should().Be(1L);
    }

    [Fact]
    public async Task Wrong_ownership_and_missing_ToolCall_reject_without_changing_turn()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(
            start.Turn, [message with { TurnId = Guid.NewGuid() }], [])));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(
            start.Turn, [message], [CreateTool(message)])));
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Tool_hook_outcome_survives_atomic_progress_and_completion()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn);
        var tool = CreateTool(message);
        message = WithTool(message, tool);
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(start.Turn, [message], [tool]));
        var source = new HookSource("guard", "check");
        var completed = tool with
        {
            Status = ToolExecutionStatus.Failed,
            HookOutcome = new ToolHookOutcome("{\"command\":\"ls\"}", [source], [source], source, "blocked",
                [new HookFeedback(source, "feedback")], [new HookFailureNotice(source, "timedOut", "timeout")])
        };
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(start.Turn, [message], [completed]));
        var loaded = (await fixture.Conversations.ListToolExecutionsAsync(message.ConversationId)).Single();
        loaded.HookOutcome.Should().BeEquivalentTo(completed.HookOutcome);
    }

    private static ToolExecutionRecord CreateTool(MessageRecord message)
        => new(Guid.NewGuid(), message.ConversationId, "read_file", "{}", ToolExecutionStatus.Running,
            null, "call-1", null, message.CreatedAtUtc, message.UpdatedAtUtc, MessageId: message.Id);

    private static MessageRecord WithTool(MessageRecord message, ToolExecutionRecord tool)
        => message with { Segments = [new MessageSegmentRecord(message.Id, 0, MessageSegmentKind.Text, message.MarkdownContent, null),
            new MessageSegmentRecord(message.Id, 1, MessageSegmentKind.ToolCall, null, tool.Id)] };
}
