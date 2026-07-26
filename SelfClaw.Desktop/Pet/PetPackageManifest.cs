namespace SelfClaw.Desktop.Pet;

internal sealed record PetPackageManifest
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? SpritesheetPath { get; init; }

    public string? Author { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Source { get; init; }

    public string? SourceUrl { get; init; }

    public GridConfig? Grid { get; init; }
}
