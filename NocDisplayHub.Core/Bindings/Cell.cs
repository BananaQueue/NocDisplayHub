namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// A single visible leaf cell to render. When a top-level cell is split into
/// a sub-grid (Task: "splitting one monitor into a grid of smaller widgets"),
/// each sub-widget is also represented as a Cell — SubRow/SubCol identify it
/// within its parent, and Bounds is already the absolute pixel rect, so the
/// compositor and watchdog never need to know nesting exists.
/// </summary>
public sealed class Cell
{
    public required int Row { get; init; }
    public required int Col { get; init; }
    public int? SubRow { get; init; }
    public int? SubCol { get; init; }
    public required CellBounds Bounds { get; init; }
    public CellBinding? Binding { get; init; }
    public CellStatus Status { get; init; }

    public string Label => SubRow is null ? $"Cell({Row},{Col})" : $"Cell({Row},{Col})/Sub({SubRow},{SubCol})";
}
