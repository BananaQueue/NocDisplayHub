using System.Threading;

namespace NocDisplayHub.Core.Logging;

/// <summary>
/// Plain text/log file on disk for layout switches, crashes/relaunches, and
/// went-dark events — no in-app log viewer for v1 (see CLAUDE.md). Every entry
/// includes a timestamp and which cell it applies to.
/// </summary>
public static class ActivityLog
{
    private static readonly object WriteLock = new();
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(40);

    public static void Write(string path, string cellLabel, string message) =>
        WriteLine(path, $"{cellLabel} | {message}");

    public static void Write(string path, string message) =>
        WriteLine(path, message);

    private static void WriteLine(string path, string body)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {body}{Environment.NewLine}";
        lock (WriteLock)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // The lock above only protects threads within this one process — nothing
            // stops a second NocDisplayHub instance (confirmed live: launching the exe
            // directly instead of through the editor's "Launch Wall" spins up a second,
            // independent process) from holding the file open at the same instant. This
            // method is called from inside cell error-handling paths, so letting that
            // collision throw here would abort the very "give up gracefully" logic it's
            // meant to support, silently — the cell would never even show its red error
            // state. Retry briefly rather than let a momentary cross-process lock
            // collision take down error handling that must not fail.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.AppendAllText(path, line);
                    return;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }
    }
}
