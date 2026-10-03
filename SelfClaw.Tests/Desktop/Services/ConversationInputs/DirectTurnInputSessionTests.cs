using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

public sealed class DirectTurnInputSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ordered_sqlite_consumption_supports_early_and_late_waiter_registration(bool early)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1");
        await context.ApplyAsync(new AssistantTextDeltaEvent("a", "a"));
        await context.ApplyAsync(new AssistantTextDeltaEvent("b", "b"));
        await context.InputSession.ReadBoundaryAsync();
        var waiter = early ? context.InputSession.WaitForCommitAsync(batch) : null;
        if (waiter is not null) waiter.IsCompleted.Should().BeFalse();
        await context.ApplyAsync(new TurnInputBoundaryEvent(batch, null));
        waiter ??= context.InputSession.WaitForCommitAsync(batch);
        var consumed = await waiter.WaitAsync(Timeout);
        consumed.Messages.Should().ContainSingle().Which.MarkdownContent.Should().Be("U1");
        var stored = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        stored.Select(message => message.MarkdownContent).Should().Equal("U0", "ab", "U1");
        stored[1].Status.Should().Be(MessageStatus.Sealed);
        stored.All(message => message.Id != context.Record.Id && message.TurnId == context.Record.Id).Should().BeTrue();
        context.InputSession.PendingWaiterCount.Should().Be(0);
    }

    [Theory]
    [InlineData("failure", true)]
    [InlineData("failure", false)]
    [InlineData("cancel", true)]
    [InlineData("cancel", false)]
    [InlineData("close", true)]
    [InlineData("close", false)]
    public async Task Terminal_session_state_releases_early_and_late_waiters(string terminal, bool early)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1");
        var waiter = early ? context.InputSession.WaitForCommitAsync(batch) : null;
        using var cancellation = new CancellationTokenSource();
        switch (terminal)
        {
            case "failure": context.InputSession.Fail(new IOException("commit failed")); break;
            case "cancel": cancellation.Cancel(); context.InputSession.Cancel(cancellation.Token); break;
            default: context.InputSession.Close(); break;
        }
        waiter ??= context.InputSession.WaitForCommitAsync(batch);
        var error = await Record.ExceptionAsync(() => waiter.WaitAsync(Timeout));
        if (terminal == "failure") error.Should().BeOfType<IOException>().Which.Message.Should().Be("commit failed");
        else if (terminal == "cancel") error.Should().BeAssignableTo<OperationCanceledException>();
        else error.Should().BeOfType<InvalidOperationException>();
        context.InputSession.PendingWaiterCount.Should().Be(0);
        (await context.ScalarAsync("SELECT status FROM conversation_inputs")).Should().Be(1L);
    }

    [Fact]
    public async Task A_second_distinct_boundary_is_rejected_and_cancellation_closes_the_first()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1");
        using var cancellation = new CancellationTokenSource();
        var first = context.InputSession.WaitForCommitAsync(batch, cancellation.Token);
        context.InputSession.PendingWaiterCount.Should().Be(1);
        await FluentActions.Awaiting(() => context.InputSession.WaitForCommitAsync(batch with { ClaimId = Guid.NewGuid() }))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*one pending boundary waiter*");
        cancellation.Cancel();
        await FluentActions.Awaiting(() => first.WaitAsync(Timeout)).Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => context.InputSession.WaitForCommitAsync(batch).WaitAsync(Timeout))
            .Should().ThrowAsync<OperationCanceledException>();
        context.InputSession.PendingWaiterCount.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_ack_is_reconfirmed_from_consumption_without_repeating_any_write(bool early)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1", "U2");
        await context.InputSession.ReadBoundaryAsync();
        var first = early ? context.InputSession.WaitForCommitAsync(batch) : null;
        if (first is not null) first.IsCompleted.Should().BeFalse();
        var content = new ConversationTurnCommit(context.Record with
        { Usage = new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: null) }, [], []);
        var committed = await context.Inputs.CommitBoundaryAsync(new ConversationInputBoundaryCommit(batch, content));
        first ??= context.InputSession.WaitForCommitAsync(batch);
        first.IsCompleted.Should().BeFalse("the first wait requires recorder acknowledgement after applying committed memory state");
        var repeated = await context.InputSession.WaitForCommitAsync(batch).WaitAsync(Timeout);
        (await first.WaitAsync(Timeout)).Should().BeEquivalentTo(committed);
        repeated.Should().BeEquivalentTo(committed);
        var again = await context.Inputs.CommitBoundaryAsync(new ConversationInputBoundaryCommit(batch, content));
        again.Should().BeEquivalentTo(committed);
        (await context.InputSession.ReadBoundaryAsync()).Should().BeNull();
        (await context.InputSession.WaitForCommitAsync(batch).WaitAsync(Timeout)).Should().BeEquivalentTo(committed,
            "late reconfirmation still reads the receipt after the producer cleared its previous slot");
        (await context.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(3L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(1L);
        (await context.ScalarAsync("SELECT total_tokens FROM turn_usage")).Should().Be(DBNull.Value);
        context.InputSession.Close();
        await FluentActions.Awaiting(() => context.InputSession.WaitForCommitAsync(batch))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*closed*");
    }

    [Fact]
    public async Task Failed_boundary_transaction_rolls_back_and_releases_the_producer()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1", "U2");
        await context.ApplyAsync(new AssistantTextDeltaEvent("a", "uncommitted output"));
        var sequence = context.State.Messages.Last().Sequence;
        var waiter = context.InputSession.WaitForCommitAsync(batch);
        await context.ExecuteSqlAsync("""
            CREATE TRIGGER reject_consumption BEFORE UPDATE OF status ON conversation_inputs
            WHEN NEW.status = 2 BEGIN SELECT RAISE(ABORT, 'fixture consumption failure'); END;
            """);
        await FluentActions.Awaiting(() => context.ApplyAsync(new TurnInputBoundaryEvent(batch,
            new TurnUsage(InputTokens: 5, OutputTokens: 6, TotalTokens: 11)))).Should().ThrowAsync<Exception>();
        var failure = await Record.ExceptionAsync(() => waiter.WaitAsync(Timeout));
        failure.Should().NotBeNull().And.NotBeOfType<TimeoutException>();
        (await context.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(1L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(0L);
        (await context.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1")).Should().Be(2L);
        context.State.Messages.Last().Sequence.Should().Be(sequence);
        context.State.Messages.Last().Status.Should().Be(MessageStatus.Streaming);
        context.InputSession.PendingWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task Multiple_boundaries_before_output_never_create_or_move_an_assistant()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var initialSequence = context.State.Messages.Single().Sequence;
        foreach (var prompt in new[] { "U1", "U2" })
        {
            var batch = await context.SeedClaimAsync(prompt);
            await context.InputSession.ReadBoundaryAsync();
            await context.ApplyAsync(new TurnInputBoundaryEvent(batch, null));
            await context.InputSession.WaitForCommitAsync(batch);
        }
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Failed, null, "no output"));
        var messages = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        messages.Select(message => message.MarkdownContent).Should().Equal("U0", "U1", "U2");
        messages.Should().OnlyContain(message => message.Role == MessageRole.User);
        messages.Select(message => message.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        messages[0].Sequence.Should().Be(initialSequence);
        (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single().Status.Should().Be(ConversationTurnStatus.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(30)]
    public async Task Boundary_snapshots_and_final_usage_overwrite_one_turn_row_without_accumulating(int? total)
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        for (var boundary = 1; boundary <= 2; boundary++)
        {
            await context.ApplyAsync(new AssistantTextDeltaEvent("a", $"A{boundary}"));
            var batch = await context.SeedClaimAsync($"U{boundary}");
            await context.InputSession.ReadBoundaryAsync();
            await context.ApplyAsync(new TurnInputBoundaryEvent(batch,
                new TurnUsage(InputTokens: boundary * 7, OutputTokens: boundary * 3,
                    TotalTokens: total is null ? null : boundary * 10, ProviderCalls: boundary)));
            await context.InputSession.WaitForCommitAsync(batch);
        }
        await context.ApplyAsync(new UsageReportedEvent(new TurnUsage(InputTokens: 21, OutputTokens: 9, TotalTokens: total, ProviderCalls: 3)));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Failed, null, "after sealed output"));
        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Usage?.InputTokens.Should().Be(21);
        turn.Usage?.OutputTokens.Should().Be(9);
        turn.Usage?.TotalTokens.Should().Be(total);
        turn.Usage?.ProviderCalls.Should().Be(3);
        (await context.ScalarAsync("SELECT COUNT(*) FROM turn_usage")).Should().Be(1L);
        var assistants = (await context.Conversations.ListMessagesAsync(context.Conversation.Id)).Where(message => message.Role == MessageRole.Assistant).ToArray();
        assistants.Select(message => message.MarkdownContent).Should().Equal("A1", "A2");
        assistants.Should().OnlyContain(message => message.Status == MessageStatus.Sealed);
    }

    [Fact]
    public async Task Duplicate_boundary_after_new_output_cannot_seal_or_overwrite_the_current_fragment()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("a", "A0"));
        var batch = await context.SeedClaimAsync("U1");
        await context.InputSession.ReadBoundaryAsync();
        var boundary = new TurnInputBoundaryEvent(batch, new TurnUsage(InputTokens: 3, OutputTokens: 4));
        await context.ApplyAsync(boundary);
        await context.InputSession.WaitForCommitAsync(batch);
        await context.InputSession.ReadBoundaryAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("b", "A1"));
        var currentId = context.Turn.CurrentAssistantMessageId;
        await context.ApplyAsync(boundary);
        context.Turn.CurrentAssistantMessageId.Should().Be(currentId);
        context.State.Messages.Last().Status.Should().Be(MessageStatus.Streaming);
        await context.ApplyAsync(new UsageReportedEvent(new TurnUsage(InputTokens: 6, OutputTokens: 8)));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "A1"));
        var stored = await context.Conversations.ListMessagesAsync(context.Conversation.Id);
        stored.Select(message => message.MarkdownContent).Should().Equal("U0", "A0", "U1", "A1");
        (await context.ScalarAsync("SELECT input_tokens FROM turn_usage")).Should().Be(6L);
    }

    [Fact]
    public async Task Frontend_publication_failure_after_commit_does_not_block_ack_or_reconsume()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1");
        var waiter = context.InputSession.WaitForCommitAsync(batch);
        context.State.TranscriptChanged += _ => throw new IOException("frontend offline");
        await context.ApplyAsync(new TurnInputBoundaryEvent(batch, null));
        (await waiter.WaitAsync(Timeout)).Messages.Should().ContainSingle();
        (await context.ScalarAsync("SELECT COUNT(*) FROM messages")).Should().Be(2L);
        (await context.InputSession.ReadBoundaryAsync()).Should().BeNull();
    }
}
