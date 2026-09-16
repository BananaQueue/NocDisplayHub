using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Core.Config;

/// <summary>JSON-serializable snapshot of a LayoutManager's state.</summary>
public sealed class ProfileData
{
    public Preset Preset { get; set; }
    public List<CellBindingEntry> Bindings { get; set; } = [];
    public List<SubGridEntry> SubGrids { get; set; } = [];
}

public sealed class CellBindingEntry
{
    public int Row { get; set; }
    public int Col { get; set; }
    public BindingType Type { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>A top-level cell split into smaller widgets. Bindings here are keyed by sub-row/sub-col, not the top-level grid.</summary>
public sealed class SubGridEntry
{
    public int Row { get; set; }
    public int Col { get; set; }
    public Preset SubPreset { get; set; }
    public List<CellBindingEntry> Bindings { get; set; } = [];
}
