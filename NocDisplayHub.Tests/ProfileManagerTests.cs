using NocDisplayHub.Core.Bindings;
using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Tests;

/// <summary>
/// Points ProfileManager's internal test-only overrides at an isolated temp directory for the
/// lifetime of each test, so these never read or write a real user's actual saved profiles —
/// see ProfileManager.RootDirectoryOverrideForTests.
/// </summary>
public class ProfileManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"noc-profilemanager-test-{Guid.NewGuid()}");
    private readonly string _legacyProfilePath;

    public ProfileManagerTests()
    {
        ProfileManager.RootDirectoryOverrideForTests = _root;
        _legacyProfilePath = Path.Combine(_root, "legacy-profile.json");
        ProfileManager.LegacyProfilePathOverrideForTests = _legacyProfilePath;
    }

    [Fact]
    public void ListProfiles_NoProfilesNoLegacyFile_ReturnsEmpty()
    {
        Assert.Empty(ProfileManager.ListProfiles());
    }

    [Fact]
    public void SaveAs_ThenListProfiles_IncludesTheNewProfile()
    {
        ProfileManager.SaveAs("Incident Response", new LayoutManager());

        Assert.Equal(["Incident Response"], ProfileManager.ListProfiles());
    }

    [Fact]
    public void SaveAs_MakesItTheActiveProfile()
    {
        ProfileManager.SaveAs("Night Shift", new LayoutManager());

        Assert.Equal("Night Shift", ProfileManager.GetActiveProfileName());
    }

    [Fact]
    public void SaveActive_ThenLoadActive_RoundTripsTheLayout()
    {
        var manager = new LayoutManager();
        manager.SetPreset(Preset.TwoByTwo);
        manager.AssignBinding(0, 1, new CellBinding(BindingType.Browser, "https://dashboard.example"));
        ProfileManager.SaveAs("Day Shift", manager);

        var loaded = ProfileManager.LoadActive();

        Assert.Equal(Preset.TwoByTwo, loaded.CurrentPreset);
        Assert.Equal(new CellBinding(BindingType.Browser, "https://dashboard.example"), loaded.GetBinding(0, 1));
    }

    [Fact]
    public void SwitchingActiveProfile_PreservesTheOtherProfileOnDisk()
    {
        var dayShift = new LayoutManager();
        dayShift.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://day.example"));
        ProfileManager.SaveAs("Day Shift", dayShift);

        var nightShift = new LayoutManager();
        nightShift.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://night.example"));
        ProfileManager.SaveAs("Night Shift", nightShift);

        ProfileManager.SetActiveProfileName("Day Shift");
        var reloadedDay = ProfileManager.LoadActive();

        Assert.Equal("https://day.example", reloadedDay.GetBinding(0, 0)!.Value);
    }

    [Fact]
    public void DeleteProfile_RemovesIt_ButNotTheLastRemainingOne()
    {
        ProfileManager.SaveAs("Day Shift", new LayoutManager());
        ProfileManager.SaveAs("Night Shift", new LayoutManager());

        Assert.True(ProfileManager.DeleteProfile("Night Shift"));
        Assert.Equal(["Day Shift"], ProfileManager.ListProfiles());

        // Only one left — refuse to delete it, since the app always needs at least one profile
        // to have loaded something from.
        Assert.False(ProfileManager.DeleteProfile("Day Shift"));
        Assert.Equal(["Day Shift"], ProfileManager.ListProfiles());
    }

    [Fact]
    public void GetActiveProfileName_MarkerPointsAtDeletedProfile_FallsBackToFirstRemaining()
    {
        ProfileManager.SaveAs("Alpha", new LayoutManager());
        ProfileManager.SaveAs("Beta", new LayoutManager());
        ProfileManager.SetActiveProfileName("Beta");
        ProfileManager.DeleteProfile("Beta");

        Assert.Equal("Alpha", ProfileManager.GetActiveProfileName());
    }

    [Fact]
    public void ListProfiles_MigratesLegacySingleProfileFile_IntoADefaultNamedProfile()
    {
        var legacyManager = new LayoutManager();
        legacyManager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://legacy.example"));
        ProfileStore.Save(_legacyProfilePath, legacyManager);

        var profiles = ProfileManager.ListProfiles();

        Assert.Equal([ProfileManager.DefaultProfileName], profiles);
        Assert.Equal(ProfileManager.DefaultProfileName, ProfileManager.GetActiveProfileName());
        Assert.Equal("https://legacy.example", ProfileManager.LoadActive().GetBinding(0, 0)!.Value);
    }

    [Fact]
    public void ListProfiles_NamedProfilesAlreadyExist_DoesNotMigrateLegacyFile()
    {
        ProfileManager.SaveAs("Existing", new LayoutManager());
        var legacyManager = new LayoutManager();
        legacyManager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, "https://legacy.example"));
        ProfileStore.Save(_legacyProfilePath, legacyManager);

        var profiles = ProfileManager.ListProfiles();

        Assert.Equal(["Existing"], profiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        ProfileManager.RootDirectoryOverrideForTests = null;
        ProfileManager.LegacyProfilePathOverrideForTests = null;
    }
}
