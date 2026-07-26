namespace SelfClaw.Desktop.Pet;

public sealed record GridConfig
{
    public int Cols { get; init; }

    public int Rows { get; init; }

    public int CellWidth { get; init; }

    public int CellHeight { get; init; }

    public IReadOnlyList<RowDef> RowsDef { get; init; } = [];
}
