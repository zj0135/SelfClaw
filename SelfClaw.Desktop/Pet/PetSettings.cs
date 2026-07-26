namespace SelfClaw.Desktop.Pet;

public sealed record PetSettings
{
    public bool Enabled { get; init; }

    public double? OffsetX { get; init; }

    public double? OffsetY { get; init; }

    public string? ScreenDeviceName { get; init; }

    public string? SpriteSheetPath { get; init; }

    public bool PixelArt { get; init; }

    public GridConfig? Grid { get; init; }
}
