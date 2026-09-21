using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// Owns the current preset, every cell binding (including bindings for slots not currently
/// visible), and any groups merging several adjacent slots into one bigger bindable region.
/// Switching presets never discards an individual binding — it only changes which (row, col)
/// slots are visible — but it does clear groups (see <see cref="SetPreset"/>), since a group's
/// shape is only meaningful for the specific grid it was drawn against.
/// </summary>
public sealed class LayoutManager
{
    private readonly Dictionary<(int Row, int Col), CellBinding> _bindings = new();
    private readonly List<CellGroup> _groups = [];

    public Preset CurrentPreset { get; private set; } = Preset.TwoByThree;

    /// <summary>Every active group, anchored top-left — used by the editor to know which cell owns which merged region.</summary>
    public IReadOnlyList<CellGroup> Groups => _groups;

    public void SetPreset(Preset preset)
    {
        if (preset == CurrentPreset) return;
        CurrentPreset = preset;

        // A group's shape (e.g. "the top-left 2x2 block") is only meaningful for the exact grid
        // it was created against — a different preset may not even have those slots. Rather than
        // try to reconcile an arbitrary group shape against a different grid, switching layouts
        // starts fresh on grouping, the same way this app already treats grouping-like features
        // (see the removed "split into widgets" feature) as not worth carrying across a preset
        // change. Individual cell bindings are untouched — only the grouping itself resets, and
        // a group's anchor keeps its binding as an ordinary single-cell binding afterward.
        _groups.Clear();
    }

    public void AssignBinding(int row, int col, CellBinding binding)
    {
        var anchor = ResolveAnchor(row, col);
        _bindings[anchor] = binding;
    }

    public void ClearBinding(int row, int col) => _bindings.Remove(ResolveAnchor(row, col));

    public CellBinding? GetBinding(int row, int col) => _bindings.GetValueOrDefault(ResolveAnchor(row, col));

    /// <summary>The group (row, col) belongs to, if any — checked from either its anchor or any other member slot.</summary>
    public CellGroup? GetGroupContaining(int row, int col)
    {
        foreach (var group in _groups)
        {
            if (group.Contains(row, col)) return group;
        }
        return null;
    }

    /// <summary>Redirects a member slot to its group's anchor; an ungrouped slot resolves to itself.</summary>
    private (int Row, int Col) ResolveAnchor(int row, int col) =>
        GetGroupContaining(row, col) is { } group ? (group.Row, group.Col) : (row, col);

    /// <summary>
    /// Merges several currently-visible, ungrouped slots into one bindable region — but only if
    /// they form an exact, gap-free rectangular block (e.g. all four slots of a 2x2 area, or a
    /// full column). Anything else (a non-rectangular shape, a slot outside the current preset, a
    /// slot that's already part of another group) is refused with a reason in
    /// <paramref name="error"/> rather than silently doing something unexpected. The merged
    /// region's binding starts as whatever its top-left (anchor) slot already had; every other
    /// member's own binding is discarded, since it's no longer independently reachable once grouped.
    /// </summary>
    public bool TryGroupCells(IReadOnlyCollection<(int Row, int Col)> cells, out string? error)
    {
        var distinct = new HashSet<(int Row, int Col)>(cells);
        if (distinct.Count < 2)
        {
            error = "Select at least two cells to group.";
            return false;
        }

        var visibleSlots = new HashSet<(int Row, int Col)>(PresetLayout.GetVisibleSlots(CurrentPreset));
        foreach (var cell in distinct)
        {
            if (!visibleSlots.Contains(cell))
            {
                error = "Selection includes a slot that isn't part of the current layout.";
                return false;
            }
            if (GetGroupContaining(cell.Row, cell.Col) is not null)
            {
                error = "Selection includes a cell that's already part of a group.";
                return false;
            }
        }

        var minRow = distinct.Min(c => c.Row);
        var maxRow = distinct.Max(c => c.Row);
        var minCol = distinct.Min(c => c.Col);
        var maxCol = distinct.Max(c => c.Col);
        var expectedCount = (maxRow - minRow + 1) * (maxCol - minCol + 1);
        if (distinct.Count != expectedCount)
        {
            error = "Selection must be a solid rectangular block with no gaps.";
            return false;
        }

        var anchor = (Row: minRow, Col: minCol);
        // Keep whatever the anchor (top-left) slot already had; every other member's binding is
        // discarded now that it's no longer an independently reachable cell — there's no
        // principled way to pick among several members' bindings otherwise.
        foreach (var cell in distinct)
        {
            if (cell != anchor) _bindings.Remove(cell);
        }
        _groups.Add(new CellGroup(minRow, minCol, maxRow - minRow + 1, maxCol - minCol + 1));
        error = null;
        return true;
    }

    /// <summary>Dissolves the group containing (row, col), if any. The anchor keeps the group's binding; every other member starts Unassigned.</summary>
    public void Ungroup(int row, int col)
    {
        if (GetGroupContaining(row, col) is not { } group) return;
        _groups.Remove(group);
    }

    public IReadOnlyList<Cell> GetVisibleCells(double windowWidth, double windowHeight)
    {
        var bounds = PresetLayout.GetBounds(CurrentPreset, windowWidth, windowHeight);
        var cells = new List<Cell>();
        var emittedGroupAnchors = new HashSet<(int Row, int Col)>();

        foreach (var (row, col) in PresetLayout.GetVisibleSlots(CurrentPreset))
        {
            if (GetGroupContaining(row, col) is { } group)
            {
                if (!emittedGroupAnchors.Add((group.Row, group.Col))) continue; // already emitted from an earlier member slot
                var binding = GetBinding(group.Row, group.Col);
                cells.Add(new Cell
                {
                    Row = group.Row,
                    Col = group.Col,
                    Bounds = UnionBounds(group, bounds),
                    Binding = binding,
                    Status = binding is null ? CellStatus.Unbound : CellStatus.Healthy,
                });
                continue;
            }

            var plainBinding = GetBinding(row, col);
            cells.Add(new Cell
            {
                Row = row,
                Col = col,
                Bounds = bounds[(row, col)],
                Binding = plainBinding,
                Status = plainBinding is null ? CellStatus.Unbound : CellStatus.Healthy,
            });
        }
        return cells;
    }

    private static CellBounds UnionBounds(CellGroup group, IReadOnlyDictionary<(int Row, int Col), CellBounds> bounds)
    {
        var topLeft = bounds[(group.Row, group.Col)];
        var bottomRight = bounds[(group.Row + group.RowSpan - 1, group.Col + group.ColSpan - 1)];
        return new CellBounds(
            topLeft.X,
            topLeft.Y,
            bottomRight.X + bottomRight.Width - topLeft.X,
            bottomRight.Y + bottomRight.Height - topLeft.Y);
    }

    /// <summary>All bindings, including ones hidden by the current preset — used for persistence. Only ever keyed by a group's anchor, never by an absorbed member.</summary>
    public IReadOnlyDictionary<(int Row, int Col), CellBinding> AllBindings => _bindings;

    public ProfileData ToSnapshot()
    {
        var data = new ProfileData { Preset = CurrentPreset };
        foreach (var ((row, col), binding) in _bindings)
        {
            data.Bindings.Add(new CellBindingEntry { Row = row, Col = col, Type = binding.Type, Value = binding.Value });
        }
        foreach (var group in _groups)
        {
            data.Groups.Add(new CellGroupEntry { Row = group.Row, Col = group.Col, RowSpan = group.RowSpan, ColSpan = group.ColSpan });
        }
        return data;
    }

    public static LayoutManager FromSnapshot(ProfileData data)
    {
        var manager = new LayoutManager();
        manager.SetPreset(data.Preset);
        foreach (var entry in data.Groups)
        {
            manager._groups.Add(new CellGroup(entry.Row, entry.Col, entry.RowSpan, entry.ColSpan));
        }
        foreach (var entry in data.Bindings)
        {
            manager.AssignBinding(entry.Row, entry.Col, new CellBinding(entry.Type, entry.Value));
        }
        return manager;
    }
}
