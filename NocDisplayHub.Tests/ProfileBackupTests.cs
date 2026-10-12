using NocDisplayHub.Core.Bindings;
using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Tests;

public class ProfileBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noc-profilebackup-test-{Guid.NewGuid()}");
    private string ProfilePath => Path.Combine(_dir, "Main.json");
    private string BackupDir => Path.Combine(_dir, "Backups", "Main");

    private static LayoutManager WithUrl(string url)
    {
        var manager = new LayoutManager();
        manager.AssignBinding(0, 0, new CellBinding(BindingType.Browser, url));
        return manager;
    }

    [Fact]
    public void Save_FirstTime_CreatesNoBackup()
    {
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));

        Assert.False(Directory.Exists(BackupDir));
    }

    [Fact]
    public void Save_OverwritingChangedProfile_KeepsThePreviousVersion()
    {
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));
        ProfileStore.Save(ProfilePath, new LayoutManager()); // simulates "Unassign" wiping the link

        var backups = Directory.GetFiles(BackupDir, "*.json");
        Assert.Single(backups);
        var restored = ProfileStore.Load(backups[0]);
        Assert.Equal(new CellBinding(BindingType.Browser, "https://one.example"), restored.GetBinding(0, 0));
    }

    [Fact]
    public void Save_IdenticalContent_DoesNotCreateAnotherBackup()
    {
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));
        ProfileStore.Save(ProfilePath, WithUrl("https://two.example"));
        ProfileStore.Save(ProfilePath, WithUrl("https://two.example"));

        Assert.Single(Directory.GetFiles(BackupDir, "*.json"));
    }

    [Fact]
    public void Save_ManyChanges_KeepsOnlyTheNewestBackups()
    {
        for (var i = 0; i <= ProfileStore.MaxBackupsPerProfile + 10; i++)
        {
            ProfileStore.Save(ProfilePath, WithUrl($"https://site{i}.example"));
        }

        Assert.Equal(ProfileStore.MaxBackupsPerProfile, Directory.GetFiles(BackupDir, "*.json").Length);
    }

    [Fact]
    public void ListBackups_NoBackups_ReturnsEmpty()
    {
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));

        Assert.Empty(ProfileStore.ListBackups(ProfilePath));
    }

    [Fact]
    public void ListBackups_ReturnsNewestFirstWithBindingCounts()
    {
        var two = WithUrl("https://one.example");
        two.AssignBinding(0, 1, new CellBinding(BindingType.Browser, "https://two.example"));
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));
        ProfileStore.Save(ProfilePath, two);
        ProfileStore.Save(ProfilePath, new LayoutManager());

        var backups = ProfileStore.ListBackups(ProfilePath);

        Assert.Equal([2, 1], backups.Select(b => b.BindingCount));
        Assert.True(backups[0].SavedOverAt >= backups[1].SavedOverAt);
    }

    [Fact]
    public void RestoreBackup_BringsBackTheOldLinks_AndBacksUpWhatItReplaced()
    {
        ProfileStore.Save(ProfilePath, WithUrl("https://one.example"));
        ProfileStore.Save(ProfilePath, new LayoutManager()); // the accidental wipe

        ProfileStore.RestoreBackup(ProfilePath, ProfileStore.ListBackups(ProfilePath)[0]);

        Assert.Equal(new CellBinding(BindingType.Browser, "https://one.example"), ProfileStore.Load(ProfilePath).GetBinding(0, 0));
        Assert.Equal(2, ProfileStore.ListBackups(ProfilePath).Count); // original + the wiped state we just replaced
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
