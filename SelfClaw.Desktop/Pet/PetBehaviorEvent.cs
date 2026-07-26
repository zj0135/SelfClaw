namespace SelfClaw.Desktop.Pet;

internal sealed record PetBehaviorEvent(
    PetBehaviorEventKind Kind,
    PetInteraction DragInteraction = PetInteraction.Idle,
    bool IsHovering = false,
    PetWorkState WorkState = PetWorkState.None);
