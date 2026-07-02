namespace SelfClaw.Desktop.Pet;

/// <summary>
/// 桌宠的持久化配置,存入 <c>desktop-settings.json</c> 的 <c>"pet"</c> node。
/// 详见 docs/pet-system-design.md §8.1。
/// </summary>
public sealed record PetSettings
{
    /// <summary>宠物是否开启。</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 距所在屏幕工作区左上角的水平偏移(DIP)。为空表示尚未摆放过,首次显示时用默认位置。
    /// </summary>
    public double? OffsetX { get; init; }

    /// <summary>距所在屏幕工作区左上角的垂直偏移(DIP)。</summary>
    public double? OffsetY { get; init; }

    /// <summary>上次所在显示器的设备名(<c>Screen.DeviceName</c>);屏幕数量变化后用于择近恢复。</summary>
    public string? ScreenDeviceName { get; init; }

    /// <summary>
    /// 素材路径(spritesheet 所在的宠物包目录或文件)。为空时用内置默认宠物。
    /// </summary>
    public string? SpriteSheetPath { get; init; }

    /// <summary>缩放模式:true = NearestNeighbor(像素风),false = HighQuality(插画风)。</summary>
    public bool PixelArt { get; init; }

    /// <summary>网格布局覆盖;为空时回退到内置 Codex 8×9 默认(见 <see cref="GridConfig"/>)。</summary>
    public GridConfig? Grid { get; init; }
}

/// <summary>
/// Spritesheet 网格布局。同时用于 <see cref="PetSettings.Grid"/> 与宠物包 <c>pet.json</c> 的可选 <c>grid</c> 字段。
/// 布局来源优先级:pet.json 的 grid &gt; PetSettings.Grid &gt; 内置 Codex 8×9 默认。
/// </summary>
public sealed record GridConfig
{
    /// <summary>列数(默认 8)。</summary>
    public int Cols { get; init; }

    /// <summary>行数(默认 9)。</summary>
    public int Rows { get; init; }

    /// <summary>单格宽度(px);为 0 时可由整图宽 / Cols 推导。</summary>
    public int CellWidth { get; init; }

    /// <summary>单格高度(px);为 0 时可由整图高 / Rows 推导。</summary>
    public int CellHeight { get; init; }

    /// <summary>各行定义(行 = 动画状态)。</summary>
    public IReadOnlyList<RowDef> RowsDef { get; init; } = [];
}

/// <summary>Spritesheet 中一行的定义(一个动画状态)。</summary>
public sealed record RowDef
{
    /// <summary>行语义 id(idle / waving / running-right ...)。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>该行的有效帧数(未用满的列为透明,播放时按此截断)。</summary>
    public int Frames { get; init; }

    /// <summary>该行的播放帧率。</summary>
    public int Fps { get; init; }
}
