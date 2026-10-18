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
using SelfClaw.Desktop.Services.ConversationInputs.Views;
using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Attachments;
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
            "req", "blocked", harness.Snapshot() with { ModelProfileId = model },
            null));

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
            "a", "A", harness.Snapshot(), null));
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "b", "B", harness.Snapshot(), null));

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        await harness.Fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(
            conversation, state.QueueRevision, true, ConversationInputReason.TurnCancelled));

        var attempt = await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None);
        attempt.Status.Should().Be(ConversationInputStartStatus.Blocked);
        harness.Runtime.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task Attachments_are_rejected_when_the_model_is_not_multimodal()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        var attachment = await IngestAsync(harness, conversation);

        var rejected = await harness.Service.SubmitAsync(
            harness.Submission(conversation, "with image") with { Attachments = [attachment] });

        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be(ConversationInputReason.ImageUnsupportedByModel);
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Attachments_are_rejected_in_cli_mode()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        var attachment = await IngestAsync(harness, conversation);
        var submission = harness.Submission(conversation, "with image");

        var rejected = await harness.Service.SubmitAsync(submission with
        {
            Agent = submission.Agent with { Mode = AgentExecutionMode.Cli },
            Attachments = [attachment]
        });

        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be(ConversationInputReason.DirectOnly);
    }

    [Fact]
    public async Task The_six_image_ceiling_accepts_six_and_refuses_the_seventh()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        harness.Models.Multimodal.Add(harness.Models.DefaultModel);
        var images = new List<MessageAttachmentRecord>();
        for (var index = 0; index < 7; index++) images.Add(await IngestAsync(harness, conversation));

        var accepted = await harness.Service.SubmitAsync(
            harness.Submission(conversation, "six images") with { Attachments = images.Take(6).ToArray() });

        accepted.Accepted.Should().BeTrue();
        var stored = await harness.Fixture.Inputs.GetInputAsync(accepted.InputId!.Value);
        stored!.Attachments.Should().HaveCount(6);
        stored.Attachments!.Select(attachment => attachment.AttachmentId)
            .Should().BeEquivalentTo(images.Take(6).Select(image => image.Id));

        var rejected = await harness.Service.SubmitAsync(
            harness.Submission(conversation, "seven images") with { Attachments = images });
        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be(ConversationInputReason.AttachmentLimitExceeded);
    }

    [Fact]
    public async Task An_attachment_outside_the_store_is_refused_as_invalid()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        harness.Models.Multimodal.Add(harness.Models.DefaultModel);
        var attachment = await IngestAsync(harness, conversation);

        var rejected = await harness.Service.SubmitAsync(harness.Submission(conversation, "forged") with
        {
            Attachments = [attachment with { StoragePath = Path.Combine(Path.GetTempPath(), "outside.png") }]
        });

        rejected.Accepted.Should().BeFalse();
        rejected.Reason.Should().Be(ConversationInputReason.AttachmentInvalid);
    }

    [Fact]
    public async Task The_payload_round_trips_attachments_and_still_reads_a_legacy_row()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        harness.Models.Multimodal.Add(harness.Models.DefaultModel);
        var attachment = await IngestAsync(harness, conversation);

        var accepted = await harness.Service.SubmitAsync(
            harness.Submission(conversation, "with image") with { Attachments = [attachment] });

        var stored = await harness.Fixture.Inputs.GetInputAsync(accepted.InputId!.Value);
        var reference = stored!.Attachments.Should().ContainSingle().Which;
        reference.AttachmentId.Should().Be(attachment.Id);
        reference.FileName.Should().Be("photo.png");
        reference.MediaType.Should().Be("image/png");
        reference.ByteLength.Should().Be(attachment.ByteLength);
        reference.RelativePath.Should().Be(AttachmentPathResolver.CreateRelativePath(conversation, attachment.Id, "image/png"));

        // A row written before the attachments field existed must keep reading as "no attachments".
        await harness.Fixture.ExecuteAsync("""
            INSERT INTO conversation_inputs(id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                payload_json, execution_snapshot_json, status, revision, claim_id, claim_owner_run_id, created_at_utc, updated_at_utc)
            VALUES($id, $conversation, $request, 99, 0, NULL, $payload, $snapshot, 0, 1, NULL, NULL, $at, $at);
            """, ("$id", Guid.NewGuid().ToString("D")), ("$conversation", conversation.ToString("D")),
            ("$request", "legacy"), ("$payload", """{"version":1,"prompt":"legacy"}"""),
            ("$snapshot", System.Text.Json.JsonSerializer.Serialize(harness.Snapshot(),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase })),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));

        var legacy = await harness.Fixture.Inputs.FindRequestAsync(conversation, "legacy");
        legacy!.Prompt.Should().Be("legacy");
        legacy.Attachments.Should().BeNull();
    }

    [Fact]
    public async Task The_queue_item_attachment_count_reaches_the_view_formatter()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        harness.Models.Multimodal.Add(harness.Models.DefaultModel);
        var attachment = await IngestAsync(harness, conversation);
        await harness.Service.SubmitAsync(harness.Submission(conversation, "with image") with { Attachments = [attachment] });

        var state = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        var response = ConversationQueueStateFormatter.Create(Guid.NewGuid(), state, null);

        state.Items.Should().ContainSingle().Which.AttachmentCount.Should().Be(1);
        response.Items.Should().ContainSingle().Which.AttachmentCount.Should().Be(1);
    }

    private static async Task<MessageAttachmentRecord> IngestAsync(ConversationInputTestHarness harness, Guid conversationId)
    {
        var store = new AttachmentStore(harness.Fixture.Paths);
        using var content = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02]);
        return await store.IngestAsync(conversationId, content, "photo.png", "image/png");
    }

    [Fact]
    public async Task A_restart_keeps_a_persisted_backlog_paused_and_does_not_schedule_it()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync(recover: false);
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "queued", "queued", harness.Snapshot(), null));

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
        harness.Fixture.CommitGate.BeforeFinalize = (stage, _) => throw new IOException(stage);
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
            "req", "changed", harness.Snapshot() with { DefinitionHash = "outdated" }, null));

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
            "a", "A", harness.Snapshot(), null));
        await harness.Fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversation,
            "b", "B", harness.Snapshot(), null));

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
    public async Task Stopping_a_running_turn_discards_the_backlog_and_leaves_the_queue_idle()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var gated = harness.Gated!;
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.SubmitAsync(conversation, "B");
        await harness.SubmitAsync(conversation, "C");
        await gated.EnteredAsync("A");

        // User cancels A while B/C wait: the backlog is dropped and the queue is left idle.
        await harness.Service.StopAsync(conversation);
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation));

        var stopped = await harness.Fixture.Inputs.GetQueueStateAsync(conversation);
        stopped.Paused.Should().BeFalse("a user stop must not strand the next send behind a manual resume");
        stopped.Items.Should().BeEmpty("a user stop clears the backlog instead of running it after the cancelled turn");
        gated.Prompts.Should().Equal("A");

        // With nothing queued the scanner has no candidate and never churns admission (the UI-flicker cause).
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);
        for (var scan = 0; scan < 3; scan++) await harness.Dispatcher.ScanOnceAsync(CancellationToken.None);
        changes.Should().Be(0);

        // The next idle send starts at once, without a manual Resume.
        gated.Release("D");
        (await harness.SubmitAsync(conversation, "D")).Accepted.Should().BeTrue();
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation)
            && (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0);

        gated.Prompts.Should().Equal("A", "D");
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
        (await harness.Fixture.Inputs.HoldAsync(head.Id, head.Revision, ConversationInputReason.AgentChanged))
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

    [Fact]
    public async Task A_submitted_input_that_cannot_start_publishes_the_backlog_but_the_scan_does_not()
    {
        using var harness = new ConversationInputTestHarness();
        var conversation = await harness.CreateConversationAsync();
        await harness.Fixture.AcceptFollowUpAsync(conversation, "B", snapshot: harness.Snapshot());
        await harness.Service.PauseAsync(conversation,
            (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).QueueRevision, ConversationInputReason.TurnCancelled);
        var queued = await harness.Fixture.AcceptFollowUpAsync(conversation, "D", snapshot: harness.Snapshot());

        var notifications = 0;
        harness.Changes.Changed += _ => Interlocked.Increment(ref notifications);

        // A submitted input that cannot start is real backlog: the client must be told so its queue card
        // shows the input instead of it silently waiting behind a paused queue.
        var blocked = await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None, queued.Id);
        blocked.Status.Should().Be(ConversationInputStartStatus.Blocked);
        notifications.Should().Be(1);

        // The periodic scan passes no immediate input, so it never republishes and cannot reintroduce churn.
        await harness.Dispatcher.TryStartConversationAsync(conversation, CancellationToken.None);
        notifications.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_turn_then_idle_send_starts_immediately()
    {
        using var harness = new ConversationInputTestHarness();
        harness.Runtime.FailPrompts.Add("A");
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await harness.WaitForAsync(async () => (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused
            && !harness.Runs.IsRunning(conversation));

        harness.Runtime.FailPrompts.Clear();
        var second = await harness.SubmitAsync(conversation, "B");
        second.Accepted.Should().BeTrue();
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation)
            && (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0);

        harness.Runtime.Prompts.Should().Equal("A", "B");
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_user_stop_then_idle_send_starts_immediately()
    {
        using var harness = new ConversationInputTestHarness(runtime: new GatedAgentChatRuntime());
        var gated = harness.Gated!;
        var conversation = await harness.CreateConversationAsync();
        await harness.SubmitAsync(conversation, "A");
        await gated.EnteredAsync("A");
        await harness.Service.StopAsync(conversation);
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation));

        gated.Release("B");
        (await harness.SubmitAsync(conversation, "B")).Accepted.Should().BeTrue();
        await harness.WaitForAsync(async () => !harness.Runs.IsRunning(conversation)
            && (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Items.Count == 0);

        gated.Prompts.Should().Equal("A", "B");
        (await harness.Fixture.Inputs.GetQueueStateAsync(conversation)).Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_blocked_continuation_does_not_churn_run_changes()
    {
        using var harness = new ConversationInputTestHarness();
        var conversationId = await harness.CreateConversationAsync();
        var held = await harness.Fixture.AcceptFollowUpAsync(conversationId, "held", snapshot: harness.Snapshot());
        await harness.Fixture.Inputs.HoldAsync(held.Id, held.Revision, ConversationInputReason.AgentChanged);
        var conversation = (await harness.Fixture.Conversations.GetConversationAsync(conversationId))!;
        var changes = 0;
        harness.Runs.Changed += _ => Interlocked.Increment(ref changes);

        for (var scan = 0; scan < 4; scan++)
            (await harness.Runs.TryReserveContinuationAsync(conversation)).Should().BeNull();

        changes.Should().Be(0);
    }
}
