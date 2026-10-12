using System.Text.Json;
using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Core.Config;

/// <summary>Persists a single profile's layout to a JSON file on disk so bindings survive an app restart.</summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public const int MaxBackupsPerProfile = 50;
    private const string BackupTimestampFormat = "yyyyMMdd-HHmmss-fff";

    private static string BackupDirectoryFor(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", "Backups", Path.GetFileNameWithoutExtension(path));

    public static void Save(string path, LayoutManager manager)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(manager.ToSnapshot(), JsonOptions);
        BackUpPreviousVersion(path, json);
        File.WriteAllText(path, json);
    }

    // Every editor change auto-saves over the profile in place, so one accidental Unassign/Apply
    // used to be unrecoverable. Keep the version being replaced under Backups\<name>\ beside it.
    // Best-effort: a failed backup must never block the save itself.
    private static void BackUpPreviousVersion(string path, string newJson)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (File.ReadAllText(path) == newJson) return;

            var backupDir = BackupDirectoryFor(path);
            Directory.CreateDirectory(backupDir);
            File.Copy(path, Path.Combine(backupDir, $"{DateTime.Now.ToString(BackupTimestampFormat)}.json"), overwrite: true);

            foreach (var old in Directory.GetFiles(backupDir, "*.json").OrderByDescending(f => f).Skip(MaxBackupsPerProfile))
            {
                File.Delete(old);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Older versions of the profile at <paramref name="path"/> that a later save replaced, newest first.</summary>
    public static IReadOnlyList<ProfileBackup> ListBackups(string path)
    {
        var backupDir = BackupDirectoryFor(path);
        if (!Directory.Exists(backupDir)) return [];

        var result = new List<ProfileBackup>();
        foreach (var file in Directory.GetFiles(backupDir, "*.json").OrderByDescending(f => f))
        {
            if (!DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), BackupTimestampFormat,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when))
            {
                continue;
            }
            try
            {
                result.Add(new ProfileBackup(file, when, Load(file).AllBindings.Count));
            }
            catch (JsonException) { }
        }
        return result;
    }

    /// <summary>Makes the profile at <paramref name="path"/> match <paramref name="backup"/>; the version being replaced is itself backed up by Save.</summary>
    public static void RestoreBackup(string path, ProfileBackup backup) => Save(path, Load(backup.Path));

    public static LayoutManager Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LayoutManager();
        }
        var data = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(path)) ?? new ProfileData();
        return LayoutManager.FromSnapshot(data);
    }
}
