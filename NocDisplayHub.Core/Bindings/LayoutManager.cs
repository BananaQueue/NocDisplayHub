using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// Owns the current preset and every cell binding, including bindings for
/// slots not currently visible. Switching presets never discards a binding —
/// it only changes which (row, col) slots are visible.
/// </summary>
public sealed class LayoutManager
{
    private readonly Dictionary<(int Row, int Col), CellBinding> _bindings = new();

    public Preset CurrentPreset { get; private set; } = Preset.TwoByThree;

    public void SetPreset(Preset preset) => CurrentPreset = preset;

    public void AssignBinding(int row, int col, CellBinding binding) => _bindings[(row, col)] = binding;

    public void ClearBinding(int row, int col) => _bindings.Remove((row, col));

    public CellBinding? GetBinding(int row, int col) => _bindings.GetValueOrDefault((row, col));

    public IReadOnlyList<Cell> GetVisibleCells(double windowWidth, double windowHeight)
    {
        var bounds = PresetLayout.GetBounds(CurrentPreset, windowWidth, windowHeight);
        var cells = new List<Cell>();

        foreach (var (row, col) in PresetLayout.GetVisibleSlots(CurrentPreset))
        {
            var binding = GetBinding(row, col);
            cells.Add(new Cell
            {
                Row = row,
                Col = col,
                Bounds = bounds[(row, col)],
                Binding = binding,
                Status = binding is null ? CellStatus.Unbound : CellStatus.Healthy,
            });
        }
        return cells;
    }

    /// <summary>All bindings, including ones hidden by the current preset — used for persistence.</summary>
    public IReadOnlyDictionary<(int Row, int Col), CellBinding> AllBindings => _bindings;

    public ProfileData ToSnapshot()
    {
        var data = new ProfileData { Preset = CurrentPreset };
        foreach (var ((row, col), binding) in _bindings)
        {
            data.Bindings.Add(new CellBindingEntry { Row = row, Col = col, Type = binding.Type, Value = binding.Value });
        }
        return data;
    }

    public static LayoutManager FromSnapshot(ProfileData data)
    {
        var manager = new LayoutManager();
        manager.SetPreset(data.Preset);
        foreach (var entry in data.Bindings)
        {
            manager.AssignBinding(entry.Row, entry.Col, new CellBinding(entry.Type, entry.Value));
        }
        return manager;
    }
}
