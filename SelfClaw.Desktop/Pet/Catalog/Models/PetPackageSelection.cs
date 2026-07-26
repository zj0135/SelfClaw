namespace SelfClaw.Desktop.Pet;

internal sealed record PetPackageSelection(
    string? PackageId,
    string SpriteSheetPath,
    GridConfig? Grid);
