namespace NocDisplayHub.Core.Config;

public static class AppPaths
{
    public static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NocDisplayHub",
        "profile.json");

    public static string ActivityLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NocDisplayHub",
        "activity.log");
}
