namespace NocDisplayHub.Core.Logging;

/// <summary>
/// Plain text/log file on disk for layout switches, crashes/relaunches, and
/// went-dark events — no in-app log viewer for v1 (see CLAUDE.md). Every entry
/// includes a timestamp and which cell it applies to.
/// </summary>
public static class ActivityLog
{
    private static readonly object WriteLock = new();

    public static void Write(string path, string cellLabel, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {cellLabel} | {message}{Environment.NewLine}";
        lock (WriteLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path, line);
        }
    }

    public static void Write(string path, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}";
        lock (WriteLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(path, line);
        }
    }
}
