namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// A rectangular block of grid slots merged into one bindable region — e.g. grouping
/// (0,0), (0,1), (1,0), (1,1) into a single 2x2 area. Anchored at its top-left slot
/// (<see cref="Row"/>, <see cref="Col"/>); every other member slot is absorbed into it and is
/// never independently rendered or bindable again while the group exists. The group's one
/// <see cref="CellBinding"/> lives in <see cref="LayoutManager"/>'s existing per-slot binding
/// dictionary, stored under the anchor's own (row, col) — grouping never needed a separate
/// storage mechanism, just a way to know which slots collapse into which anchor.
/// </summary>
public readonly record struct CellGroup(int Row, int Col, int RowSpan, int ColSpan)
{
    public bool Contains(int row, int col) =>
        row >= Row && row < Row + RowSpan && col >= Col && col < Col + ColSpan;
}
