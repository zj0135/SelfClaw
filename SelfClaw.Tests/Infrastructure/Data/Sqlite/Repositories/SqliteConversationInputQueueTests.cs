using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

/// <summary>
/// Q03, Q04, Q12, Q13 and Q14 at the durable store boundary: idempotent acceptance, capacity,
/// conditional mutation, claim/consume atomicity and restart recovery.
/// </summary>
public sealed class SqliteConversationInputQueueTests
{
    [Fact]
    public async Task Acceptance_is_idempotent_and_rejects_a_changed_payload_as_a_conflict()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        const string requestId = "request-1";

        var snapshot = fixture.Snapshot();
        var first = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, requestId, ConversationInputKind.FollowUp, null, "hello", snapshot, null, true));
        var retry = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, requestId, ConversationInputKind.FollowUp, null, "hello", snapshot, null, true));
        first.Status.Should().Be(ConversationInputAcceptStatus.Accepted);
        retry.Status.Should().Be(ConversationInputAcceptStatus.Duplicate);
        retry.Input!.Id.Should().Be(first.Input!.Id);

        var conflict = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, requestId, ConversationInputKind.FollowUp, null, "different", snapshot, null, true));
        conflict.Status.Should().Be(ConversationInputAcceptStatus.Conflict);
        conflict.Reason.Should().Be("request-conflict");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);
    }

    [Fact]
    public async Task Acceptance_rejects_oversized_prompts_and_does_not_evict_the_oldest_item()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;

        var oversized = new string('x', 64 * 1024 + 1);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, "big", ConversationInputKind.FollowUp, null, oversized, fixture.Snapshot(), null, true)));

        var accepted = new string('y', 64 * 1024);
        var edge = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, "edge", ConversationInputKind.FollowUp, null, accepted, fixture.Snapshot(), null, true));
        edge.Status.Should().Be(ConversationInputAcceptStatus.Accepted);

        for (var index = 0; index < 19; index++)
        {
            var result = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
                conversationId, $"filler-{index}", ConversationInputKind.FollowUp, null, $"p{index}", fixture.Snapshot(), null, true));
            result.Status.Should().Be(ConversationInputAcceptStatus.Accepted);
        }

        var full = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, "overflow", ConversationInputKind.FollowUp, null, "overflow", fixture.Snapshot(), null, true));
        full.Status.Should().Be(ConversationInputAcceptStatus.Full);
        full.Reason.Should().Be(ConversationInputReason.Capacity);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(20L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE client_request_id = 'edge';")).Should().Be(1L);
    }

    [Fact]
    public async Task Global_capacity_is_enforced_across_conversations()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversations = new List<Guid>();
        for (var index = 0; index < 25; index++)
        {
            conversations.Add((await fixture.StartAsync($"c{index}")).Turn.ConversationId);
        }

        foreach (var conversationId in conversations)
        {
            for (var index = 0; index < 20; index++)
            {
                var result = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
                    conversationId, $"r-{index}", ConversationInputKind.FollowUp, null, "p", fixture.Snapshot(), null, true));
                result.Status.Should().Be(ConversationInputAcceptStatus.Accepted);
            }
        }

        var overflow = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversations[0], "global-overflow", ConversationInputKind.FollowUp, null, "p", fixture.Snapshot(), null, true));
        overflow.Status.Should().Be(ConversationInputAcceptStatus.Full);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(500L);
    }

    [Fact]
    public async Task Edit_and_cancel_honor_revision_and_a_claim_makes_the_item_immutable()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        var input = await fixture.AcceptFollowUpAsync(conversationId, "first");

        var stale = await fixture.Inputs.EditAsync(new ConversationInputEditRequest(input.Id, input.Revision + 5, "stale"));
        stale.Status.Should().Be(ConversationInputUpdateStatus.Conflict);

        var edited = await fixture.Inputs.EditAsync(new ConversationInputEditRequest(input.Id, input.Revision, "edited"));
        edited.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        edited.Input!.Prompt.Should().Be("edited");
        edited.Input.Revision.Should().Be(input.Revision + 1);

        var owner = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var claimed = await fixture.ClaimNextAsync(conversationId, owner, claimId);
        claimed.Status.Should().Be(ConversationInputStatus.Claimed);
        claimed.Prompt.Should().Be("edited");

        var afterClaim = await fixture.Inputs.EditAsync(new ConversationInputEditRequest(claimed.Id, claimed.Revision, "too late"));
        afterClaim.Status.Should().Be(ConversationInputUpdateStatus.Conflict);
        var cancel = await fixture.Inputs.CancelAsync(new ConversationInputCancelRequest(claimed.Id, claimed.Revision));
        cancel.Status.Should().Be(ConversationInputUpdateStatus.Conflict);

        var reloaded = await fixture.Inputs.GetInputAsync(claimed.Id);
        reloaded!.Prompt.Should().Be("edited");
        reloaded.Status.Should().Be(ConversationInputStatus.Claimed);
    }

    [Fact]
    public async Task A_held_item_can_be_edited_back_to_pending_and_retried()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var input = await fixture.AcceptFollowUpAsync(start.Turn.ConversationId, "held");

        var held = await fixture.Inputs.HoldAsync(input.Id, input.Revision, ConversationInputReason.AgentChanged, "changed");
        held.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        held.Input!.Status.Should().Be(ConversationInputStatus.Held);

        var edited = await fixture.Inputs.EditAsync(new ConversationInputEditRequest(input.Id, held.Input.Revision, "retry body"));
        edited.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        edited.Input!.Status.Should().Be(ConversationInputStatus.Pending);
        edited.Input.ReasonCode.Should().BeNull();

        var heldAgain = await fixture.Inputs.HoldAsync(edited.Input.Id, edited.Input.Revision, ConversationInputReason.AgentChanged, "changed");
        heldAgain.Input!.Status.Should().Be(ConversationInputStatus.Held);
        var retried = await fixture.Inputs.RetryAsync(heldAgain.Input.Id, heldAgain.Input.Revision);
        retried.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        retried.Input!.Status.Should().Be(ConversationInputStatus.Pending);
    }

    [Fact]
    public async Task Pause_round_trips_with_monotonic_revision_and_blocks_claims()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        await fixture.AcceptFollowUpAsync(conversationId, "queued");

        var state = await fixture.Inputs.GetQueueStateAsync(conversationId);
        var paused = await fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(
            conversationId, state.QueueRevision, true, ConversationInputReason.QueuePaused));
        paused.Paused.Should().BeTrue();
        paused.QueueRevision.Should().BeGreaterThan(state.QueueRevision);

        var claimed = await fixture.Inputs.TryClaimNextFollowUpAsync(conversationId, Guid.NewGuid(), Guid.NewGuid());
        claimed.Should().BeNull();

        var stale = await fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(
            conversationId, state.QueueRevision, false, null));
        stale.Paused.Should().BeTrue();
        stale.QueueRevision.Should().Be(paused.QueueRevision);

        var resumed = await fixture.Inputs.SetPausedAsync(new ConversationInputPauseRequest(
            conversationId, paused.QueueRevision, false, null));
        resumed.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task Claim_and_start_atomically_write_one_user_and_mark_the_input_consumed()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        var conversation = await fixture.Conversations.GetConversationAsync(conversationId) ?? throw new InvalidOperationException();
        await fixture.AcceptFollowUpAsync(conversationId, "follow up");
        await fixture.AcceptFollowUpAsync(conversationId, "second");

        var owner = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var claimed = await fixture.ClaimNextAsync(conversationId, owner, claimId);
        var commit = await fixture.StartClaimedAsync(conversation, claimed, claimId, owner);

        commit.Messages.Should().ContainSingle().Which.Role.Should().Be(MessageRole.User);
        commit.Messages[0].MarkdownContent.Should().Be("follow up");
        commit.Turn.Status.Should().Be(ConversationTurnStatus.Running);
        var reloaded = await fixture.Inputs.GetInputAsync(claimed.Id);
        reloaded!.Status.Should().Be(ConversationInputStatus.Consumed);
        reloaded.ConsumedTurnId.Should().Be(commit.Turn.Id);
        reloaded.MessageId.Should().Be(commit.Messages[0].Id);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(2L);

        var state = await fixture.Inputs.GetQueueStateAsync(conversationId);
        state.Items.Should().ContainSingle();
        state.Items[0].InputId.Should().NotBe(reloaded.Id);
        state.Items[0].Status.Should().Be(ConversationInputStatus.Pending);
    }

    [Fact]
    public async Task A_start_transaction_failure_leaves_no_orphan_turn_user_or_consumed_input()
    {
        var startCount = 0;
        using var fixture = new ConversationPersistenceFixture(startBeforeCommit: (stage, _) =>
            Interlocked.Increment(ref startCount) == 2 ? throw new InvalidOperationException(stage) : Task.CompletedTask);
        var start = await fixture.StartAsync("U0");
        var conversationId = start.Turn.ConversationId;
        var conversation = await fixture.Conversations.GetConversationAsync(conversationId) ?? throw new InvalidOperationException();
        await fixture.AcceptFollowUpAsync(conversationId, "follow up");

        var owner = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var claimed = await fixture.ClaimNextAsync(conversationId, owner, claimId);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.StartClaimedAsync(conversation, claimed, claimId, owner));
        exception.Message.Should().Be("start-before-commit");

        var reloaded = await fixture.Inputs.GetInputAsync(claimed.Id);
        reloaded!.Status.Should().Be(ConversationInputStatus.Claimed);
        reloaded.ConsumedTurnId.Should().BeNull();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_turns;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(1L);

        var retry = await fixture.StartClaimedAsync(conversation, reloaded, claimId, owner);
        retry.Messages.Should().ContainSingle().Which.MarkdownContent.Should().Be("follow up");
        (await fixture.Inputs.GetInputAsync(claimed.Id))!.Status.Should().Be(ConversationInputStatus.Consumed);
    }

    [Fact]
    public async Task A_stale_claim_revision_rolls_back_the_start_transaction()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        var conversation = await fixture.Conversations.GetConversationAsync(conversationId) ?? throw new InvalidOperationException();
        var input = await fixture.AcceptFollowUpAsync(conversationId, "follow up");
        var owner = Guid.NewGuid();
        var claimId = Guid.NewGuid();
        var claimed = await fixture.ClaimNextAsync(conversationId, owner, claimId);
        var stale = claimed with { Revision = claimed.Revision + 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.StartClaimedAsync(conversation, stale, claimId, owner));

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_turns;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 1;")).Should().Be(1L);
        (await fixture.Inputs.GetInputAsync(claimed.Id))!.Status.Should().Be(ConversationInputStatus.Claimed);
    }

    [Fact]
    public async Task Startup_recovery_holds_old_claims_stale_steers_and_pauses_without_replaying()
    {
        using var fixture = new ConversationPersistenceFixture();
        var active = await fixture.StartAsync("active");
        var claimedConversation = (await fixture.StartAsync("claimed")).Turn.ConversationId;
        var pendingConversation = (await fixture.StartAsync("pending")).Turn.ConversationId;

        await fixture.AcceptFollowUpAsync(claimedConversation, "claimed item");
        var claimed = await fixture.ClaimNextAsync(claimedConversation, Guid.NewGuid(), Guid.NewGuid());
        await fixture.AcceptFollowUpAsync(pendingConversation, "pending item");

        // A stale steer targets an already-terminal turn.
        await fixture.ExecuteAsync("""
            INSERT INTO conversation_inputs(id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                payload_json, status, revision, created_at_utc, updated_at_utc)
            VALUES($id, $conversation, $request, 9, 1, $turn, $payload, 0, 1, $at, $at);
            """, ("$id", Guid.NewGuid().ToString("D")), ("$conversation", active.Turn.ConversationId.ToString("D")),
            ("$request", "steer-1"), ("$turn", active.Turn.Id.ToString("D")),
            ("$payload", JsonSerializer.Serialize(new { version = 1, prompt = "stale steer" })),
            ("$at", DateTimeOffset.UtcNow.ToString("O")));
        await fixture.Turns.TryFinalizeTurnAsync(new(active.Turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow }, [], []));

        var states = await fixture.Inputs.RecoverStartupAsync();
        states.Should().NotBeEmpty();
        states.Should().OnlyContain(state => state.Paused);

        (await fixture.Inputs.GetInputAsync(claimed.Id))!.Status.Should().Be(ConversationInputStatus.Held);
        var pendingStatus = (long)(await fixture.ScalarAsync(
            "SELECT status FROM conversation_inputs WHERE conversation_id = $id;",
            ("$id", pendingConversation.ToString("D"))) ?? -1L);
        ((ConversationInputStatus)pendingStatus).Should().Be(ConversationInputStatus.Pending);
        (await fixture.ScalarAsync("SELECT status FROM conversation_inputs WHERE client_request_id = 'steer-1';")).Should().Be(3L);
        (await fixture.Inputs.TryClaimNextFollowUpAsync(pendingConversation, Guid.NewGuid(), Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Queue_disabled_rejects_a_busy_conversation_but_keeps_existing_inputs()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var conversationId = start.Turn.ConversationId;
        await fixture.AcceptFollowUpAsync(conversationId, "first", queueEnabled: true);

        var rejected = await fixture.Inputs.AcceptAsync(new ConversationInputAcceptRequest(
            conversationId, "second", ConversationInputKind.FollowUp, null, "second", fixture.Snapshot(), null, false));
        rejected.Status.Should().Be(ConversationInputAcceptStatus.Rejected);
        rejected.Reason.Should().Be(ConversationInputReason.QueueDisabled);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);
    }
}
