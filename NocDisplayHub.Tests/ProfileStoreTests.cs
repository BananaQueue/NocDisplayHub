using NocDisplayHub.Core.Bindings;
using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Tests;

public class ProfileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"noc-profile-test-{Guid.NewGuid()}.json");

    [Fact]
    public void Load_MissingFile_ReturnsFreshDefaultLayout()
    {
        var manager = ProfileStore.Load(_path);

        Assert.Equal(Preset.TwoByThree, manager.CurrentPreset);
        Assert.Empty(manager.AllBindings);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsToDisk()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 1, new CellBinding(BindingType.Browser, "https://dashboard.example"));

        ProfileStore.Save(_path, manager);
        var loaded = ProfileStore.Load(_path);

        Assert.Equal(Preset.TwoByTwo, loaded.CurrentPreset);
        Assert.Equal(new CellBinding(BindingType.Browser, "https://dashboard.example"), loaded.GetBinding(0, 1));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
