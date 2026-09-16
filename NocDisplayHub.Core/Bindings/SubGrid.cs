namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// A top-level cell's internal split into smaller widgets. Mirrors
/// LayoutManager's shape at a smaller scale so the same preset/bounds math
/// applies recursively, one level deep (v1 doesn't nest sub-grids further).
/// </summary>
public sealed class SubGrid
{
    private readonly Dictionary<(int Row, int Col), CellBinding> _bindings = new();

    public required Preset Preset { get; init; }

    public void AssignBinding(int subRow, int subCol, CellBinding binding) => _bindings[(subRow, subCol)] = binding;

    public void ClearBinding(int subRow, int subCol) => _bindings.Remove((subRow, subCol));

    public CellBinding? GetBinding(int subRow, int subCol) => _bindings.GetValueOrDefault((subRow, subCol));

    public IReadOnlyDictionary<(int Row, int Col), CellBinding> AllBindings => _bindings;
}
