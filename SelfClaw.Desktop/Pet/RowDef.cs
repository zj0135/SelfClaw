namespace SelfClaw.Desktop.Pet;

public sealed record RowDef
{
    public string Id { get; init; } = string.Empty;

    public int Frames { get; init; }

    public int Fps { get; init; }
}
