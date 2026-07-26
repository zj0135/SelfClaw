using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SelfClaw.Desktop.Pet;

/// <summary>
/// Validated spritesheet metadata plus frame slicing over a decoded bitmap.
/// </summary>
public sealed class SpriteSheet
{
    private readonly BitmapSource _source;
    private readonly Dictionary<(string RowId, int Frame), ImageSource> _frameCache = new();
    private readonly Dictionary<string, SpriteSheetRow> _rowsById;

    private SpriteSheet(
        BitmapSource source,
        int cellWidth,
        int cellHeight,
        IReadOnlyDictionary<string, SpriteSheetRow> rowsById)
    {
        _source = source;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        _rowsById = new Dictionary<string, SpriteSheetRow>(rowsById, StringComparer.Ordinal);
    }

    public int CellWidth { get; }

    public int CellHeight { get; }

    public IReadOnlyCollection<string> RowIds => _rowsById.Keys;

    public static SpriteSheet Create(BitmapSource source, GridConfig? gridOverride)
    {
        ArgumentNullException.ThrowIfNull(source);

        var grid = gridOverride ?? PetLayout.CreateDefaultGrid();
        var cols = grid.Cols > 0 ? grid.Cols : PetLayout.CreateDefaultGrid().Cols;
        var rows = grid.Rows > 0 ? grid.Rows : PetLayout.CreateDefaultGrid().Rows;

        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new InvalidOperationException("Pet spritesheet has invalid dimensions.");
        }

        var cellWidth = ResolveCellSize(grid.CellWidth, source.PixelWidth, cols, "width");
        var cellHeight = ResolveCellSize(grid.CellHeight, source.PixelHeight, rows, "height");

        if (cellWidth * cols > source.PixelWidth || cellHeight * rows > source.PixelHeight)
        {
            throw new InvalidOperationException(
                $"Pet spritesheet grid {cols}x{rows} with cells {cellWidth}x{cellHeight} exceeds image {source.PixelWidth}x{source.PixelHeight}.");
        }

        var rowDefs = grid.RowsDef.Count > 0 ? grid.RowsDef : PetLayout.CreateDefaultGrid().RowsDef;
        if (rowDefs.Count > rows)
        {
            throw new InvalidOperationException("Pet spritesheet row definitions exceed configured row count.");
        }

        var rowsById = new Dictionary<string, SpriteSheetRow>(StringComparer.Ordinal);
        for (var index = 0; index < rowDefs.Count; index++)
        {
            var rowDef = rowDefs[index];
            if (string.IsNullOrWhiteSpace(rowDef.Id))
            {
                throw new InvalidOperationException($"Pet spritesheet row {index} has no id.");
            }

            var frames = rowDef.Frames > 0 ? rowDef.Frames : 1;
            if (frames > cols)
            {
                throw new InvalidOperationException(
                    $"Pet spritesheet row '{rowDef.Id}' declares {frames} frames, but the grid has only {cols} columns.");
            }

            var fps = rowDef.Fps > 0 ? rowDef.Fps : 1;
            rowsById[rowDef.Id] = new SpriteSheetRow(rowDef.Id, index, frames, fps);
        }

        if (!rowsById.ContainsKey(PetLayout.IdleRowId))
        {
            throw new InvalidOperationException("Pet spritesheet layout must define an idle row.");
        }

        return new SpriteSheet(source, cellWidth, cellHeight, rowsById);
    }

    public SpriteSheetRow GetRow(string rowId)
    {
        if (_rowsById.TryGetValue(rowId, out var row))
        {
            return row;
        }

        throw new InvalidOperationException($"Pet spritesheet row '{rowId}' is not defined.");
    }

    public bool HasRow(string rowId)
    {
        return _rowsById.ContainsKey(rowId);
    }

    public ImageSource GetFrame(string rowId, int frameIndex)
    {
        var row = GetRow(rowId);
        var normalizedFrame = Math.Clamp(frameIndex, 0, row.Frames - 1);
        var key = (row.Id, normalizedFrame);

        if (_frameCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var rect = new Int32Rect(
            normalizedFrame * CellWidth,
            row.Index * CellHeight,
            CellWidth,
            CellHeight);
        var frame = new CroppedBitmap(_source, rect);
        if (frame.CanFreeze)
        {
            frame.Freeze();
        }

        _frameCache[key] = frame;
        return frame;
    }

    private static int ResolveCellSize(int configured, int imageSize, int count, string axis)
    {
        if (configured > 0)
        {
            return configured;
        }

        if (imageSize % count != 0)
        {
            throw new InvalidOperationException(
                $"Pet spritesheet {axis} {imageSize} is not divisible by configured count {count}.");
        }

        return imageSize / count;
    }
}
