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

    private readonly record struct MonitorEntry(int Left, int Top, int Width, int Height, Rect WorkArea, bool IsPrimary);

    private static List<MonitorEntry> EnumerateMonitors()
    {
        var monitors = new List<MonitorEntry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Rect rect, IntPtr _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add(new MonitorEntry(
                    info.Monitor.Left, info.Monitor.Top,
                    info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                    info.WorkArea, (info.Flags & MonitorInfoFPrimary) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>
    /// Returns the top-left corner (in virtual-desktop pixels) of the screen
    /// that's the hub, or null if it can't tell (caller should fall back to
    /// (0,0) in that case).
    /// </summary>
    public static (int Left, int Top)? FindHubOrigin()
    {
        var matches = EnumerateMonitors()
            .Where(m => m.Width == HubSpec.InputWidth && m.Height == HubSpec.InputHeight)
            .ToList();

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

    /// <summary>
    /// Returns the work area (in virtual-desktop pixels, taskbar excluded) of a screen
    /// that ISN'T the hub — for pinning the editor to whatever's presumably the operator's
    /// actual working monitor, rather than leaving it to WPF's own WindowStartupLocation
    /// heuristics. Confirmed live: "CenterScreen" is not guaranteed to land on the
    /// OS-designated primary monitor for a PerMonitorV2-aware app (our manifest applies
    /// process-wide, not just to the compositor window) — its centering math resolves
    /// against whichever monitor Windows' own placement heuristic assigns the new window
    /// to at creation, which observably was the hub's own extended display, not the main
    /// screen, when launched from a non-interactive context.
    ///
    /// Deliberately excludes whichever single screen <see cref="FindHubOrigin"/> itself
    /// resolved the hub to, rather than independently re-filtering by resolution — caught
    /// on review before it ever shipped: an independent resolution-only filter degenerates
    /// exactly when the tie <see cref="FindHubOrigin"/>'s own doc comment already describes
    /// (the laptop panel reporting the hub's exact resolution) occurs, excluding BOTH
    /// screens as "hub-shaped" and leaving nothing, silently falling back to the very
    /// CenterScreen bug this method exists to avoid. Reusing FindHubOrigin's own
    /// already-tie-broken answer means editor positioning can never disagree with wherever
    /// the compositor will actually land. Among whatever's left, prefers the OS-designated
    /// primary monitor (nobody sets the hub-fed output as primary — same assumption
    /// FindHubOrigin's own tiebreak relies on). Returns null when there's no unambiguous
    /// non-hub screen to pick (e.g. the real kiosk, which only has the hub, or a single-
    /// monitor dev setup) — callers should fall back to their own default positioning then.
    /// </summary>
    public static (int Left, int Top, int Width, int Height)? FindEditorWorkArea()
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count <= 1) return null; // nothing to disambiguate against

        var hubOrigin = FindHubOrigin();
        var candidates = hubOrigin is { } hub
            ? monitors.Where(m => m.Left != hub.Left || m.Top != hub.Top).ToList()
            : monitors;

        var chosen = candidates.Count switch
        {
            0 => (MonitorEntry?)null,
            1 => candidates[0],
            _ => candidates.Count(m => m.IsPrimary) == 1 ? candidates.First(m => m.IsPrimary) : null,
        };

        if (chosen is not { } area) return null;
        return (area.WorkArea.Left, area.WorkArea.Top,
            area.WorkArea.Right - area.WorkArea.Left, area.WorkArea.Bottom - area.WorkArea.Top);
    }
}
