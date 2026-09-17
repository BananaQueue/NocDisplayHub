using System.Diagnostics;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Compositor;

/// <summary>
/// Live state for one visible cell: its model, the WPF Border rendering it,
/// and whichever content it's hosting. The watchdog mutates this in place
/// (border brush, child content, process handle) rather than re-rendering the
/// whole grid, since a full re-render would tear down healthy cells too.
/// </summary>
public sealed class CellRuntime
{
    public required Cell Cell { get; set; }
    public required Border Border { get; init; }
    public Process? Process { get; set; }
    public WebView2? WebView { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// The window currently reparented into this cell, if any — set for every
    /// successfully-attached native app cell, not just shell-hosted ones (Process
    /// may be set too, when we also own the launching process's lifecycle). The
    /// watchdog checks this directly with NativeAppHost.IsWindowAlive rather than
    /// trusting Process.HasExited alone: confirmed live with Outlook, a still-running
    /// process can silently swap to a brand new window (a splash/loading frame gets
    /// destroyed once the real one is ready), which HasExited alone would never catch.
    /// Cleanup always goes through NativeAppHost.CloseWindow (safe for a shell-owned
    /// window too), never Process.Kill.
    /// </summary>
    public IntPtr? TrackedWindowHandle { get; set; }

    /// <summary>Set once a cell exceeds the retry cap — the watchdog stops touching it until the app restarts.</summary>
    public bool GaveUp { get; set; }

    /// <summary>
    /// True while a launch attempt is awaiting (native app attach can take up to
    /// its timeout). Without this, the watchdog's 3s tick would start another
    /// overlapping attempt for the same cell before the first one finishes.
    /// </summary>
    public bool AttemptInProgress { get; set; }

    /// <summary>When this cell's browser content was last (re)loaded — drives scheduled refresh.</summary>
    public DateTime LastLoadedUtc { get; set; } = DateTime.UtcNow;
}
