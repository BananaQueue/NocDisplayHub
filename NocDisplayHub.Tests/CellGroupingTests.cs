using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Tests;

public class CellGroupingTests
{
    [Fact]
    public void TryGroupCells_SolidRectangle_MergesIntoOneCellSpanningTheirCombinedBounds()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://merged.example"));

        var ok = manager.TryGroupCells([(0, 0), (0, 1), (1, 0), (1, 1)], out var error);

        Assert.True(ok);
        Assert.Null(error);
        var cell = Assert.Single(manager.GetVisibleCells(1920, 1080));
        Assert.Equal((0, 0), (cell.Row, cell.Col));
        Assert.Equal(new CellBounds(0, 0, 1920, 1080), cell.Bounds);
        Assert.Equal("https://merged.example", cell.Binding!.Value);
    }

    [Fact]
    public void TryGroupCells_VerticalPair_MergesIntoOneTallCell()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);

        var ok = manager.TryGroupCells([(0, 2), (1, 2)], out var error);

        Assert.True(ok);
        Assert.Null(error);
        var cell = manager.GetVisibleCells(1920, 1080).Single(c => c.Row == 0 && c.Col == 2);
        Assert.Equal(1080, cell.Bounds.Height);
    }

    [Fact]
    public void TryGroupCells_NonRectangularLShape_IsRefused()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);

        var ok = manager.TryGroupCells([(0, 0), (0, 1), (1, 0)], out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Equal(6, manager.GetVisibleCells(1920, 1080).Count); // nothing merged
    }

    [Fact]
    public void TryGroupCells_SingleCell_IsRefused()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);

        var ok = manager.TryGroupCells([(0, 0)], out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryGroupCells_SlotOutsideCurrentPreset_IsRefused()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.OneByOne);

        var ok = manager.TryGroupCells([(0, 0), (1, 0)], out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryGroupCells_CellAlreadyInAnotherGroup_IsRefused()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);
        manager.TryGroupCells([(0, 0), (0, 1)], out _);

        var ok = manager.TryGroupCells([(0, 1), (1, 1)], out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryGroupCells_DiscardsNonAnchorMembersOwnBindings()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://anchor.example"));
        manager.AssignBinding(0, 1, new CellBinding(BindingType.Browser, "https://discarded.example"));

        manager.TryGroupCells([(0, 0), (0, 1), (1, 0), (1, 1)], out _);

        var cell = Assert.Single(manager.GetVisibleCells(1920, 1080));
        Assert.Equal("https://anchor.example", cell.Binding!.Value);
    }

    [Fact]
    public void Ungroup_RestoresIndividualCellsWithAnchorKeepingTheBinding()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://merged.example"));
        manager.TryGroupCells([(0, 0), (0, 1), (1, 0), (1, 1)], out _);

        manager.Ungroup(1, 1); // works from any member, not just the anchor

        var cells = manager.GetVisibleCells(1920, 1080);
        Assert.Equal(4, cells.Count);
        Assert.Equal("https://merged.example", manager.GetBinding(0, 0)!.Value);
        Assert.Null(manager.GetBinding(0, 1));
        Assert.Null(manager.GetBinding(1, 0));
        Assert.Null(manager.GetBinding(1, 1));
    }

    [Fact]
    public void AssignBinding_OnAbsorbedMemberSlot_RedirectsToTheGroupsAnchor()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.TryGroupCells([(0, 0), (0, 1), (1, 0), (1, 1)], out _);

        manager.AssignBinding(1, 1, new CellBinding(BindingType.Browser, "https://redirected.example"));

        Assert.Equal("https://redirected.example", manager.GetBinding(0, 0)!.Value);
        var cell = Assert.Single(manager.GetVisibleCells(1920, 1080));
        Assert.Equal("https://redirected.example", cell.Binding!.Value);
    }

    [Fact]
    public void SwitchingPreset_ClearsGroupsButKeepsTheAnchorsBindingAsAnOrdinaryCell()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://merged.example"));
        manager.TryGroupCells([(0, 0), (0, 1), (1, 0), (1, 1)], out _);

        manager.SetPreset(Preset.TwoByThree);

        Assert.Empty(manager.Groups);
        Assert.Equal(6, manager.GetVisibleCells(1920, 1080).Count);
        Assert.Equal("https://merged.example", manager.GetBinding(0, 0)!.Value);
    }

    [Fact]
    public void ToSnapshot_ThenFromSnapshot_RoundTripsGroups()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByThree);
        manager.AssignBinding(0, 2, new CellBinding(BindingType.Browser, "https://tall.example"));
        manager.TryGroupCells([(0, 2), (1, 2)], out _);

        var restored = LayoutManager.FromSnapshot(manager.ToSnapshot());

        var group = Assert.Single(restored.Groups);
        Assert.Equal(new CellGroup(0, 2, 2, 1), group);
        var cell = restored.GetVisibleCells(1920, 1080).Single(c => c.Row == 0 && c.Col == 2);
        Assert.Equal("https://tall.example", cell.Binding!.Value);
    }
}
