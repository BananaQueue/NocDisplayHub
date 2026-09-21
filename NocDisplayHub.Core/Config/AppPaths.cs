namespace NocDisplayHub.Core.Config;

public static class AppPaths
{
    /// <summary>
    /// The single-profile path used before named-profile support (<see cref="ProfileManager"/>)
    /// existed. Kept only so <see cref="ProfileManager"/> can migrate an existing install's
    /// layout into a "Default" named profile on first run — nothing writes here anymore.
    /// </summary>
    public static string LegacyProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NocDisplayHub",
        "profile.json");

    public static string ActivityLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NocDisplayHub",
        "activity.log");
}
