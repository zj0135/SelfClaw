using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

/// <summary>
/// R01–R12 of the P6 recovery matrix: every scenario uses an isolated temporary database and a
/// real SQLite transaction chain. A "restart" is modeled by re-reading the same database after
/// the first process state is abandoned; startup recovery is the only replay path and it never
/// re-executes uncertain work.
/// </summary>
public sealed class ConversationInputRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task R01_an_acceptance_that_fails_before_commit_leaves_no_fact_and_the_same_request_retries()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var conversationId = start.Turn.ConversationId;
        await fixture.ExecuteAsync(
            "CREATE TRIGGER fail_input BEFORE INSERT ON conversation_inputs BEGIN SELECT RAISE(ABORT, 'r01'); END;");
        var request = new ConversationInputAcceptRequest(conversationId, "r01-request",
            ConversationInputKind.FollowUp, null, "queued", fixture.Snapshot(), null);

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Inputs.AcceptAsync(request));

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_input_state;")).Should().Be(0L,
            "the state row allocated in the same transaction must roll back with the rejected input");

        await fixture.ExecuteAsync("DROP TRIGGER fail_input;");
        var retry = await fixture.Inputs.AcceptAsync(request);
        retry.Status.Should().Be(ConversationInputAcceptStatus.Accepted);
        retry.Input!.ClientRequestId.Should().Be("r01-request");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);
    }

    [Fact]
    public async Task R02_a_committed_acceptance_is_queryable_pending_and_paused_after_restart_and_retry_returns_the_same_id()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var conversationId = start.Turn.ConversationId;
        var request = new ConversationInputAcceptRequest(conversationId, "r02-request",
            ConversationInputKind.FollowUp, null, "durable", fixture.Snapshot(), null);
        var accepted = await fixture.Inputs.AcceptAsync(request);
        accepted.Status.Should().Be(ConversationInputAcceptStatus.Accepted);

        // The process exits before the submit ACK reaches the caller.
        await fixture.RecoverStartupAsync();

        var reloaded = await fixture.Inputs.GetInputAsync(accepted.Input!.Id);
        reloaded!.Status.Should().Be(ConversationInputStatus.Pending);
        var state = await fixture.Inputs.GetQueueStateAsync(conversationId);
        state.Paused.Should().BeTrue("restart recovery pauses every queue with unprocessed work");
        state.Items.Should().ContainSingle().Which.Preview.Should().Be("durable");

        var retry = await fixture.Inputs.AcceptAsync(request);
        retry.Status.Should().Be(ConversationInputAcceptStatus.Duplicate);
        retry.Input!.Id.Should().Be(accepted.Input.Id);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);
    }

    [Fact]
    public async Task R03_a_claim_without_consumption_survives_restart_as_held_without_running_provider_or_tool()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync(recover: false);
        await harness.Fixture.AcceptFollowUpAsync(conversation, "A", snapshot: harness.Snapshot());
        var claimed = await harness.Fixture.ClaimNextAsync(conversation, Guid.NewGuid(), Guid.NewGuid());
        claimed.Status.Should().Be(ConversationInputStatus.Claimed);

        await harness.Dispatcher.RecoverOnceAsync(CancellationToken.None);

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.Paused.Should().BeTrue();
        var item = state.Items.Should().ContainSingle().Which;
        item.InputId.Should().Be(claimed.Id);
        item.Status.Should().Be(ConversationInputStatus.Held);
        item.ReasonCode.Should().Be(ConversationInputReason.InterruptedByRestart);
        harness.Runtime.Prompts.Should().BeEmpty("a stale claim is never replayed as an execution");
        (await harness.Fixture.ScalarAsync("SELECT COUNT(*) FROM tool_runs;")).Should().Be(0L);
        (await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None)).Status
            .Should().Be(ConversationInputStartStatus.Blocked);
    }

    [Fact]
    public async Task R04_a_failed_consumption_transaction_rolls_back_the_batch_and_recovery_holds_it_without_duplicate_users()
    {
        using var fixture = new ConversationPersistenceFixture(
            boundaryBeforeCommit: (stage, _) => throw new InvalidOperationException(stage));
        var start = await fixture.StartAsync("U0");
        var batch = await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "U1", "U2");
        var commit = new ConversationInputBoundaryCommit(batch, new ConversationTurnCommit(
            start.Turn with { Usage = new TurnUsage(InputTokens: 5, OutputTokens: 6, TotalTokens: 11) }, [], []));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inputs.CommitBoundaryAsync(commit));
        failure.Message.Should().Be("boundary-before-commit");

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages;")).Should().Be(1L, "no half of the batch may be persisted");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM turn_usage;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;")).Should().Be(2L,
            "the whole claimed batch stays claimable-for-recovery, not half consumed");

        await fixture.RecoverStartupAsync();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 3;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 2;")).Should().Be(0L,
            "a rolled-back claim is never marked Consumed");
    }

    [Fact]
    public async Task R05_a_consumption_committed_before_exit_is_not_replayed_and_its_turn_recovers_interrupted()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var batch = await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "U1");
        await fixture.Inputs.CommitBoundaryAsync(new ConversationInputBoundaryCommit(batch,
            new ConversationTurnCommit(start.Turn with { Usage = new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: 7) }, [], [])));

        // The process exits before the runtime observes the ACK.
        await fixture.RecoverStartupAsync();

        var input = await fixture.Inputs.GetInputAsync(batch.Inputs[0].InputId);
        input!.Status.Should().Be(ConversationInputStatus.Consumed);
        input.MessageId.Should().Be(batch.Inputs[0].MessageId);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(2L,
            "U0 plus exactly one U1; recovery must not insert the user again");
        (await fixture.Turns.ListTurnsAsync(start.Turn.ConversationId)).Single().Status
            .Should().Be(ConversationTurnStatus.Interrupted);
        (await fixture.Inputs.ReadConsumptionAsync(batch)).Should().NotBeNull(
            "the consumption fact is re-readable without a new transaction");
        (await fixture.Inputs.TryClaimNextFollowUpAsync(start.Turn.ConversationId, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeNull("a consumed input is never put back into Pending");
    }

    [Fact]
    public async Task R06_an_executed_tool_without_a_terminal_commit_is_interrupted_and_not_replayed()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var conversationId = start.Turn.ConversationId;
        var conversation = (await fixture.Conversations.GetConversationAsync(conversationId))!;
        var input = await fixture.AcceptFollowUpAsync(conversationId, "U1");
        var owner = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var claimed = await fixture.ClaimNextAsync(conversationId, owner, claimId);
        var started = await fixture.StartClaimedAsync(conversation, claimed, claimId, owner);
        var message = await fixture.AssistantAsync(started.Turn, "partial");
        var tool = CreateTool(message);
        message = WithTool(message, tool);
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(started.Turn, [message], [tool]));

        // The process exits after the tool has run but before the terminal commit.
        await fixture.RecoverStartupAsync();

        (await fixture.Inputs.GetInputAsync(input.Id))!.Status.Should().Be(ConversationInputStatus.Consumed,
            "an executed tool never returns its input to Pending");
        (await fixture.ScalarAsync("SELECT status FROM tool_runs;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(2L);
        (await fixture.Turns.ListTurnsAsync(conversationId)).Last().Status.Should().Be(ConversationTurnStatus.Interrupted);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status IN (0, 1);")).Should().Be(0L,
            "no uncertain item is schedulable after the restart");
    }

    [Fact]
    public async Task R07_a_failed_request_after_a_sealed_boundary_keeps_messages_usage_and_holds_the_remaining_steer()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        await context.ApplyAsync(new AssistantTextDeltaEvent("a", "A0"));
        var batch = await context.SeedClaimAsync("U1");
        await context.ApplyAsync(new TurnInputBoundaryEvent(batch, new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: 7)));
        (await context.InputSession.WaitForCommitAsync(batch).WaitAsync(Timeout)).Messages.Should().ContainSingle();
        await context.SeedPendingSteerAsync("U2");

        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Failed, null, "provider failed"));

        var turn = (await context.Turns.ListTurnsAsync(context.Conversation.Id)).Single();
        turn.Status.Should().Be(ConversationTurnStatus.Failed);
        turn.Usage.Should().NotBeNull();
        (await context.Conversations.ListMessagesAsync(context.Conversation.Id))
            .Select(message => message.MarkdownContent).Should().Equal("U0", "A0", "U1");
        (await context.ScalarAsync("SELECT status FROM conversation_inputs WHERE kind = 1 ORDER BY sequence DESC LIMIT 1;"))
            .Should().Be(3L, "the remaining steer is held by the failed terminal transaction");

        // A restart sees the same sealed history and never re-requests the provider.
        await new SqliteStartupRecovery(context.Database).RecoverAsync();
        (await context.Conversations.ListMessagesAsync(context.Conversation.Id))
            .Select(message => message.MarkdownContent).Should().Equal("U0", "A0", "U1");
        (await context.ScalarAsync("SELECT status FROM conversation_inputs WHERE kind = 1 ORDER BY sequence DESC LIMIT 1;"))
            .Should().Be(3L);
        (await context.Inputs.TryClaimNextFollowUpAsync(context.Conversation.Id, Guid.NewGuid(), Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task R08_a_terminal_write_failure_pauses_before_release_and_survives_restart()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        harness.Fixture.CommitGate.BeforeFinalize = (stage, _) => throw new IOException(stage);
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.Gated!.EnteredAsync("A").WaitAsync(Timeout);
        // B is accepted while A is still running, so this is a queue submission, not an idle send.
        await harness.SubmitAsync(conversation, "B");
        harness.Gated.Release("A");

        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation) &&
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);

        harness.Gated.Prompts.Should().Equal(new[] { "A" },
            "the next queued turn must not start after a finalize failure");
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.PauseReason.Should().Be(ConversationInputReason.PersistFailed);
        state.Items.Should().ContainSingle().Which.Preview.Should().Be("B");

        // A restart keeps both the pause and its reason instead of silently resuming the backlog.
        await harness.Fixture.RecoverStartupAsync();
        var recovered = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        recovered.Paused.Should().BeTrue();
        recovered.PauseReason.Should().Be(ConversationInputReason.PersistFailed);
        recovered.Items.Should().ContainSingle().Which.Status.Should().Be(ConversationInputStatus.Pending);
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        for (var scan = 0; scan < 3; scan++) await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);
        changes.Should().Be(0);
        harness.Gated.Prompts.Should().Equal("A");
    }

    [Fact]
    public async Task R09_stop_or_shutdown_ends_an_outstanding_boundary_wait_without_orphans()
    {
        using var context = new ConversationTurnTestContext();
        await context.StartAsync();
        var batch = await context.SeedClaimAsync("U1");
        var waiter = context.InputSession.WaitForCommitAsync(batch);
        waiter.IsCompleted.Should().BeFalse();
        context.InputSession.PendingWaiterCount.Should().Be(1);

        await context.InputSession.HoldUnconsumedSteersAsync(ConversationInputReason.TurnCancelled);
        context.InputSession.Cancel();

        await FluentActions.Awaiting(() => waiter.WaitAsync(Timeout)).Should().ThrowAsync<OperationCanceledException>();
        context.InputSession.PendingWaiterCount.Should().Be(0, "no waiter may survive the terminal session state");
        (await context.ScalarAsync("SELECT status FROM conversation_inputs;")).Should().Be(3L,
            "an unconsumed steer settles as Held before cancellation");
        await FluentActions.Awaiting(() => context.InputSession.ReadBoundaryAsync())
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => context.InputSession.WaitForCommitAsync(batch))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task R10_a_successful_delete_removes_the_queue_and_a_failed_delete_keeps_it_paused()
    {
        // The full deletion ownership lives in ConversationDeletionServiceTests; this scenario pins
        // the R10 queue-specific invariant on the same durable fixtures those tests use.
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await fixture.CreateConversationAsync();
        await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation.Id,
            "r10", ConversationInputKind.FollowUp, null, "queued", fixture.Snapshot(), null));

        var state = await fixture.Inputs.GetQueueStateAsync(conversation.Id);
        await fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(conversation.Id,
            state.QueueRevision, true, ConversationInputReason.TurnCancelled));
        (await fixture.Inputs.GetQueueStateAsync(conversation.Id)).Paused.Should().BeTrue(
            "a failed delete leaves the durable queue paused");

        await fixture.Conversations.DeleteConversationAsync(conversation.Id);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_input_state;")).Should().Be(0L);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation.Id,
            "late", ConversationInputKind.FollowUp, null, "late", fixture.Snapshot(), null)));
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L,
            "a deleted conversation must not rebuild its queue from a late acceptance");
    }

    [Fact]
    public async Task R11_a_lost_wakeup_is_recovered_by_the_scan_without_starving_other_conversations()
    {
        using var harness = new ConversationInputTestHarness();
        var first = await harness.CreateConversationAsync();
        var second = await harness.CreateConversationAsync();
        var paused = await harness.CreateConversationAsync();
        var held = await harness.CreateConversationAsync();

        // Seeded through the store, bypassing submit notifications: the equivalent of a lost in-process wakeup.
        await harness.Fixture.AcceptFollowUpAsync(first, "A", snapshot: harness.Snapshot());
        await harness.Fixture.AcceptFollowUpAsync(second, "B", snapshot: harness.Snapshot());
        await harness.Fixture.AcceptFollowUpAsync(paused, "C", snapshot: harness.Snapshot());
        var head = await harness.Fixture.AcceptFollowUpAsync(held, "D", snapshot: harness.Snapshot());
        await harness.Fixture.AcceptFollowUpAsync(held, "E", snapshot: harness.Snapshot());
        (await harness.Fixture.Inputs.HoldAsync(head.Id, head.Revision, ConversationInputReason.AgentChanged))
            .Status.Should().Be(ConversationInputUpdateStatus.Applied);
        var pausedQueue = await harness.Fixture.Inputs.GetQueueStateAsync(paused);
        await harness.Fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(paused,
            pausedQueue.QueueRevision, true, ConversationInputReason.TurnCancelled));

        var pausedChanges = 0;
        harness.Runs.Changed += change => { if (change.Handle.ConversationId == paused) Interlocked.Increment(ref pausedChanges); };

        await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(first)).Items.Count == 0 &&
            (await harness.Fixture.Inputs.GetQueueStateAsync(second)).Items.Count == 0);

        harness.Runtime.Prompts.Should().BeEquivalentTo(["A", "B"], "the scan recovers work whose wakeup was lost");
        (await harness.Fixture.Inputs.GetQueueStateAsync(held)).Items.Should().HaveCount(2,
            "a Held FIFO head blocks its own conversation without starving the others");
        (await harness.Fixture.Inputs.GetQueueStateAsync(paused)).Items.Should().ContainSingle().Which.Preview.Should().Be("C");
        harness.Runtime.Prompts.Should().NotContain("C");
        harness.Runtime.Prompts.Should().NotContain("D").And.NotContain("E");
        pausedChanges.Should().Be(0, "a paused queue must not churn admission on every scan");
    }

    [Fact]
    public async Task R12_a_pending_steer_after_restart_stays_held_and_never_enters_a_new_turn()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var conversationId = start.Turn.ConversationId;
        var followUp = await fixture.AcceptFollowUpAsync(conversationId, "queued follow-up");
        var steerId = await fixture.SeedRawPendingSteerAsync(start.Turn, "old steer", sequence: followUp.Sequence + 1);

        await fixture.RecoverStartupAsync();

        var state = await fixture.Inputs.GetQueueStateAsync(conversationId);
        state.Paused.Should().BeTrue();
        state.Items.Should().HaveCount(2, "the persisted steer stays visible as Held next to the pending follow-up");
        state.Items.Single(item => item.InputId == steerId).Status.Should().Be(ConversationInputStatus.Held);
        state.Items.Single(item => item.InputId == followUp.Id).Status.Should().Be(ConversationInputStatus.Pending);
        (await fixture.Inputs.GetInputAsync(followUp.Id))!.Status.Should().Be(ConversationInputStatus.Pending);
        (await fixture.Inputs.GetInputAsync(steerId))!.Status.Should().Be(ConversationInputStatus.Held,
            "the pending steer is Held by restart recovery and cannot enter a new turn");
        (await fixture.Inputs.TryClaimPendingSteersAsync(conversationId, start.Turn.Id, Guid.NewGuid())).Should().BeNull();
        (await fixture.Inputs.ListDispatchableConversationIdsAsync()).Should().NotContain(conversationId);
    }

    private static ToolExecutionRecord CreateTool(MessageRecord message)
        => new(Guid.NewGuid(), message.ConversationId, "read_file", "{}", ToolExecutionStatus.Running,
            null, "call-1", null, message.CreatedAtUtc, message.UpdatedAtUtc, MessageId: message.Id);

    private static MessageRecord WithTool(MessageRecord message, ToolExecutionRecord tool)
        => message with
        {
            Segments =
            [
                new MessageSegmentRecord(message.Id, 0, MessageSegmentKind.Text, message.MarkdownContent, null),
                new MessageSegmentRecord(message.Id, 1, MessageSegmentKind.ToolCall, null, tool.Id)
            ]
        };
}
