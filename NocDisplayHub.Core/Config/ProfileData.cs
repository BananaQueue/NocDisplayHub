using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Core.Config;

/// <summary>JSON-serializable snapshot of a LayoutManager's state.</summary>
public sealed class ProfileData
{
    public Preset Preset { get; set; }
    public List<CellBindingEntry> Bindings { get; set; } = [];
}

public sealed class CellBindingEntry
{
    public int Row { get; set; }
    public int Col { get; set; }
    public BindingType Type { get; set; }
    public string Value { get; set; } = "";
}
