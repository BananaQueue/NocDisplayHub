using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Compositor;

/// <summary>
/// Thrown when the launched process exits on its own right after starting,
/// without ever producing a window — seen with singleton/shell-hosted apps
/// (e.g. `explorer.exe`, which hands the request to the already-running
/// shell and then exits). Unlike a timeout, this means a real side effect
/// likely already happened (a window opened somewhere we don't control), so
/// the caller should not blindly retry — each retry repeats the same
/// uncontrolled side effect instead of ever succeeding.
/// </summary>
public sealed class ProcessExitedWithoutWindowException(string message) : Exception(message);

/// <summary>
/// Result of attaching a cell to a native app. <see cref="Process"/> is set
/// when we launched and own the process's lifecycle (the normal case — the
/// watchdog checks <c>Process.HasExited</c> and it's safe to kill on cleanup).
/// It's null for singleton-shell-hosted windows (e.g. Explorer), where the
/// window belongs to a long-lived shell process we must never kill or close
/// wholesale — <see cref="WindowHandle"/> is what the caller should track
/// and close/watch instead.
/// </summary>
public sealed class AttachResult
{
    public required IntPtr WindowHandle { get; init; }
    public Process? Process { get; init; }
}

/// <summary>
/// Launches a native executable and reparents its top-level window into a
/// compositor cell via Win32 SetParent. This keeps the window's own message
/// loop and input handling intact, so it stays clickable/interactive without
/// any extra input-injection work — the trade-off documented in the spec for
/// choosing reparenting over capture-based rendering.
/// </summary>
public static class NativeAppHost
{
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_POPUP = unchecked((long)0x80000000);
    private const long WS_CHILD = 0x40000000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint WM_CLOSE = 0x0010;

    /// <summary>Window class of a File Explorer folder window — stable since Windows Vista, still current on Windows 11.</summary>
    private const string ExplorerWindowClass = "CabinetWClass";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Starts the app at <paramref name="exePath"/> and reparents its window into
    /// a compositor cell. Handles two shapes of app: a normal app whose own
    /// launched process owns its window, and a singleton-shell-hosted app (only
    /// Explorer for now) whose launched process exits immediately after asking
    /// the already-running shell to open a window we don't own the process for.
    /// </summary>
    public static async Task<AttachResult> AttachAsync(string exePath, IntPtr parentHwnd, CellBounds bounds, TimeSpan? timeout = null)
    {
        if (IsShellHostedTarget(exePath))
        {
            return await AttachShellWindowAsync(exePath, parentHwnd, bounds, timeout);
        }

        var process = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true })
            ?? throw new InvalidOperationException($"Failed to start process: {exePath}");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (process.MainWindowHandle == IntPtr.Zero)
        {
            if (process.HasExited)
            {
                throw new ProcessExitedWithoutWindowException($"Process exited before a main window appeared: {exePath}");
            }
            if (DateTime.UtcNow > deadline)
            {
                // The process is still alive but never produced a window we can find — seen with
                // MSIX-packaged apps (e.g. Windows 11's Store Notepad/Paint), where the launched
                // process is a redirection stub that hands off to a different process entirely.
                // Kill it rather than leaving it running: every watchdog retry would otherwise
                // orphan another one of these instead of ever cleaning up.
                TryKill(process);
                throw new TimeoutException($"Timed out waiting for a main window: {exePath}");
            }
            await Task.Delay(100);
            process.Refresh();
        }

        Reparent(process.MainWindowHandle, parentHwnd, bounds);
        return new AttachResult { WindowHandle = process.MainWindowHandle, Process = process };
    }

    /// <summary>
    /// True for explorer.exe itself, or for a bare folder path (e.g. a shared logs
    /// or drop folder bound directly, with no .exe at all) — ShellExecute opens a
    /// directory path by handing it to the already-running Explorer shell exactly
    /// the same way it does for explorer.exe, so both need the same window-tracking
    /// path rather than the normal "wait for the launched process's own window" one.
    /// </summary>
    private static bool IsShellHostedTarget(string exePath) =>
        string.Equals(Path.GetFileName(exePath), "explorer.exe", StringComparison.OrdinalIgnoreCase)
        || Directory.Exists(exePath);

    /// <summary>
    /// explorer.exe (or a bare folder path) hands the request to the already-running
    /// shell (which opens a real Explorer folder window) and exits immediately — our
    /// launched process never owns a window. Instead of chasing that process, we
    /// watch for the new top-level Explorer window that appears and reparent that
    /// directly. The window belongs to the persistent shell process, which we must
    /// never kill — AttachResult.Process is left null so callers know to track/close
    /// the window handle itself, not a process.
    /// </summary>
    private static async Task<AttachResult> AttachShellWindowAsync(string exePath, IntPtr parentHwnd, CellBounds bounds, TimeSpan? timeout)
    {
        var before = new HashSet<IntPtr>(FindTopLevelWindowsByClass(ExplorerWindowClass));

        using (Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true })) { }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            var newWindow = FindTopLevelWindowsByClass(ExplorerWindowClass).FirstOrDefault(h => !before.Contains(h));
            if (newWindow != IntPtr.Zero)
            {
                Reparent(newWindow, parentHwnd, bounds);
                return new AttachResult { WindowHandle = newWindow, Process = null };
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for a new Explorer window to appear: {exePath}");
    }

    private static List<IntPtr> FindTopLevelWindowsByClass(string className)
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            if (sb.ToString() == className) result.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>True if the given window still exists — used for cells tracked by window handle rather than owned process.</summary>
    public static bool IsWindowAlive(IntPtr hWnd) => IsWindow(hWnd);

    /// <summary>Asks a specific window to close itself, without touching whatever process owns it — safe for shell-owned windows.</summary>
    public static void CloseWindow(IntPtr hWnd) => PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    private static void Reparent(IntPtr childHwnd, IntPtr parentHwnd, CellBounds bounds)
    {
        // SetParent alone does not turn a top-level window into a real child window —
        // per Microsoft's own docs, it doesn't touch WS_CHILD/WS_POPUP. Without this,
        // the window keeps behaving like an independent top-level window (can render
        // over the whole display instead of being clipped to its cell) even though
        // SetParent "succeeded". WS_POPUP and WS_CHILD are mutually exclusive.
        var style = GetWindowLongPtr(childHwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_POPUP);
        style |= WS_CHILD;
        SetWindowLongPtr(childHwnd, GWL_STYLE, (IntPtr)style);

        SetParent(childHwnd, parentHwnd);
        Reposition(childHwnd, bounds);
    }

    /// <summary>Repositions an already-reparented window, e.g. after a preset switch changes its cell's bounds.</summary>
    public static void Reposition(IntPtr childHwnd, CellBounds bounds)
    {
        SetWindowPos(childHwnd, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only — it may have exited in the meantime.
        }
    }
}
