using System.Linq;
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

    private const uint MonitorInfoFPrimary = 0x1;

    /// <summary>
    /// Returns the top-left corner (in virtual-desktop pixels) of the screen
    /// that's the hub, or null if it can't tell (caller should fall back to
    /// (0,0) in that case).
    /// </summary>
    public static (int Left, int Top)? FindHubOrigin()
    {
        var matches = new List<(int Left, int Top, bool IsPrimary)>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var width = info.Monitor.Right - info.Monitor.Left;
                var height = info.Monitor.Bottom - info.Monitor.Top;
                if (width == HubSpec.InputWidth && height == HubSpec.InputHeight)
                {
                    matches.Add((info.Monitor.Left, info.Monitor.Top, (info.Flags & MonitorInfoFPrimary) != 0));
                }
            }
            return true;
        }, IntPtr.Zero);

        if (matches.Count == 1)
        {
            return (matches[0].Left, matches[0].Top);
        }

        // A dev/laptop panel can legitimately share the hub's exact resolution
        // (confirmed on hardware: a 1920x1080-at-125%-scaling laptop screen
        // reports the same physical 1920x1080 as the hub) — that alone isn't
        // enough to disambiguate. But nobody sets the hub-fed output as their
        // Windows-designated primary monitor, so when there's a tie, prefer
        // the one non-primary match over a screen that's someone's main display.
        var nonPrimary = matches.Where(m => !m.IsPrimary).ToList();
        return nonPrimary.Count == 1 ? (nonPrimary[0].Left, nonPrimary[0].Top) : null;
    }
}
