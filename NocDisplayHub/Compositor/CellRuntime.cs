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

    /// <summary>When this cell's browser content was last (re)loaded — drives scheduled refresh.</summary>
    public DateTime LastLoadedUtc { get; set; } = DateTime.UtcNow;
}
