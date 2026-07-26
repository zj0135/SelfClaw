using FluentAssertions;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetBehaviorTests
{
    [Theory]
    [InlineData(PetWorkState.Working, PetLayout.ReviewRowId)]
    [InlineData(PetWorkState.Reviewing, PetLayout.ReviewRowId)]
    [InlineData(PetWorkState.Running, PetLayout.RunningRowId)]
    [InlineData(PetWorkState.AwaitingApproval, PetLayout.WaitingRowId)]
    [InlineData(PetWorkState.Succeeded, PetLayout.WavingRowId)]
    [InlineData(PetWorkState.Failed, PetLayout.FailedRowId)]
    [InlineData(PetWorkState.Cancelled, PetLayout.WaitingRowId)]
    public void Work_state_selects_the_expected_animation(
        PetWorkState workState,
        string expectedRowId)
    {
        var behavior = CreateStartedBehavior();

        var result = behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: workState));

        result.AnimationRowId.Should().Be(expectedRowId);
        result.WaitingTimer.Operation.Should().Be(PetTimerOperation.Stop);
        result.AmbientTimer.Operation.Should().Be(PetTimerOperation.Stop);
    }

    [Fact]
    public void Direct_interaction_overrides_work_and_release_restores_it()
    {
        var behavior = CreateStartedBehavior();
        behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: PetWorkState.Failed));

        behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.PointerEntered))
            .AnimationRowId.Should().Be(PetLayout.WavingRowId);
        behavior.Apply(new PetBehaviorEvent(
                PetBehaviorEventKind.DragDirectionChanged,
                DragInteraction: PetInteraction.DragLeft))
            .AnimationRowId.Should().Be(PetLayout.RunningLeftRowId);

        var released = behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.PointerReleased,
            IsHovering: false));

        released.AnimationRowId.Should().Be(PetLayout.FailedRowId);
        released.WorkState.Should().Be(PetWorkState.Failed);
    }

    [Fact]
    public void Ambient_play_is_scheduled_only_while_idle_and_is_interrupted_by_pointer_input()
    {
        var behavior = CreateBehavior();
        var started = behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.AnimationStarted));

        started.AmbientTimer.Operation.Should().Be(PetTimerOperation.Restart);
        started.AmbientTimer.Delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4));

        var ambient = behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.AmbientElapsed));
        PetLayout.AmbientRowIds.Should().Contain(ambient.AnimationRowId);
        ambient.AmbientTimer.Operation.Should().Be(PetTimerOperation.Restart);

        var interrupted = behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.PointerEntered));
        interrupted.AnimationRowId.Should().Be(PetLayout.WavingRowId);
        interrupted.AmbientTimer.Operation.Should().Be(PetTimerOperation.Stop);

        behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.AmbientElapsed))
            .AnimationRowId.Should().Be(PetLayout.WavingRowId);
    }

    [Fact]
    public void Work_suspends_idle_schedules_and_none_resumes_them()
    {
        var behavior = CreateStartedBehavior();

        var running = behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: PetWorkState.Running));
        running.WaitingTimer.Operation.Should().Be(PetTimerOperation.Stop);
        running.AmbientTimer.Operation.Should().Be(PetTimerOperation.Stop);

        behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.WaitingElapsed))
            .AnimationRowId.Should().Be(PetLayout.RunningRowId);

        var idle = behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: PetWorkState.None));
        idle.AnimationRowId.Should().Be(PetLayout.IdleRowId);
        idle.WaitingTimer.Operation.Should().Be(PetTimerOperation.Restart);
        idle.AmbientTimer.Operation.Should().Be(PetTimerOperation.Restart);

        behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.WaitingElapsed))
            .AnimationRowId.Should().Be(PetLayout.WaitingRowId);
    }

    [Fact]
    public void Missing_work_row_uses_the_declared_fallback_order()
    {
        var behavior = new PetBehavior(new Random(0));
        behavior.ConfigureRows([PetLayout.IdleRowId, PetLayout.WaitingRowId]);

        var result = behavior.Apply(new PetBehaviorEvent(
            PetBehaviorEventKind.WorkStateChanged,
            WorkState: PetWorkState.Failed));

        result.AnimationRowId.Should().Be(PetLayout.WaitingRowId);
    }

    private static PetBehavior CreateStartedBehavior()
    {
        var behavior = CreateBehavior();
        behavior.Apply(new PetBehaviorEvent(PetBehaviorEventKind.AnimationStarted));
        return behavior;
    }

    private static PetBehavior CreateBehavior()
    {
        var behavior = new PetBehavior(new Random(0));
        behavior.ConfigureRows(PetLayout.CreateDefaultGrid().RowsDef.Select(row => row.Id));
        return behavior;
    }
}
