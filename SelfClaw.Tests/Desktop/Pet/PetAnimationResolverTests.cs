using FluentAssertions;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetAnimationResolverTests
{
    [Theory]
    [InlineData(PetWorkState.Working, PetLayout.ReviewRowId)]
    [InlineData(PetWorkState.Reviewing, PetLayout.ReviewRowId)]
    [InlineData(PetWorkState.Running, PetLayout.RunningRowId)]
    [InlineData(PetWorkState.AwaitingApproval, PetLayout.WaitingRowId)]
    [InlineData(PetWorkState.Succeeded, PetLayout.WavingRowId)]
    [InlineData(PetWorkState.Failed, PetLayout.FailedRowId)]
    public void ResolveRowId_maps_work_state(PetWorkState workState, string expectedRowId)
    {
        PetAnimationResolver.ResolveRowId(PetInteraction.Idle, workState)
            .Should().Be(expectedRowId);
    }

    [Fact]
    public void ResolveRowId_prioritizes_direct_user_interaction()
    {
        PetAnimationResolver.ResolveRowId(PetInteraction.DragLeft, PetWorkState.Failed)
            .Should().Be(PetLayout.RunningLeftRowId);
    }
}
