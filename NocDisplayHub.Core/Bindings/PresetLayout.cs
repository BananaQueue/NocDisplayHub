namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// Maps a preset to its grid dimensions and cell pixel bounds. Every preset's
/// grid is the top-left subset of the largest grid (2x3, matching the 6-monitor
/// wall) — this is what lets LayoutManager preserve bindings for cells that
/// become hidden when switching to a smaller preset and restore them when
/// switching back.
/// </summary>
public static class PresetLayout
{
    public const int MaxRows = 2;
    public const int MaxCols = 3;

    public static (int Rows, int Cols) GetDimensions(Preset preset) => preset switch
    {
        Preset.OneByOne => (1, 1),
        Preset.OneByTwo => (1, 2),
        Preset.TwoByOne => (2, 1),
        Preset.TwoByTwo => (2, 2),
        Preset.TwoByThree => (2, 3),
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>
    /// The (row, col) slots visible for this preset, top-left aligned within the
    /// max 2x3 grid.
    /// </summary>
    public static IReadOnlyList<(int Row, int Col)> GetVisibleSlots(Preset preset)
    {
        var (rows, cols) = GetDimensions(preset);
        var slots = new List<(int, int)>(rows * cols);
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                slots.Add((row, col));
            }
        }
        return slots;
    }

    /// <summary>
    /// Pixel bounds per visible slot, evenly dividing the compositor window.
    /// TODO_HUB_SPEC: this assumes an even split. Replace with the hub's actual
    /// physical split-point coordinates once confirmed — real hubs commonly cut
    /// unevenly to account for bezel width.
    /// </summary>
    public static IReadOnlyDictionary<(int Row, int Col), CellBounds> GetBounds(Preset preset, double windowWidth, double windowHeight)
    {
        var (rows, cols) = GetDimensions(preset);
        var cellWidth = windowWidth / cols;
        var cellHeight = windowHeight / rows;

        var bounds = new Dictionary<(int, int), CellBounds>();
        foreach (var (row, col) in GetVisibleSlots(preset))
        {
            bounds[(row, col)] = new CellBounds(col * cellWidth, row * cellHeight, cellWidth, cellHeight);
        }
        return bounds;
    }
}
