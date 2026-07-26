namespace SelfClaw.Desktop.Pet;

public sealed record PetPackageSummary(
    string Id,
    string DisplayName,
    string Description,
    string? Author,
    IReadOnlyList<string> Tags,
    string? Source,
    string? SourceUrl,
    string PreviewAssetPath,
    int Columns,
    int Rows);
