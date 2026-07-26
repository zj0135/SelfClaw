namespace SelfClaw.Desktop.Pet;

public sealed record PetHostCommand(
    PetHostCommandKind Kind,
    string? PetId = null);
