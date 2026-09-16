using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NocDisplayHub.Core.Bindings;
using NocDisplayHub.Core.Config;
using NocDisplayHub.Core.Logging;
using NocDisplayHub.Core.Monitoring;

namespace NocDisplayHub.Compositor;

/// <summary>
/// The single fullscreen window the app renders into. Windows only ever sees
/// one virtual display here — the hub is what physically slices this window's
/// output across the 6 monitors. Do not use multi-monitor APIs against this
/// window; size and position come from HubSpec, not from screen enumeration.
///
/// Owns three independent timers:
///  - watchdog (Phase 2): relaunches crashed native apps / failed WebView2 cells,
///    always with a visible "Restarting" state, never silently.
///  - scheduled refresh (Phase 3): reloads browser cells periodically so they
///    don't go stale silently.
///  - frozen-content check (Phase 3): screenshots each browser cell and flags
///    one whose content hasn't changed in a while. Shares the went-dark red
///    border per the spec's decision; the activity log is what tells them apart.
///
/// Browser cells always keep their WebView2 control as the cell's content —
/// status is communicated purely via the border color. Native app cells have
/// nothing to show while no process is attached, so a text placeholder is used.
/// A cell split into a sub-grid (Task: "splitting one monitor into a grid of
/// smaller widgets") is invisible to all of this — LayoutManager.GetVisibleCells
/// already flattens it into plain leaf Cells before this class ever sees it.
/// </summary>
public partial class CompositorWindow : Window
{
    private const int MaxConsecutiveFailures = 5;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrozenCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrozenThreshold = TimeSpan.FromMinutes(5);

    private readonly Dictionary<CellKey, CellRuntime> _runtimes = new();
    private readonly Dictionary<CellKey, FrozenContentTracker> _frozenTrackers = new();
    private readonly DispatcherTimer _watchdogTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _frozenCheckTimer;

    private readonly record struct CellKey(int Row, int Col, int? SubRow, int? SubCol)
    {
        public static CellKey Of(Cell cell) => new(cell.Row, cell.Col, cell.SubRow, cell.SubCol);
    }

    public CompositorWindow()
    {
        InitializeComponent();
        Width = HubSpec.InputWidth;
        Height = HubSpec.InputHeight;
        Loaded += OnLoaded;
        Closed += OnClosed;

        _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
        _watchdogTimer.Tick += (_, _) => RunWatchdogPass();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += (_, _) => RunScheduledRefreshPass();

        _frozenCheckTimer = new DispatcherTimer { Interval = FrozenCheckInterval };
        _frozenCheckTimer.Tick += (_, _) => _ = RunFrozenContentPassAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var manager = ProfileStore.Load(AppPaths.ProfilePath);
        BuildCells(manager);
        _watchdogTimer.Start();
        _refreshTimer.Start();
        _frozenCheckTimer.Start();
    }

    private void BuildCells(LayoutManager manager)
    {
        CellCanvas.Children.Clear();
        _runtimes.Clear();
        _frozenTrackers.Clear();

        foreach (var cell in manager.GetVisibleCells(Width, Height))
        {
            var border = new Border
            {
                BorderThickness = new Thickness(4),
                Background = Brushes.Black,
                Width = cell.Bounds.Width,
                Height = cell.Bounds.Height,
            };
            Canvas.SetLeft(border, cell.Bounds.X);
            Canvas.SetTop(border, cell.Bounds.Y);
            CellCanvas.Children.Add(border);

            var runtime = new CellRuntime { Cell = cell, Border = border };
            _runtimes[CellKey.Of(cell)] = runtime;

            StartContent(runtime);
        }
    }

    /// <summary>(Re)starts whatever a cell is bound to. Used for the initial render and every watchdog-triggered relaunch.</summary>
    private void StartContent(CellRuntime runtime)
    {
        switch (runtime.Cell.Binding?.Type)
        {
            case BindingType.Browser:
                StartBrowserCell(runtime);
                break;
            case BindingType.NativeApp:
                _ = StartNativeAppCellAsync(runtime);
                break;
            default:
                SetBorderStatus(runtime, CellStatus.Unbound, Brushes.Gray);
                runtime.Border.Child = new TextBlock
                {
                    Text = "Unassigned",
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                break;
        }
    }

    private void StartBrowserCell(CellRuntime runtime)
    {
        var webView = new WebView2 { Source = new Uri(runtime.Cell.Binding!.Value) };
        webView.NavigationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                runtime.ConsecutiveFailures = 0;
                runtime.LastLoadedUtc = DateTime.UtcNow;
                SetBorderStatus(runtime, CellStatus.Healthy, Brushes.Green);
            }
        };
        webView.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                LogAndGiveUp(runtime, $"WebView2 failed to initialize: {args.InitializationException?.Message}");
                return;
            }
            webView.CoreWebView2.ProcessFailed += (_, failedArgs) => OnBrowserProcessFailed(runtime, failedArgs);
        };

        runtime.WebView = webView;
        runtime.Process = null;
        runtime.Border.Child = webView;
        _frozenTrackers[CellKey.Of(runtime.Cell)] = new FrozenContentTracker(FrozenThreshold);
        SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);
    }

    private void OnBrowserProcessFailed(CellRuntime runtime, CoreWebView2ProcessFailedEventArgs args)
    {
        Dispatcher.Invoke(() =>
        {
            runtime.ConsecutiveFailures++;
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label,
                $"WebView2 process failed ({args.ProcessFailedKind}); restarting");

            if (runtime.ConsecutiveFailures > MaxConsecutiveFailures)
            {
                LogAndGiveUp(runtime, "Too many WebView2 failures in a row");
                return;
            }

            SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);
            runtime.WebView?.Reload();
        });
    }

    private async Task StartNativeAppCellAsync(CellRuntime runtime)
    {
        runtime.Border.Child = new TextBlock
        {
            Text = "Restarting…",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);

        runtime.AttemptInProgress = true;
        var parentHwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var process = await NativeAppHost.AttachAsync(runtime.Cell.Binding!.Value, parentHwnd, runtime.Cell.Bounds);
            runtime.Process = process;
            runtime.ConsecutiveFailures = 0;
            runtime.Border.Child = null; // the reparented Win32 window overlays this Border directly.
            SetBorderStatus(runtime, CellStatus.Healthy, Brushes.Green);
        }
        catch (ProcessExitedWithoutWindowException ex)
        {
            // The process already handed off to something we don't control (e.g. explorer.exe
            // asking the running shell to open a window) before exiting. That side effect already
            // happened — retrying would just repeat it, leaving another uncontrolled window behind
            // each time. Give up immediately rather than retrying blindly.
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"Failed to launch native app: {ex.Message}");
            LogAndGiveUp(runtime, "Process exited immediately after starting — likely handed off to an already-running instance we can't control (not retrying, to avoid repeating the side effect)");
        }
        catch (Exception ex)
        {
            runtime.ConsecutiveFailures++;
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"Failed to launch native app: {ex.Message}");

            if (runtime.ConsecutiveFailures > MaxConsecutiveFailures)
            {
                LogAndGiveUp(runtime, "Too many launch failures in a row");
            }
            // else: leave the "Restarting…" placeholder up; RunWatchdogPass retries
            // this cell (Process is still null) on its next tick.
        }
        finally
        {
            runtime.AttemptInProgress = false;
        }
    }

    private void RunWatchdogPass()
    {
        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.Cell.Binding?.Type != BindingType.NativeApp) continue;
            if (runtime.GaveUp) continue;
            if (runtime.AttemptInProgress) continue; // a launch is already awaiting — don't start another
            if (runtime.Process is { HasExited: false }) continue; // still running fine

            if (runtime.Process is { HasExited: true })
            {
                ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, "Native app exited unexpectedly; relaunching");
            }
            // else: Process is null — this cell never attached in the first place (e.g. a bad
            // path); retry it too, rather than leaving "Restarting…" up forever.

            runtime.Process = null;
            _ = StartNativeAppCellAsync(runtime);
        }
    }

    /// <summary>Reloads every healthy browser cell on a fixed interval so dashboards that don't self-refresh don't go stale silently.</summary>
    private void RunScheduledRefreshPass()
    {
        foreach (var runtime in _runtimes.Values)
        {
            if (runtime.Cell.Binding?.Type != BindingType.Browser) continue;
            if (runtime.WebView is null) continue;
            if (DateTime.UtcNow - runtime.LastLoadedUtc < RefreshSettings.Interval) continue;

            runtime.LastLoadedUtc = DateTime.UtcNow;
            runtime.WebView.Reload();
        }
    }

    /// <summary>Screenshots every healthy browser cell and flags any whose content hasn't changed past the threshold.</summary>
    private async Task RunFrozenContentPassAsync()
    {
        foreach (var (key, runtime) in _runtimes)
        {
            if (runtime.Cell.Binding?.Type != BindingType.Browser) continue;
            if (runtime.WebView?.CoreWebView2 is null) continue;
            if (!_frozenTrackers.TryGetValue(key, out var tracker)) continue;

            string hash;
            try
            {
                using var stream = new MemoryStream();
                await runtime.WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                hash = Convert.ToHexString(SHA256.HashData(stream.ToArray()));
            }
            catch
            {
                continue; // Capture can transiently fail (e.g. mid-navigation) — just skip this tick.
            }

            var changed = tracker.Observe(hash, DateTime.UtcNow);
            if (!changed) continue;

            if (tracker.IsFrozen)
            {
                ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"Frozen content detected (no visual change in {FrozenThreshold.TotalMinutes:0}m)");
                SetBorderStatus(runtime, CellStatus.Dark, Brushes.Red);
            }
            else
            {
                ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, "Content resumed updating");
                SetBorderStatus(runtime, CellStatus.Healthy, Brushes.Green);
            }
        }
    }

    private void LogAndGiveUp(CellRuntime runtime, string reason)
    {
        runtime.GaveUp = true;
        ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"Giving up: {reason}");
        runtime.Border.Child = new TextBlock
        {
            Text = "Dark",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetBorderStatus(runtime, CellStatus.Dark, Brushes.Red);
    }

    /// <summary>Updates only the border color and the cell's recorded status — never touches Child.</summary>
    private static void SetBorderStatus(CellRuntime runtime, CellStatus status, Brush borderBrush)
    {
        runtime.Border.BorderBrush = borderBrush;
        runtime.Cell = new Cell
        {
            Row = runtime.Cell.Row,
            Col = runtime.Cell.Col,
            SubRow = runtime.Cell.SubRow,
            SubCol = runtime.Cell.SubCol,
            Bounds = runtime.Cell.Bounds,
            Binding = runtime.Cell.Binding,
            Status = status,
        };
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _watchdogTimer.Stop();
        _refreshTimer.Stop();
        _frozenCheckTimer.Stop();
        foreach (var runtime in _runtimes.Values)
        {
            try
            {
                if (runtime.Process is { HasExited: false } process) process.CloseMainWindow();
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }
}
