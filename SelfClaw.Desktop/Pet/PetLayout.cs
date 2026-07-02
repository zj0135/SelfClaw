namespace SelfClaw.Desktop.Pet;

/// <summary>
/// Shared spritesheet row identifiers and the built-in Codex 8x9 layout.
/// </summary>
public static class PetLayout
{
    public const string IdleRowId = "idle";
    public const string WavingRowId = "waving";
    public const string RunningRightRowId = "running-right";
    public const string RunningLeftRowId = "running-left";
    public const string JumpingRowId = "jumping";
    public const string WaitingRowId = "waiting";

    public static string GetRowId(PetInteraction interaction)
    {
        return interaction switch
        {
            PetInteraction.Hover => WavingRowId,
            PetInteraction.DragRight => RunningRightRowId,
            PetInteraction.DragLeft => RunningLeftRowId,
            PetInteraction.DragUp => JumpingRowId,
            PetInteraction.DragDown => WavingRowId,
            PetInteraction.Waiting => WaitingRowId,
            _ => IdleRowId,
        };
    }

    public static GridConfig CreateDefaultGrid()
    {
        return new GridConfig
        {
            Cols = 8,
            Rows = 9,
            CellWidth = 192,
            CellHeight = 208,
            RowsDef =
            [
                new RowDef { Id = "idle", Frames = 6, Fps = 6 },
                new RowDef { Id = "running-right", Frames = 8, Fps = 8 },
                new RowDef { Id = "running-left", Frames = 8, Fps = 8 },
                new RowDef { Id = "waving", Frames = 4, Fps = 6 },
                new RowDef { Id = "jumping", Frames = 5, Fps = 7 },
                new RowDef { Id = "failed", Frames = 8, Fps = 7 },
                new RowDef { Id = "waiting", Frames = 6, Fps = 6 },
                new RowDef { Id = "running", Frames = 6, Fps = 8 },
                new RowDef { Id = "review", Frames = 6, Fps = 6 },
            ],
        };
    }
}
