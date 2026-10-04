using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.ConversationInputs;

/// <summary>
/// P4.6/P4.7/P4.8 host acceptance: target validation, the follow-up fallbacks and the stop
/// ordering that settles unconsumed steers before cancellation.
/// </summary>
public sealed class ConversationInputSteerServiceTests
{
    [Fact]
    public async Task A_steer_without_a_matching_live_turn_becomes_a_follow_up()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var busy = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;

        var result = await h.SubmitSteerAsync(id, Guid.NewGuid(), "late steer");

        result.Accepted.Should().BeTrue();
        result.Reason.Should().Be(ConversationInputReason.TargetTurnClosed);
        var stored = await h.Fixture.Inputs.GetInputAsync(result.InputId!.Value);
        stored!.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.TargetTurnId.Should().BeNull();
        stored.ReasonCode.Should().Be(ConversationInputReason.TargetTurnClosed);
        h.Runs.Complete(busy, false);
    }

    [Fact]
    public async Task A_steer_during_a_continuation_becomes_a_follow_up()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var conversation = (await h.Fixture.Conversations.GetConversationAsync(id))!;
        var continuation = (await h.Runs.TryReserveContinuationAsync(conversation))!;

        var result = await h.SubmitSteerAsync(id, continuation.TurnId, "during continuation");

        result.Accepted.Should().BeTrue();
        result.Reason.Should().Be(ConversationInputReason.ContinuationActive);
        var stored = await h.Fixture.Inputs.GetInputAsync(result.InputId!.Value);
        stored!.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.TargetTurnId.Should().BeNull();
        h.Runs.Complete(continuation, false);
    }

    [Fact]
    public async Task A_steer_during_a_live_direct_turn_is_durably_targeted_and_pulses_the_session()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        var session = handle.InputSession!;

        var result = await h.SubmitSteerAsync(id, handle.TurnId, "adjust now");

        result.Accepted.Should().BeTrue();
        result.Outcome.Should().Be(ConversationInputSubmitOutcome.Queued);
        var stored = await h.Fixture.Inputs.GetInputAsync(result.InputId!.Value);
        stored!.Kind.Should().Be(ConversationInputKind.Steer);
        stored.TargetTurnId.Should().Be(handle.TurnId);
        session.HasSupersedingSteer.Should().BeFalse("the loop has not claimed the batch yet");
        await session.WaitForSteerPulseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task A_blocked_steer_policy_accepts_the_input_as_a_follow_up()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        await handle.InputSession!.SetSteerPolicyAsync(ConversationInputReason.HookRequiresNewTurn);

        var result = await h.SubmitSteerAsync(id, handle.TurnId, "must not bypass the hook");

        result.Accepted.Should().BeTrue();
        result.Reason.Should().Be(ConversationInputReason.HookRequiresNewTurn);
        var stored = await h.Fixture.Inputs.GetInputAsync(result.InputId!.Value);
        stored!.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.TargetTurnId.Should().BeNull();
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task Stop_pauses_and_holds_unconsumed_steers_before_cancelling_the_run()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        var steer = await h.SubmitSteerAsync(id, handle.TurnId, "held by stop");
        steer.Accepted.Should().BeTrue();

        await h.Service.StopAsync(id);

        handle.CancellationToken.IsCancellationRequested.Should().BeTrue();
        var stored = await h.Fixture.Inputs.GetInputAsync(steer.InputId!.Value);
        stored!.Status.Should().Be(ConversationInputStatus.Held);
        stored.ReasonCode.Should().Be(ConversationInputReason.TurnCancelled);
        (await h.Service.GetStateAsync(id)).Paused.Should().BeTrue();
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task A_cli_agent_cannot_submit_a_steer()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var submission = h.Submission(id, "cli steer") with
        {
            Agent = new AgentRuntimeDefinition("build", "Builder", "test", AgentExecutionMode.Cli,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], ""),
            Kind = ConversationInputKind.Steer,
            TargetTurnId = Guid.NewGuid()
        };

        var result = await h.Service.SubmitAsync(submission);

        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be(ConversationInputReason.DirectOnly);
    }

    [Fact]
    public async Task A_steer_submitted_after_the_session_close_becomes_a_follow_up()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        // The final boundary read with no pending steer is the close.
        (await handle.InputSession!.ReadFinalBoundaryAsync()).Should().BeNull();
        handle.InputSession.SteerIntakeClosed.Should().BeTrue();

        var result = await h.SubmitSteerAsync(id, handle.TurnId, "after close");

        result.Accepted.Should().BeTrue();
        result.Reason.Should().Be(ConversationInputReason.TargetTurnClosed);
        var stored = await h.Fixture.Inputs.GetInputAsync(result.InputId!.Value);
        stored!.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.TargetTurnId.Should().BeNull();
        stored.Status.Should().Be(ConversationInputStatus.Pending);
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task A_provider_failure_holds_an_unconsumed_steer_and_pauses_the_queue()
    {
        var runtime = new FailingGatedRuntime();
        using var h = new ConversationInputTestHarness(runtime: runtime);
        var id = await h.CreateConversationAsync();
        await h.SubmitAsync(id, "A");
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var turn = h.Runs.GetActiveRun(id)!.TurnId;
        var steer = await h.SubmitSteerAsync(id, turn, "B");

        runtime.Release.TrySetResult();
        await h.WaitForAsync(async () =>
            (await h.Fixture.Inputs.GetInputAsync(steer.InputId!.Value))?.Status == ConversationInputStatus.Held);

        await h.WaitForAsync(async () => (await h.Service.GetStateAsync(id)).Paused);
        var stored = await h.Fixture.Inputs.GetInputAsync(steer.InputId!.Value);
        stored!.ReasonCode.Should().Be(ConversationInputReason.TurnFailed);
    }

    [Fact]
    public async Task Promote_converts_a_pending_follow_up_into_a_steer_for_the_live_turn()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        var followUp = await h.SubmitAsync(id, "promote me");
        followUp.Accepted.Should().BeTrue();
        followUp.Outcome.Should().Be(ConversationInputSubmitOutcome.Queued);
        var stored = (await h.Fixture.Inputs.GetInputAsync(followUp.InputId!.Value))!;

        var result = await h.Queue.PromoteAsync(followUp.InputId!.Value, followUp.Revision, handle.TurnId);

        result.Status.Should().Be(ConversationInputUpdateStatus.Applied);
        result.Input!.Id.Should().Be(followUp.InputId!.Value);
        result.Input.Sequence.Should().Be(stored.Sequence);
        result.Input.Kind.Should().Be(ConversationInputKind.Steer);
        result.Input.TargetTurnId.Should().Be(handle.TurnId);
        result.Input.Status.Should().Be(ConversationInputStatus.Pending);
        (await h.Fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(1L);
        handle.InputSession!.HasSupersedingSteer.Should().BeFalse("the loop has not claimed the batch yet");
        await handle.InputSession.WaitForSteerPulseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task Promote_rejects_a_stale_revision_or_a_target_that_is_not_the_live_turn()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        var followUp = await h.SubmitAsync(id, "stay a follow-up");

        var stale = await h.Queue.PromoteAsync(followUp.InputId!.Value, followUp.Revision + 5, handle.TurnId);
        stale.Status.Should().Be(ConversationInputUpdateStatus.Conflict);

        var wrongTurn = await h.Queue.PromoteAsync(followUp.InputId!.Value, followUp.Revision, Guid.NewGuid());
        wrongTurn.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        wrongTurn.Reason.Should().Be(ConversationInputReason.TargetTurnClosed);

        var stored = (await h.Fixture.Inputs.GetInputAsync(followUp.InputId!.Value))!;
        stored.Kind.Should().Be(ConversationInputKind.FollowUp);
        stored.TargetTurnId.Should().BeNull();
        h.Runs.Complete(handle, false);
    }

    [Fact]
    public async Task Promote_respects_the_published_policy_and_the_explicit_skill_rule()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        await handle.InputSession!.SetSteerPolicyAsync(ConversationInputReason.HookRequiresNewTurn);
        var blocked = await h.SubmitAsync(id, "blocked promote");

        var blockedResult = await h.Queue.PromoteAsync(blocked.InputId!.Value, blocked.Revision, handle.TurnId);

        blockedResult.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        blockedResult.Reason.Should().Be(ConversationInputReason.HookRequiresNewTurn);

        using var other = new ConversationInputTestHarness();
        var otherId = await other.CreateConversationAsync();
        var otherHandle = other.Runs.TryReserve(otherId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await other.StartReservedTurnAsync(otherId, otherHandle.TurnId);
        var skill = await other.SubmitAsync(otherId, "[/pdf] summarize");

        var skillResult = await other.Queue.PromoteAsync(skill.InputId!.Value, skill.Revision, otherHandle.TurnId);

        skillResult.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        skillResult.Reason.Should().Be(ConversationInputReason.SkillRequiresNewTurn);
        (await other.Fixture.Inputs.GetInputAsync(skill.InputId!.Value))!.Kind.Should().Be(ConversationInputKind.FollowUp);
        h.Runs.Complete(handle, false);
        other.Runs.Complete(otherHandle, false);
    }

    [Fact]
    public async Task Promote_that_loses_the_close_race_stays_a_follow_up()
    {
        using var h = new ConversationInputTestHarness();
        var id = await h.CreateConversationAsync();
        var handle = h.Runs.TryReserve(id, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive)!;
        await h.StartReservedTurnAsync(id, handle.TurnId);
        var followUp = await h.SubmitAsync(id, "close race");
        (await handle.InputSession!.ReadFinalBoundaryAsync()).Should().BeNull();

        var result = await h.Queue.PromoteAsync(followUp.InputId!.Value, followUp.Revision, handle.TurnId);

        result.Status.Should().Be(ConversationInputUpdateStatus.Invalid);
        result.Reason.Should().Be(ConversationInputReason.TargetTurnClosed);
        (await h.Fixture.Inputs.GetInputAsync(followUp.InputId!.Value))!.Kind.Should().Be(ConversationInputKind.FollowUp);
        h.Runs.Complete(handle, false);
    }

    private sealed class FailingGatedRuntime : IAgentChatRuntime
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<AgentStreamEvent> StreamTurnAsync(ChatTurnRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new RunCompletedEvent(RunCompletionStatus.Failed, null, "provider failed");
        }
    }
}
