namespace SelfClaw.Desktop.Pet;

public sealed record PetHostState(
    bool IsVisible,
    PetSettings Settings,
    string SelectedBuiltInPetId,
    IReadOnlyList<PetPackageSummary> BuiltInPackages);
