using System.Diagnostics;
using System.Runtime.InteropServices;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Starts the process at <paramref name="exePath"/>, waits for its main
    /// window to appear, strips its title bar/border, and reparents it under
    /// <paramref name="parentHwnd"/> at <paramref name="bounds"/>.
    /// </summary>
    public static async Task<Process> AttachAsync(string exePath, IntPtr parentHwnd, CellBounds bounds, TimeSpan? timeout = null)
    {
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
        return process;
    }

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
