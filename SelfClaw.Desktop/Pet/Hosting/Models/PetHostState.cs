namespace SelfClaw.Desktop.Pet;

public sealed record PetHostState(
    bool IsVisible,
    PetSettings Settings,
    string SelectedBuiltInPetId,
    IReadOnlyList<PetPackageSummary> BuiltInPackages,
    string? ActualPetId = null,
    string LoadStatus = "unloaded",
    string? LoadError = null,
    long Revision = 0);
