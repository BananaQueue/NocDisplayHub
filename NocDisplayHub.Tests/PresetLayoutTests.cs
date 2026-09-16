using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Tests;

public class PresetLayoutTests
{
    [Theory]
    [InlineData(Preset.OneByOne, 1, 1)]
    [InlineData(Preset.OneByTwo, 1, 2)]
    [InlineData(Preset.TwoByOne, 2, 1)]
    [InlineData(Preset.TwoByTwo, 2, 2)]
    [InlineData(Preset.TwoByThree, 2, 3)]
    public void GetDimensions_MatchesHubSupportedPresets(Preset preset, int expectedRows, int expectedCols)
    {
        var (rows, cols) = PresetLayout.GetDimensions(preset);

        Assert.Equal(expectedRows, rows);
        Assert.Equal(expectedCols, cols);
    }

    [Fact]
    public void GetVisibleSlots_TwoByTwo_IsTopLeftSubsetOfTwoByThree()
    {
        var slots = PresetLayout.GetVisibleSlots(Preset.TwoByTwo);

        Assert.Equal(4, slots.Count);
        Assert.Contains((0, 0), slots);
        Assert.Contains((0, 1), slots);
        Assert.Contains((1, 0), slots);
        Assert.Contains((1, 1), slots);
        Assert.DoesNotContain((0, 2), slots);
    }

    [Fact]
    public void GetBounds_TwoByThree_TilesTheFullWindowWithNoGapsOrOverlap()
    {
        var bounds = PresetLayout.GetBounds(Preset.TwoByThree, 1920, 1080);

        Assert.Equal(6, bounds.Count);
        foreach (var (row, col) in PresetLayout.GetVisibleSlots(Preset.TwoByThree))
        {
            var cell = bounds[(row, col)];
            Assert.Equal(col * 640.0, cell.X);
            Assert.Equal(row * 540.0, cell.Y);
            Assert.Equal(640.0, cell.Width);
            Assert.Equal(540.0, cell.Height);
        }
    }

    [Fact]
    public void GetBounds_OneByOne_FillsEntireWindow()
    {
        var bounds = PresetLayout.GetBounds(Preset.OneByOne, 1920, 1080);

        var cell = Assert.Single(bounds).Value;
        Assert.Equal(new CellBounds(0, 0, 1920, 1080), cell);
    }
}
