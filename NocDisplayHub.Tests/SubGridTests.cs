using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Tests;

public class SubGridTests
{
    [Fact]
    public void SetSubGrid_ReplacesAnyExistingSingleBinding()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://a.example"));

        manager.SetSubGrid(0, 0, Preset.OneByTwo);

        Assert.Null(manager.GetBinding(0, 0));
        Assert.NotNull(manager.GetSubGrid(0, 0));
    }

    [Fact]
    public void AssignBinding_ReplacesAnyExistingSubGrid()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        manager.SetSubGrid(0, 0, Preset.OneByTwo);

        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://a.example"));

        Assert.Null(manager.GetSubGrid(0, 0));
        Assert.Equal("https://a.example", manager.GetBinding(0, 0)!.Value);
    }

    [Fact]
    public void GetVisibleCells_SplitCell_YieldsSubCellsWithAbsoluteBoundsAndLabels()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        var subGrid = manager.SetSubGrid(0, 0, Preset.OneByTwo);
        subGrid.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://left.example"));
        subGrid.AssignBinding(0, 1, new CellBinding(BindingType.Browser, "https://right.example"));

        var cells = manager.GetVisibleCells(1000, 500);

        Assert.Equal(2, cells.Count);
        var left = cells.Single(c => c.SubCol == 0);
        var right = cells.Single(c => c.SubCol == 1);

        Assert.Equal(new CellBounds(0, 0, 500, 500), left.Bounds);
        Assert.Equal(new CellBounds(500, 0, 500, 500), right.Bounds);
        Assert.Equal("https://left.example", left.Binding!.Value);
        Assert.Equal("https://right.example", right.Binding!.Value);
        Assert.Equal("Cell(0,0)/Sub(0,1)", right.Label);
    }

    [Fact]
    public void GetVisibleCells_UnsplitCell_HasNullSubCoordinatesAndPlainLabel()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://a.example"));

        var cell = Assert.Single(manager.GetVisibleCells(1000, 500));

        Assert.Null(cell.SubRow);
        Assert.Null(cell.SubCol);
        Assert.Equal("Cell(0,0)", cell.Label);
    }

    [Fact]
    public void ToSnapshot_ThenFromSnapshot_RoundTripsSubGrids()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);
        var subGrid = manager.SetSubGrid(0, 0, Preset.TwoByTwo);
        subGrid.AssignBinding(1, 1, new CellBinding(BindingType.NativeApp, @"C:\apps\tool.exe"));

        var restored = LayoutManager.FromSnapshot(manager.ToSnapshot());

        var restoredSubGrid = restored.GetSubGrid(0, 0);
        Assert.NotNull(restoredSubGrid);
        Assert.Equal(Preset.TwoByTwo, restoredSubGrid!.Preset);
        Assert.Equal(new CellBinding(BindingType.NativeApp, @"C:\apps\tool.exe"), restoredSubGrid.GetBinding(1, 1));
    }

    [Fact]
    public void SwitchingToSmallerPreset_PreservesSubGridsOnHiddenCells()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);
        var subGrid = manager.SetSubGrid(0, 2, Preset.OneByTwo);
        subGrid.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://hidden.example"));

        manager.SetPreset(Preset.OneByOne);
        Assert.Single(manager.GetVisibleCells(1920, 1080));

        manager.SetPreset(Preset.TwoByThree);
        var restored = manager.GetVisibleCells(1920, 1080).Single(c => c.Row == 0 && c.Col == 2 && c.SubCol == 0);
        Assert.Equal("https://hidden.example", restored.Binding!.Value);
    }
}
