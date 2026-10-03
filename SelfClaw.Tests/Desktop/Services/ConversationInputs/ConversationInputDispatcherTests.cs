using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

/// <summary>
/// Q01, Q02, Q05, Q06, Q07, Q08, Q15 and Q16 at the dispatcher boundary: the durable queue is the
/// only execution path, failures pause one conversation without blocking another and an invalid
/// snapshot holds the item in place.
/// </summary>
public sealed class ConversationInputDispatcherTests
{
    [Fact]
    public async Task A_successful_turn_advances_the_fifo_one_independent_turn_at_a_time()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");
        await harness.SubmitAsync(conversation, "C");

        await harness.StartAsync(conversation);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0 && !harness.Runs.IsRunning(conversation));

        harness.Runtime.Prompts.Should().Equal("A", "B", "C");
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.Paused.Should().BeFalse();
        var messages = await harness.Fixture.Conversations.ListMessagesAsync(conversation);
        messages.Where(message => message.Role == MessageRole.User)
            .Select(message => message.MarkdownContent).Should().Contain(["A", "B", "C"]);
        (await harness.Fixture.ScalarAsync("SELECT COUNT(DISTINCT turn_id) FROM messages WHERE role = 1;")).Should().Be(3L);
    }

    [Fact]
    public async Task A_failed_turn_pauses_the_queue_and_never_starts_the_next_item()
    {
        using var harness = new ConversationInputTestHarness();
        harness.Runtime.FailPrompts.Add("A");
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");

        await harness.StartAsync(conversation);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);

        harness.Runtime.Prompts.Should().Equal("A");
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.PauseReason.Should().Be(ConversationInputReason.TurnFailed);
        state.Items.Should().ContainSingle().Which.Preview.Should().Be("B");
        state.Items[0].Status.Should().Be(ConversationInputStatus.Pending);
    }

    [Fact]
    public async Task One_failing_conversation_does_not_block_another_conversation()
    {
        using var harness = new ConversationInputTestHarness();
        harness.Runtime.FailPrompts.Add("fail");
        var failing = await harness.CreateConversationAsync();
        var healthy = await harness.CreateConversationAsync();
        await harness.SubmitAsync(failing, "fail");
        await harness.SubmitAsync(healthy, "ok");

        await harness.StartAsync(healthy);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(healthy)).Items.Count == 0);
        await harness.StartAsync(failing);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(failing)).Paused);

        harness.Runtime.Prompts.Should().Contain("ok");
        (await harness.Fixture.Inputs.GetQueueStateAsync(failing)).Paused.Should().BeTrue();
        (await harness.Fixture.Inputs.GetQueueStateAsync(healthy)).Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_disabled_model_holds_the_input_and_does_not_cross_it()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        var model = Guid.NewGuid();
        harness.Models.Available.Remove(model);
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "req", ConversationInputKind.FollowUp, null, "blocked", harness.Snapshot() with { ModelProfileId = model },
            null, true));

        await harness.StartAsync(conversation);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);

        harness.Runtime.Prompts.Should().BeEmpty();
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        var item = state.Items.Should().ContainSingle().Which;
        item.Status.Should().Be(ConversationInputStatus.Held);
        item.ReasonCode.Should().Be(ConversationInputReason.ModelDisabled);

        // A later valid input must not skip the held blocker.
        await harness.SubmitAsync(conversation, "after");
        await harness.StartAsync(conversation);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_pauses_the_queue_so_the_next_item_does_not_start()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "a", ConversationInputKind.FollowUp, null, "A", harness.Snapshot(), null, true));
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "b", ConversationInputKind.FollowUp, null, "B", harness.Snapshot(), null, true));

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        await harness.Fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(
            conversation, state.QueueRevision, true, ConversationInputReason.TurnCancelled));

        var attempt = await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None);
        attempt.Status.Should().Be(ConversationInputStartStatus.Blocked);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Attachments_are_rejected_explicitly()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        var submission = harness.Submission(conversation, "with attachment");
        var rejected = await harness.Service.SubmitAsync(submission with
        {
            Attachments = [new MessageAttachmentRecord(Guid.NewGuid(), Guid.Empty, MessageAttachmentKind.Image,
                "a.png", "image/png", "/tmp/a.png", 1, DateTimeOffset.UtcNow)]
        });
        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be(ConversationInputReason.AttachmentsUnsupported);
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_switch_keeps_a_persisted_backlog_but_does_not_schedule_it()
    {
        using var harness = new ConversationInputTestHarness(queueEnabled: false);
        var conversation = await harness.CreateConversationAsync(recover: false);
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "queued", ConversationInputKind.FollowUp, null, "queued", harness.Snapshot(), null, true));

        // Restart recovery pauses without dropping the item.
        await harness.Dispatcher.RecoverOnceAsync(CancellationToken.None);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);
        var item = (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Should().ContainSingle().Which;
        item.Preview.Should().Be("queued");

        // A paused conversation is never claimed, so nothing runs.
        var attempt = await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None);
        attempt.Status.Should().Be(ConversationInputStartStatus.Blocked);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_terminal_persistence_failure_pauses_the_queue_with_the_persist_reason()
    {
        using var harness = new ConversationInputTestHarness();
        harness.Fixture.Turns.BeforeFinalizeCommit = (stage, _) => throw new IOException(stage);
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");

        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.PauseReason.Should().Be(ConversationInputReason.PersistFailed);
        state.Items.Should().ContainSingle().Which.Preview.Should().Be("B");
    }

    [Theory]
    [InlineData(RunCompletionStatus.Failed, ConversationInputReason.TurnFailed)]
    [InlineData(RunCompletionStatus.Blocked, ConversationInputReason.TurnBlocked)]
    [InlineData(RunCompletionStatus.Truncated, ConversationInputReason.TurnTruncated)]
    public async Task Non_success_turn_outcomes_pause_the_queue(RunCompletionStatus completion, string reason)
    {
        using var harness = new ConversationInputTestHarness();
        harness.Runtime.Outcomes.Enqueue(completion);
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");

        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).PauseReason.Should().Be(reason);
        (await harness.Fixture.ScalarAsync("SELECT status FROM conversation_turns;"))
            .Should().Be((long)(completion == RunCompletionStatus.Truncated ? ConversationTurnStatus.Truncated : completion == RunCompletionStatus.Blocked ? ConversationTurnStatus.Blocked : ConversationTurnStatus.Failed));
    }

    [Fact]
    public async Task An_agent_revision_change_holds_the_input_and_pauses()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "req", ConversationInputKind.FollowUp, null, "changed", harness.Snapshot() with { DefinitionHash = "outdated" }, null, true));

        await harness.StartAsync(conversation);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused);

        var item = (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Should().ContainSingle().Which;
        item.Status.Should().Be(ConversationInputStatus.Held);
        item.ReasonCode.Should().Be(ConversationInputReason.AgentChanged);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_during_a_running_turn_prevents_the_next_item_and_pauses_with_cancelled_reason()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var gated = harness.Gated!;
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");

        await harness.StartAsync(conversation);
        await gated.EnteredAsync("A");

        // The host stop path pauses the durable queue and then cancels the live turn.
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        await harness.Service.PauseAsync(conversation, state.QueueRevision, ConversationInputReason.TurnCancelled);
        harness.Runs.Stop(conversation);

        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation));

        gated.Prompts.Should().Equal("A");
        var final = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        final.Paused.Should().BeTrue();
        final.PauseReason.Should().Be(ConversationInputReason.TurnCancelled);
        final.Items.Should().ContainSingle().Which.Preview.Should().Be("B");
    }

    [Fact]
    public async Task Stop_after_the_next_item_is_admitted_cancels_that_turn_and_pauses()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var gated = harness.Gated!;
        gated.Release("A");
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");

        await harness.StartAsync(conversation);
        // A succeeded, so the dispatcher already admitted B and its turn is live.
        await gated.EnteredAsync("B");

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        await harness.Service.PauseAsync(conversation, state.QueueRevision, ConversationInputReason.TurnCancelled);
        harness.Runs.Stop(conversation);

        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation));
        gated.Prompts.Should().Equal("A", "B");
        var final = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        final.Paused.Should().BeTrue();
        final.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_restart_holds_a_previous_claim_and_never_replays_it()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync(recover: false);
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "a", ConversationInputKind.FollowUp, null, "A", harness.Snapshot(), null, true));
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "b", ConversationInputKind.FollowUp, null, "B", harness.Snapshot(), null, true));

        // A previous process claimed A and exited before consuming it.
        var claimed = await harness.Fixture.Inputs.TryClaimNextFollowUpAsync(conversation, Guid.NewGuid(), Guid.NewGuid());
        claimed!.Status.Should().Be(ConversationInputStatus.Claimed);

        await harness.Dispatcher.RecoverOnceAsync(CancellationToken.None);

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        state.Paused.Should().BeTrue();
        state.Items.Single(item => item.InputId == claimed.Id).Status.Should().Be(ConversationInputStatus.Held);
        state.Items.Single(item => item.InputId == claimed.Id).ReasonCode.Should().Be(ConversationInputReason.InterruptedByRestart);
        state.Items.Single(item => item.Preview == "B").Status.Should().Be(ConversationInputStatus.Pending);

        (await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None)).Status
            .Should().Be(ConversationInputStartStatus.Blocked);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_a_running_turn_then_resuming_delivers_queued_items_in_order_without_scan_churn()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var gated = harness.Gated!;
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");
        await harness.SubmitAsync(conversation, "C");
        await gated.EnteredAsync("A");

        // User cancels A while B/C wait: pause the durable queue, then cancel the live turn.
        await harness.Service.StopAsync(conversation);
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation));

        var paused = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        paused.Paused.Should().BeTrue();
        paused.Items.Select(item => item.Preview).Should().Equal("B", "C");
        gated.Prompts.Should().Equal("A");

        // The scanner must not reserve/release handles for the paused queue (the UI-flicker cause).
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        for (var scan = 0; scan < 3; scan++) await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);
        changes.Should().Be(0);

        // Resuming runs the backlog in FIFO order.
        gated.Release("B");
        gated.Release("C");
        await harness.Service.ResumeAsync(conversation,
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).QueueRevision);
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0);

        gated.Prompts.Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task A_paused_queue_is_not_a_scan_candidate_and_never_churns_admission()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.AcceptFollowUpAsync(conversation, "B", snapshot: harness.Snapshot());
        await harness.Fixture.AcceptFollowUpAsync(conversation, "C", snapshot: harness.Snapshot());
        (await harness.Fixture.Inputs.ListDispatchableConversationIdsAsync()).Should().Contain(conversation,
            "an unpaused queue with a Pending head is a candidate");
        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        await harness.Service.PauseAsync(conversation, state.QueueRevision, ConversationInputReason.TurnCancelled);

        (await harness.Fixture.Inputs.ListDispatchableConversationIdsAsync()).Should().NotContain(conversation,
            "a paused queue has no claimable head");
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        for (var scan = 0; scan < 4; scan++) await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);

        changes.Should().Be(0, "the scanner must not reserve and release a run handle for a paused queue");
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_fifo_head_conversation_is_not_a_scan_candidate()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        var head = await harness.Fixture.AcceptFollowUpAsync(conversation, "A", snapshot: harness.Snapshot());
        await harness.Fixture.AcceptFollowUpAsync(conversation, "B", snapshot: harness.Snapshot());
        (await harness.Fixture.Inputs.ListDispatchableConversationIdsAsync()).Should().Contain(conversation);
        (await harness.Fixture.Inputs.HoldAsync(head.Id, head.Revision, ConversationInputReason.AgentChanged, null))
            .Status.Should().Be(ConversationInputUpdateStatus.Applied);

        (await harness.Fixture.Inputs.ListDispatchableConversationIdsAsync()).Should().NotContain(conversation,
            "a Held head blocks B and must not be claimed");
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);
        changes.Should().Be(0);
    }

    [Fact]
    public async Task Resuming_a_paused_queue_runs_its_pending_items_again()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.AcceptFollowUpAsync(conversation, "B", snapshot: harness.Snapshot());
        var paused = await harness.Service.PauseAsync(conversation,
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).QueueRevision, ConversationInputReason.TurnCancelled);

        await harness.Service.ResumeAsync(conversation, paused.QueueRevision);
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation) &&
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0);

        harness.Runtime.Prompts.Should().ContainSingle().Which.Should().Be("B");
    }

    [Fact]
    public async Task A_paused_conversation_accepts_a_later_input_but_does_not_reserve_a_run()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.AcceptFollowUpAsync(conversation, "B", snapshot: harness.Snapshot());
        await harness.Service.PauseAsync(conversation,
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).QueueRevision, ConversationInputReason.TurnCancelled);
        var queued = await harness.Fixture.AcceptFollowUpAsync(conversation, "D", snapshot: harness.Snapshot());

        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        // Even with the just-accepted input id, a paused queue must not reserve a run handle.
        var attempt = await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None, queued.Id);

        attempt.Status.Should().Be(ConversationInputStartStatus.Blocked);
        changes.Should().Be(0);
        harness.Runtime.Prompts.Should().BeEmpty();
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Select(item => item.Preview)
            .Should().Equal("B", "D");
    }
}
