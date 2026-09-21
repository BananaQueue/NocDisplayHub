using System.Linq;
using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Core.Config;

/// <summary>
/// Manages multiple named profiles and which one is currently active. A profile is just an
/// ordinary <see cref="ProfileStore"/>-persisted JSON file living under a dedicated profiles
/// folder — this class adds naming, listing, and "which one loads next" on top of that, so
/// both the editor and the wall always agree on the same active profile without either of
/// them needing to track it separately.
/// </summary>
public static class ProfileManager
{
    public const string DefaultProfileName = "Default";

    /// <summary>
    /// Overrides the base directory normally rooted at LocalApplicationData — test-only, so
    /// tests exercise real file I/O without ever touching a real user's actual saved profiles.
    /// Left null in production; <see cref="InternalsVisibleTo"/> in the csproj is what lets
    /// NocDisplayHub.Tests reach this internal setter at all.
    /// </summary>
    internal static string? RootDirectoryOverrideForTests { get; set; }

    /// <summary>Same idea as <see cref="RootDirectoryOverrideForTests"/>, for the legacy migration source path.</summary>
    internal static string? LegacyProfilePathOverrideForTests { get; set; }

    private static string LegacyProfilePath => LegacyProfilePathOverrideForTests ?? AppPaths.LegacyProfilePath;

    private static string RootDirectory => RootDirectoryOverrideForTests ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NocDisplayHub");

    public static string ProfilesDirectory => Path.Combine(RootDirectory, "Profiles");

    private static string ActiveProfileMarkerPath => Path.Combine(RootDirectory, "active-profile.txt");

    public static string PathFor(string profileName) =>
        Path.Combine(ProfilesDirectory, $"{SanitizeFileName(profileName)}.json");

    /// <summary>
    /// All saved profile names, alphabetically (case-insensitive). Migrates the old
    /// single-file <see cref="AppPaths.LegacyProfilePath"/> into a "Default" named profile the
    /// first time this runs after upgrading, so existing users don't lose their current layout.
    /// </summary>
    public static IReadOnlyList<string> ListProfiles()
    {
        MigrateLegacyProfileIfNeeded();
        if (!Directory.Exists(ProfilesDirectory)) return [];
        return Directory.GetFiles(ProfilesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The profile that should load next — whatever was last explicitly activated, falling
    /// back to the first alphabetically if that one's gone missing, or "Default" if there
    /// are no profiles at all yet (a brand-new install).
    /// </summary>
    public static string GetActiveProfileName()
    {
        MigrateLegacyProfileIfNeeded();
        var name = File.Exists(ActiveProfileMarkerPath) ? File.ReadAllText(ActiveProfileMarkerPath).Trim() : "";
        if (!string.IsNullOrEmpty(name) && File.Exists(PathFor(name)))
        {
            return name;
        }
        var all = ListProfiles();
        return all.Count > 0 ? all[0] : DefaultProfileName;
    }

    public static void SetActiveProfileName(string profileName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ActiveProfileMarkerPath)!);
        File.WriteAllText(ActiveProfileMarkerPath, profileName);
    }

    public static LayoutManager LoadActive() => ProfileStore.Load(PathFor(GetActiveProfileName()));

    public static void SaveActive(LayoutManager manager) => ProfileStore.Save(PathFor(GetActiveProfileName()), manager);

    /// <summary>Saves under a (possibly new) name and makes it the active profile.</summary>
    public static void SaveAs(string profileName, LayoutManager manager)
    {
        ProfileStore.Save(PathFor(profileName), manager);
        SetActiveProfileName(profileName);
    }

    /// <summary>
    /// Deletes a saved profile. Never deletes the last remaining one — the editor always
    /// needs at least one profile to have loaded something from, and an empty profiles folder
    /// would just re-trigger the legacy-migration path in a confusing way.
    /// </summary>
    public static bool DeleteProfile(string profileName)
    {
        if (ListProfiles().Count <= 1) return false;
        var path = PathFor(profileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static void MigrateLegacyProfileIfNeeded()
    {
        if (Directory.Exists(ProfilesDirectory) && Directory.GetFiles(ProfilesDirectory, "*.json").Length > 0) return;
        if (!File.Exists(LegacyProfilePath)) return;

        Directory.CreateDirectory(ProfilesDirectory);
        File.Copy(LegacyProfilePath, PathFor(DefaultProfileName), overwrite: false);
        SetActiveProfileName(DefaultProfileName);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
