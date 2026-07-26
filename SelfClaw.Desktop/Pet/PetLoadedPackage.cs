namespace SelfClaw.Desktop.Pet;

internal sealed record PetLoadedPackage(
    string? PackageId,
    SpriteSheet SpriteSheet,
    string? Warning);
