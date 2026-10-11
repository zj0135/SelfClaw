using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

/// <summary>
/// P4.1/P4.7 repository contracts: the pending high-water claim and the atomic close inside the
/// terminal transaction.
/// </summary>
public sealed class SqliteConversationSteerRepositoryTests
{
    [Fact]
    public async Task Pending_steers_are_claimed_as_one_ordered_high_water_batch_and_later_inputs_stay_pending()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S1", 1);
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S2", 2);

        var batch = await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);

        batch.Should().NotBeNull();
        batch!.Inputs.Select(input => input.Prompt).Should().Equal("S1", "S2");
        batch.Inputs.Select(input => input.Sequence).Should().BeInAscendingOrder();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S3", 3);
        var firstBatchClaim = batch.ClaimId;
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 0;")).Should().Be(1L);

        var second = await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);

        second!.ClaimId.Should().NotBe(firstBatchClaim);
        second.Inputs.Select(input => input.Prompt).Should().Equal("S3");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;")).Should().Be(3L);
    }

    [Fact]
    public async Task A_closed_target_turn_cannot_be_claimed_again()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S1");
        await fixture.Turns.TryFinalizeTurnAsync(new(start.Turn with
        {
            Status = ConversationTurnStatus.Failed,
            CompletedAtUtc = DateTimeOffset.UtcNow
        }, [], []));

        (await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, Guid.NewGuid()))
            .Should().BeNull();
        (await fixture.ScalarAsync("SELECT status FROM conversation_inputs;")).Should().Be(3L);
        (await fixture.ScalarAsync("SELECT reason_code FROM conversation_inputs;")).Should().Be(ConversationInputReason.TurnFailed);
    }

    [Fact]
    public async Task A_successful_close_holds_unconsumed_steers_instead_of_reopening_them()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S1", 1);
        var claimed = await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S2", 2);

        await fixture.Turns.TryFinalizeTurnAsync(new(start.Turn with
        {
            Status = ConversationTurnStatus.Succeeded,
            CompletedAtUtc = DateTimeOffset.UtcNow
        }, [], []));

        claimed.Should().NotBeNull();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 3;"))
            .Should().Be(2L, "a claimed input must not silently become a cancellable follow-up");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE kind = 1;"))
            .Should().Be(2L, "a never-consumed steer stays a steer until the user resolves it");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE reason_code = 'target-turn-closed';"))
            .Should().Be(2L);
        (await fixture.Inputs.HasUnprocessedInputsAsync(start.Turn.ConversationId)).Should().BeTrue();
        (await fixture.Inputs.TryClaimNextFollowUpAsync(start.Turn.ConversationId, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_explicit_skill_token_steer_is_converted_to_a_follow_up_at_claim_time()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "please [/pdf] now", 2);
        await fixture.SeedRawPendingSteerAsync(start.Turn, "plain steer", 3);

        var batch = await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);

        batch!.Inputs.Select(input => input.Prompt).Should().Equal("plain steer");
        (await fixture.ScalarAsync("SELECT kind FROM conversation_inputs WHERE reason_code = 'skill-requires-new-turn';"))
            .Should().Be(0L);
        (await fixture.ScalarAsync("SELECT status FROM conversation_inputs WHERE reason_code = 'skill-requires-new-turn';"))
            .Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;"))
            .Should().Be(1L, "only the plain steer stays claimed");
    }

    [Theory]
    [InlineData(ConversationTurnStatus.Cancelled, ConversationInputReason.TurnCancelled)]
    [InlineData(ConversationTurnStatus.Failed, ConversationInputReason.TurnFailed)]
    [InlineData(ConversationTurnStatus.Truncated, ConversationInputReason.TurnTruncated)]
    [InlineData(ConversationTurnStatus.Blocked, ConversationInputReason.TurnBlocked)]
    [InlineData(ConversationTurnStatus.Interrupted, ConversationInputReason.TurnInterrupted)]
    public async Task An_abnormal_close_holds_pending_and_claimed_steers_with_the_mapped_reason(
        ConversationTurnStatus status, string expectedReason)
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S1", 1);
        await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);
        await fixture.SeedRawPendingSteerAsync(start.Turn, "S2", 2);

        await fixture.Turns.TryFinalizeTurnAsync(new(start.Turn with
        {
            Status = status,
            CompletedAtUtc = DateTimeOffset.UtcNow
        }, [], []));

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 3;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE reason_code = $reason;",
            ("$reason", expectedReason))).Should().Be(2L);
        (await fixture.Inputs.HasUnprocessedInputsAsync(start.Turn.ConversationId)).Should().BeTrue();
        (await fixture.Inputs.TryClaimNextFollowUpAsync(start.Turn.ConversationId, Guid.NewGuid(), Guid.NewGuid()))
            .Should().BeNull();
    }

    [Fact]
    public async Task Releasing_for_a_blocked_policy_only_touches_unclaimed_pending_steers()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        await fixture.SeedRawPendingSteerAsync(start.Turn, "claimed", 1);
        var claimed = await fixture.Inputs.TryClaimPendingSteersAsync(start.Turn.ConversationId, start.Turn.Id, owner);
        await fixture.SeedRawPendingSteerAsync(start.Turn, "pending", 2);

        var changed = await fixture.Inputs.ReleasePendingSteersToFollowUpAsync(start.Turn.ConversationId, start.Turn.Id,
            ConversationInputReason.HookRequiresNewTurn);

        changed.Should().Be(1);
        (await fixture.ScalarAsync("SELECT kind FROM conversation_inputs WHERE status = 1;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT reason_code FROM conversation_inputs WHERE status = 0;"))
            .Should().Be(ConversationInputReason.HookRequiresNewTurn);
        (await fixture.Inputs.ReadConsumptionAsync(claimed!)).Should().BeNull();
    }

    [Fact]
    public async Task Promote_converts_the_same_pending_row_and_rejects_closed_or_skill_targets()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var input = await fixture.AcceptFollowUpAsync(start.Turn.ConversationId, "promote me");

        var promoted = await fixture.Inputs.PromoteAsync(new ConversationInputPromoteRequest(input.Id, input.Revision, start.Turn.Id));

        promoted.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        promoted.Input!.Id.Should().Be(input.Id);
        promoted.Input.Sequence.Should().Be(input.Sequence);
        promoted.Input.Kind.Should().Be(ConversationInputKind.Steer);
        promoted.Input.TargetTurnId.Should().Be(start.Turn.Id);
        promoted.Input.Status.Should().Be(ConversationInputStatus.Pending);
        promoted.QueueRevision.Should().BeGreaterThan(0);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);

        var stale = await fixture.Inputs.PromoteAsync(new ConversationInputPromoteRequest(input.Id, input.Revision, start.Turn.Id));
        stale.Status.Should().Be(ConversationInputUpdateStatus.Conflict);

        var skill = await fixture.AcceptFollowUpAsync(start.Turn.ConversationId, "[/pdf] summarize");
        var skillResult = await fixture.Inputs.PromoteAsync(new ConversationInputPromoteRequest(skill.Id, skill.Revision, start.Turn.Id));
        skillResult.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        skillResult.Reason.Should().Be(ConversationInputReason.SkillRequiresNewTurn);
        (await fixture.Inputs.GetInputAsync(skill.Id))!.Kind.Should().Be(ConversationInputKind.FollowUp);

        await fixture.Turns.TryFinalizeTurnAsync(new(start.Turn with
        {
            Status = ConversationTurnStatus.Succeeded,
            CompletedAtUtc = DateTimeOffset.UtcNow
        }, [], []));
        var late = await fixture.AcceptFollowUpAsync(start.Turn.ConversationId, "after close");
        var lateResult = await fixture.Inputs.PromoteAsync(new ConversationInputPromoteRequest(late.Id, late.Revision, start.Turn.Id));
        lateResult.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        lateResult.Reason.Should().Be(ConversationInputReason.TargetTurnClosed);
        (await fixture.Inputs.GetInputAsync(late.Id))!.Kind.Should().Be(ConversationInputKind.FollowUp);
    }
}
