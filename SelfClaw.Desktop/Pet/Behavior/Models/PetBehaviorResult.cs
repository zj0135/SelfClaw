namespace SelfClaw.Desktop.Pet;

internal sealed record PetBehaviorResult(
    PetInteraction Interaction,
    PetWorkState WorkState,
    string AnimationRowId,
    PetTimerCommand WaitingTimer,
    PetTimerCommand AmbientTimer);
