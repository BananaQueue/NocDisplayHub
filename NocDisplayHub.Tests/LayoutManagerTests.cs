using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Tests;

public class LayoutManagerTests
{
    [Fact]
    public void GetVisibleCells_UnboundCell_HasUnboundStatus()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);

        var cells = manager.GetVisibleCells(1920, 1080);

        var cell = Assert.Single(cells);
        Assert.Null(cell.Binding);
        Assert.Equal(CellStatus.Unbound, cell.Status);
    }

    [Fact]
    public void GetVisibleCells_BoundCell_HasHealthyStatusAndBinding()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://example.com"));

        var cell = Assert.Single(manager.GetVisibleCells(1920, 1080));

        Assert.Equal(CellStatus.Healthy, cell.Status);
        Assert.Equal("https://example.com", cell.Binding!.Value);
    }

    [Fact]
    public void SwitchingToSmallerPreset_HidesCellsButKeepsTheirBindings()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);
        manager.AssignBinding(0, 2, new CellBinding(BindingType.Browser, "https://hidden-cell.example"));

        manager.SetPreset(Preset.OneByOne);
        Assert.Single(manager.GetVisibleCells(1920, 1080));

        manager.SetPreset(Preset.TwoByThree);
        var restored = manager.GetVisibleCells(1920, 1080).Single(c => c.Row == 0 && c.Col == 2);

        Assert.Equal("https://hidden-cell.example", restored.Binding!.Value);
    }

    [Fact]
    public void ToSnapshot_ThenFromSnapshot_RoundTripsPresetAndAllBindings()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://a.example"));
        manager.AssignBinding(1, 2, new CellBinding(BindingType.NativeApp, @"C:\apps\tool.exe"));

        var restored = LayoutManager.FromSnapshot(manager.ToSnapshot());

        Assert.Equal(Preset.TwoByTwo, restored.CurrentPreset);
        Assert.Equal(new CellBinding(BindingType.Browser, "https://a.example"), restored.GetBinding(0, 0));
        Assert.Equal(new CellBinding(BindingType.NativeApp, @"C:\apps\tool.exe"), restored.GetBinding(1, 2));
    }
}
