using System.Diagnostics;
using System.IO;
using System.Linq;
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement lpwndpl);

    private const int SwMinimize = 6;
    private const int SwShowMinimized = 2;

    // The primary monitor is always anchored at (0,0) by Windows convention, and these
    // give its resolution directly — simpler than enumerating monitors for this.
    private const int SmCxscreen = 0;
    private const int SmCyscreen = 1;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

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

        // Snapshotted up front, before the process even starts, the same way
        // AttachShellWindowAsync snapshots Explorer windows — so the fallback below
        // can recognize a window as "new" even if one happened to already be open.
        var imageName = Path.GetFileNameWithoutExtension(exePath);
        var priorWindowsByName = new HashSet<IntPtr>(FindTopLevelWindowsByProcessName(imageName));

        var process = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true })
            ?? throw new InvalidOperationException($"Failed to start process: {exePath}");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (process.MainWindowHandle == IntPtr.Zero)
        {
            if (process.HasExited)
            {
                // Confirmed on Viber: not just explorer.exe hands off and exits. Modern
                // chat/Electron-style apps often launch via a bootstrapper that spawns the
                // real worker process (or hands off to an already-running one) and exits
                // immediately — even on a genuinely cold start, so this isn't just the
                // "already running" case. Rather than give up straight away, fall back to
                // watching for a new top-level window owned by ANY process sharing this
                // exe's image name, the same idea as AttachShellWindowAsync's Explorer
                // window-class watch, generalized since most apps don't have Explorer's
                // stable, publicly-documented window class to key off instead.
                var fallback = await WaitForWindowByProcessNameAsync(imageName, priorWindowsByName, parentHwnd, bounds, deadline);
                if (fallback is not null) return fallback;

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

        if (!Reparent(process.MainWindowHandle, parentHwnd, bounds))
        {
            throw new InvalidOperationException($"Failed to reparent window: {exePath}");
        }
        return new AttachResult { WindowHandle = process.MainWindowHandle, Process = process };
    }

    /// <summary>
    /// Polls for a new top-level window owned by any process named <paramref name="imageName"/>
    /// (no extension) that wasn't in <paramref name="before"/>, until <paramref name="deadline"/>.
    /// The owning process might not be the one we launched (e.g. a hand-off to an already-running
    /// instance) or might be a brand new one spawned by a launcher/bootstrapper that already exited
    /// — either way we don't own its lifecycle, so like Explorer's window, it's tracked by handle
    /// only (<see cref="AttachResult.Process"/> left null) and must never be killed outright.
    /// </summary>
    private static async Task<AttachResult?> WaitForWindowByProcessNameAsync(
        string imageName, HashSet<IntPtr> before, IntPtr parentHwnd, CellBounds bounds, DateTime deadline)
    {
        while (DateTime.UtcNow < deadline)
        {
            var newWindow = FindTopLevelWindowsByProcessName(imageName).FirstOrDefault(h => !before.Contains(h));
            if (newWindow != IntPtr.Zero && Reparent(newWindow, parentHwnd, bounds))
            {
                return new AttachResult { WindowHandle = newWindow, Process = null };
            }
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>
    /// Visible top-level windows with a non-empty title, owned by any currently-running process
    /// whose name matches <paramref name="processName"/> — a non-empty title filters out
    /// invisible utility/notification-area helper windows a launcher process might also own.
    /// </summary>
    private static List<IntPtr> FindTopLevelWindowsByProcessName(string processName)
    {
        var result = new List<IntPtr>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            var titleLength = GetWindowText(hWnd, new StringBuilder(256), 256);
            if (titleLength == 0) return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            try
            {
                using var owner = Process.GetProcessById((int)pid);
                if (string.Equals(owner.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(hWnd);
                }
            }
            catch (ArgumentException)
            {
                // The owning process already exited between enumeration and lookup — ignore.
            }
            return true;
        }, IntPtr.Zero);
        return result;
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
            if (newWindow != IntPtr.Zero && Reparent(newWindow, parentHwnd, bounds))
            {
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

    /// <summary>
    /// Reparents a window into a compositor cell. Returns false if the OS-level
    /// reparent itself failed — confirmed live, this was previously never checked at
    /// all: <c>SetParent</c>'s return value was discarded outright, so a cell could
    /// report "Healthy" while actually showing nothing. <c>SetParent</c> returning
    /// NULL is ambiguous by itself (it also means "had no previous parent," the
    /// normal case for a plain top-level window), so failure is only real when
    /// <c>GetLastWin32Error</c> is nonzero too.
    /// </summary>
    public static bool Reparent(IntPtr childHwnd, IntPtr parentHwnd, CellBounds bounds)
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

        var previousParent = SetParent(childHwnd, parentHwnd);
        if (previousParent == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
        {
            return false;
        }

        Reposition(childHwnd, bounds);
        return true;
    }

    /// <summary>
    /// Repositions an already-reparented window, e.g. after a preset switch changes its
    /// cell's bounds. Resizes twice — first 1px smaller, then to the real target size —
    /// rather than once: confirmed live, a browser window dragged in via drag-and-drop
    /// capture left a black gap around its content after a single resize. Browsers (and
    /// other apps using GPU-accelerated/DirectComposition-backed window surfaces) don't
    /// always recompute that surface from one SetWindowPos alone, leaving stale, undersized
    /// content. A second, genuinely different resize immediately after reliably forces a
    /// full recompute — a known, low-risk workaround for this class of reparenting quirk,
    /// imperceptible since both calls happen back-to-back with nothing rendered in between.
    /// </summary>
    public static void Reposition(IntPtr childHwnd, CellBounds bounds)
    {
        var nudgeWidth = Math.Max(1, (int)bounds.Width - 1);
        var nudgeHeight = Math.Max(1, (int)bounds.Height - 1);
        SetWindowPos(childHwnd, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, nudgeWidth, nudgeHeight, SWP_NOZORDER | SWP_FRAMECHANGED);
        SetWindowPos(childHwnd, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height, SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// The inverse of <see cref="Reparent"/> — hands a window back to the desktop as an
    /// ordinary independent top-level window again, restoring the caption/frame/system-menu
    /// style bits reparenting stripped. Used when a cell's window is displaced by a
    /// drag-and-drop capture: the window that used to be there doesn't just vanish, it
    /// becomes a normal floating window again, exactly as if the user had never assigned
    /// it to a cell. Minimized immediately after, so it doesn't stay sitting on top of the
    /// wall.
    ///
    /// Confirmed live this needed two rounds to get right. First attempt: a separate
    /// SetWindowPos to move it onto the primary monitor, immediately followed by
    /// ShowWindow(SW_MINIMIZE) — still restored back over the wall. Separate Win32 calls
    /// like that aren't atomic; nothing guarantees the position change has actually been
    /// committed to the window's tracked "restore" rect before the very next call minimizes
    /// it, especially back-to-back with no message-loop turnaround in between. SetWindowPlacement
    /// is the documented, atomic way to set both a window's show state *and* its remembered
    /// restore rect in one call — used here instead, so there's no ordering gap left to race.
    /// </summary>
    public static void Release(IntPtr childHwnd)
    {
        var style = GetWindowLongPtr(childHwnd, GWL_STYLE).ToInt64();
        style &= ~WS_CHILD;
        style |= WS_CAPTION | WS_THICKFRAME | WS_SYSMENU;
        SetWindowLongPtr(childHwnd, GWL_STYLE, (IntPtr)style);
        SetParent(childHwnd, IntPtr.Zero);

        var primaryWidth = GetSystemMetrics(SmCxscreen);
        var primaryHeight = GetSystemMetrics(SmCyscreen);
        var currentSize = TryGetWindowRect(childHwnd);
        var width = (int)Math.Min(currentSize?.Width ?? 800, Math.Max(400, primaryWidth - 200));
        var height = (int)Math.Min(currentSize?.Height ?? 600, Math.Max(300, primaryHeight - 200));

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(childHwnd, ref placement);
        placement.ShowCmd = SwShowMinimized;
        placement.NormalPosition = new Rect { Left = 100, Top = 100, Right = 100 + width, Bottom = 100 + height };
        SetWindowPlacement(childHwnd, ref placement);
    }

    /// <summary>A window's current position and size in physical screen pixels, or null if the window no longer exists.</summary>
    public static CellBounds? TryGetWindowRect(IntPtr hWnd)
    {
        if (!GetWindowRect(hWnd, out var rect)) return null;
        return new CellBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>The process ID that owns a given window — used to exclude our own windows from drag-and-drop capture.</summary>
    public static uint GetOwningProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var pid);
        return pid;
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
