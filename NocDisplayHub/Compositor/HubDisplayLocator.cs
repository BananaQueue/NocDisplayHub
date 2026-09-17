using System.Runtime.InteropServices;
using NocDisplayHub.Core.Config;

namespace NocDisplayHub.Compositor;

/// <summary>
/// Finds which of Windows' GDI screens is the hub's output, so the compositor
/// can position itself there automatically instead of assuming it's always at
/// (0,0). On the real kiosk workstation there is only one screen and it IS the
/// hub (see CLAUDE.md), so this is a no-op there — but on the dev machine (and
/// any machine where a second output is briefly attached) Windows exposes more
/// than one screen, and (0,0) is whichever one happens to be primary, not
/// necessarily the hub. This is a one-time "which screen do I cover" lookup,
/// not the "treat the hub as 6 monitors" pattern CLAUDE.md warns against.
/// </summary>
public static class HubDisplayLocator
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    /// <summary>
    /// Returns the top-left corner (in virtual-desktop pixels) of the single
    /// screen whose resolution matches HubSpec, or null if zero or more than
    /// one screen matches (caller should fall back to (0,0) in that case).
    /// </summary>
    public static (int Left, int Top)? FindHubOrigin()
    {
        var matches = new List<(int Left, int Top)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;
                if (width == HubSpec.InputWidth && height == HubSpec.InputHeight)
                {
                    matches.Add((info.Monitor.Left, info.Monitor.Top));
                }
            }
            return true;
        }, IntPtr.Zero);

        return matches.Count == 1 ? matches[0] : null;
    }
}
