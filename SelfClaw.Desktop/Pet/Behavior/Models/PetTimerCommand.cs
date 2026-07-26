namespace SelfClaw.Desktop.Pet;

internal sealed record PetTimerCommand(PetTimerOperation Operation, TimeSpan? Delay = null);
