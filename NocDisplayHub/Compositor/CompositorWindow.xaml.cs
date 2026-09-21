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
/// </summary>
public partial class CompositorWindow : Window
{
    private const int MaxConsecutiveFailures = 5;
    private const double CellBorderThickness = 4;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrozenCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FrozenThreshold = TimeSpan.FromMinutes(5);

    private readonly Dictionary<CellKey, CellRuntime> _runtimes = new();
    private readonly Dictionary<CellKey, FrozenContentTracker> _frozenTrackers = new();
    private readonly DispatcherTimer _watchdogTimer;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _frozenCheckTimer;
    private WindowDragWatcher? _dragWatcher;

    private readonly record struct CellKey(int Row, int Col)
    {
        public static CellKey Of(Cell cell) => new(cell.Row, cell.Col);
    }

    public CompositorWindow()
    {
        InitializeComponent();
        Width = HubSpec.InputWidth;
        Height = HubSpec.InputHeight;

        if (HubDisplayLocator.FindHubOrigin() is { } origin)
        {
            Left = origin.Left;
            Top = origin.Top;
        }
        else
        {
            // Zero or multiple screens matched the hub's resolution — can't tell
            // which one is the hub, so fall back to the XAML default (0,0) and
            // leave a trail instead of silently guessing wrong.
            ActivityLog.Write(AppPaths.ActivityLogPath,
                "Could not auto-detect the hub display (no unique screen matched HubSpec resolution); defaulting to (0,0)");
        }

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

        _dragWatcher = new WindowDragWatcher();
        _dragWatcher.WindowDropped += OnWindowDropped;
    }

    /// <summary>
    /// Any window dropped onto a cell's screen area gets captured into it — deliberately
    /// no "capture mode" toggle, per explicit direction: a wrong drop is trivially fixed by
    /// dragging the right window on top of it afterward, so the extra step isn't worth it.
    /// </summary>
    private void OnWindowDropped(IntPtr hwnd)
    {
        if (NativeAppHost.TryGetWindowRect(hwnd) is not { } windowRect) return;

        var centerX = windowRect.X + (windowRect.Width / 2);
        var centerY = windowRect.Y + (windowRect.Height / 2);

        foreach (var runtime in _runtimes.Values)
        {
            var cellScreenX = Left + runtime.Cell.Bounds.X;
            var cellScreenY = Top + runtime.Cell.Bounds.Y;
            if (centerX < cellScreenX || centerX >= cellScreenX + runtime.Cell.Bounds.Width) continue;
            if (centerY < cellScreenY || centerY >= cellScreenY + runtime.Cell.Bounds.Height) continue;

            CaptureDroppedWindow(runtime, hwnd);
            return;
        }
    }

    private void CaptureDroppedWindow(CellRuntime runtime, IntPtr newHwnd)
    {
        if (runtime.TrackedWindowHandle == newHwnd) return; // already showing exactly this window

        // A reparented window has no caption left to drag by, so this shouldn't normally be
        // reachable — but if this exact window somehow is still claimed by a different cell,
        // don't leave two cells silently pointing at the same handle.
        foreach (var other in _runtimes.Values)
        {
            if (other == runtime || other.TrackedWindowHandle != newHwnd) continue;
            other.TrackedWindowHandle = null;
            other.Process = null;
            other.IsDragDropCaptured = false;
            other.Border.Child = new TextBlock
            {
                Text = "Unassigned",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            SetBorderStatus(other, CellStatus.Unbound, Brushes.Gray);
        }

        // Whatever this cell was showing before doesn't just vanish — it becomes an ordinary
        // floating window again, never touching its process, exactly as if it had never been
        // assigned to a cell. This is what makes a wrong drop trivially correctable: drag the
        // right window on top of it, and the wrong one is simply given back, not lost.
        if (runtime.TrackedWindowHandle is { } oldHwnd)
        {
            NativeAppHost.Release(oldHwnd);
        }
        runtime.WebView?.Dispose();
        runtime.WebView = null;

        var parentHwnd = new WindowInteropHelper(this).Handle;
        var insetBounds = InsetForBorder(runtime.Cell.Bounds);
        if (!NativeAppHost.Reparent(newHwnd, parentHwnd, insetBounds))
        {
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, "Drag-and-drop capture failed: could not reparent the dropped window");
            return;
        }

        runtime.TrackedWindowHandle = newHwnd;
        runtime.Process = null; // we don't own whatever process this window actually belongs to
        runtime.IsDragDropCaptured = true; // give it back on wall shutdown, never ask it to close
        runtime.ConsecutiveFailures = 0;
        runtime.GaveUp = false; // a manual drop always takes effect, even correcting a cell that had given up
        runtime.Border.Child = null; // the reparented Win32 window overlays this Border directly
        SetBorderStatus(runtime, CellStatus.Healthy, Brushes.Green);
        ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, "Captured a window via drag-and-drop");
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
                BorderThickness = new Thickness(CellBorderThickness),
                Background = Brushes.Black,
                Width = cell.Bounds.Width,
                Height = cell.Bounds.Height,
            };
            Canvas.SetLeft(border, cell.Bounds.X);
            Canvas.SetTop(border, cell.Bounds.Y);
            CellCanvas.Children.Add(border);

            var runtime = new CellRuntime { Cell = cell, Border = border };
            _runtimes[CellKey.Of(cell)] = runtime;

            try
            {
                StartContent(runtime);
            }
            catch (Exception ex)
            {
                // A single malformed binding (confirmed live: a URL missing "https://" threw
                // here) must never take down the other 5 working cells — isolate the failure
                // to just this one cell instead of letting it escape BuildCells entirely.
                LogAndGiveUp(runtime, $"Failed to start cell content: {ex.Message}", displayText: DescribeStartupError(runtime, ex));
            }
        }
    }

    /// <summary>Produces a short, specific on-screen message for a cell that failed to start, instead of a generic "Dark".</summary>
    private static string DescribeStartupError(CellRuntime runtime, Exception ex)
    {
        if (runtime.Cell.Binding?.Type == BindingType.Browser && ex is UriFormatException)
        {
            return $"Invalid URL:\n{runtime.Cell.Binding.Value}";
        }
        return "Error:\n" + ex.Message;
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
                return;
            }

            // Previously unhandled entirely: a failed navigation (e.g. no network yet on a
            // kiosk boot, DNS not resolving, wrong host) left the border on whatever state it
            // already had — usually the initial orange "Restarting" — forever, since nothing
            // here ever changed it and RunWatchdogPass deliberately skips Browser cells. The
            // only thing that would ever retry it was RunScheduledRefreshPass, up to 30 minutes
            // later. Show it as failed immediately and retry on a short backoff instead.
            runtime.ConsecutiveFailures++;
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label,
                $"Navigation failed ({args.WebErrorStatus}); retrying");

            if (runtime.ConsecutiveFailures > MaxConsecutiveFailures)
            {
                LogAndGiveUp(runtime, $"Too many navigation failures in a row ({args.WebErrorStatus})");
                return;
            }

            SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);
            _ = RetryNavigationAsync(runtime, webView);
        };
        webView.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                LogAndGiveUp(runtime, $"WebView2 failed to initialize: {args.InitializationException?.Message}");
                return;
            }
            webView.CoreWebView2.ProcessFailed += (_, failedArgs) => OnBrowserProcessFailed(runtime, failedArgs);

            // Tried and reverted earlier the same day: setting ZoomFactor = 1/DpiScaleX to
            // compensate for a reported "zoomed in and cropped" look. Wrong fix for the wrong
            // theory — that treated ZoomFactor as a DPI/rasterization-scale correction, when it's
            // actually Chromium's real page-zoom feature (the same thing as Ctrl+/Ctrl- in a
            // normal browser). Confirmed live that made things worse.
            //
            // Used correctly here for a different, deliberate purpose: a WebView2 control, like
            // any embedded browser, always treats its own allocated pixel area as 100% of the
            // viewport (100vw/100vh) — it has no concept of "the rest of the desktop", so a page
            // designed for a full monitor (fixed-width cards, desktop-oriented layout) looks
            // cramped, or shows scrollbars, once squeezed into a small grid cell. Explicit user
            // request: make every browser cell render as though it had a full HubSpec-sized
            // viewport, then let that rendering scale down to the cell's actual size — the exact
            // opposite direction from the reverted fix above, and for a genuinely different
            // reason (deliberately faking a bigger viewport, not correcting a device-scale
            // mismatch). Unlike a WPF-level visual transform, this is safe for a reparented native
            // HwndHost control like WebView2: the zoom is internal to Chromium, so input
            // coordinates are still mapped correctly by WebView2 itself — a click on a visually
            // shrunk button still lands correctly, no interactivity is actually lost.
            //
            // Confirmed live: using the WIDTH ratio alone left a visible empty gap at the bottom
            // of every cell whose aspect ratio is narrower than HubSpec's 16:9 (any grid split
            // more than 1 row — a 2x3 cell is roughly 1.19:1). ZoomFactor is one uniform scalar,
            // so it can only match ONE dimension exactly; matching width alone made the page's
            // EFFECTIVE viewport (physical size / zoom) taller than a real 16:9 monitor would be
            // (e.g. a 640x540 2x3 cell at width-only zoom ≈0.333 effectively sees ~1920x1620) —
            // dashboards designed for a normal screen don't stretch to fill that extra height, so
            // it shows as blank space below the content. Using the LARGER of the two ratios instead
            // makes the effective viewport's SMALLER dimension match HubSpec exactly (no gap on
            // that axis), at the cost of the other dimension coming in narrower than a full
            // HubSpec-sized screen would be — i.e. content fills the cell completely, cropping
            // slightly on one axis instead of leaving empty space on the other. A full 1x1 cell
            // (already exactly HubSpec-sized) still computes to exactly 1.0 either way.
            var zoomFactor = Math.Max(
                runtime.Cell.Bounds.Width / HubSpec.InputWidth,
                runtime.Cell.Bounds.Height / HubSpec.InputHeight);
            webView.ZoomFactor = zoomFactor;

            NudgeWebViewLayout(webView);
        };

        runtime.WebView = webView;
        runtime.Process = null;
        runtime.Border.Child = webView;
        _frozenTrackers[CellKey.Of(runtime.Cell)] = new FrozenContentTracker(FrozenThreshold);
        SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);
    }

    /// <summary>
    /// Forces WebView2 to recompute its native Chromium composition surface against its
    /// actual allocated space. Confirmed on real hardware: on this compositor's borderless
    /// window, explicitly positioned on a non-primary monitor, WebView2's surface can end
    /// up mismatched with the WPF layout size it was given, leaving a visible black gap
    /// around the rendered page — the WPF-level equivalent of reparented native windows
    /// not recomputing their DirectComposition surface from a single resize (see
    /// NativeAppHost.Reposition). Same remedy, different layer: force two genuinely
    /// different layout passes back-to-back so WebView2's HwndHost re-arranges its surface.
    /// </summary>
    private static void NudgeWebViewLayout(WebView2 webView)
    {
        webView.Dispatcher.BeginInvoke(new Action(() =>
        {
            var width = webView.ActualWidth;
            var height = webView.ActualHeight;
            if (width <= 1 || height <= 1) return; // not laid out yet — nothing to nudge

            webView.Width = width - 1;
            webView.Height = height - 1;
            webView.UpdateLayout();
            webView.Width = double.NaN;
            webView.Height = double.NaN;
            webView.UpdateLayout();
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Retries a failed browser navigation after a short backoff (2s per consecutive failure,
    /// capped at 10s) rather than immediately — a network hiccup right at boot is likely to
    /// need a moment, and hammering CoreWebView2.Reload() in a tight loop would just repeat
    /// the same failure MaxConsecutiveFailures times almost instantly.
    /// </summary>
    private static async Task RetryNavigationAsync(CellRuntime runtime, WebView2 webView)
    {
        var delay = TimeSpan.FromSeconds(Math.Min(10, 2 * runtime.ConsecutiveFailures));
        await Task.Delay(delay);
        if (webView.CoreWebView2 is not null)
        {
            webView.CoreWebView2.Reload();
        }
        else
        {
            webView.Reload();
        }
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
            // Browser cells are WPF children of the Border, so WPF automatically insets them
            // within its BorderThickness padding, keeping the colored status border visible.
            // Reparented native windows are separate HWNDs positioned by raw SetWindowPos with
            // no such automatic inset — sized to the cell's exact outer bounds, they can cover
            // the border completely if the app fills its window precisely (confirmed on hardware:
            // Character Map did this while Paint, filling slightly short, happened not to).
            // Inset explicitly so the border is reliably visible regardless of how precisely a
            // given app fills its window.
            var insetBounds = InsetForBorder(runtime.Cell.Bounds);
            var result = await NativeAppHost.AttachAsync(runtime.Cell.Binding!.Value, parentHwnd, insetBounds);
            runtime.Process = result.Process;
            // Always tracked by handle now, even when we also own the process — the watchdog
            // needs to know exactly which window is showing right now, independently of whether
            // the launching process is still alive (see CellRuntime.TrackedWindowHandle).
            runtime.TrackedWindowHandle = result.WindowHandle;
            runtime.IsDragDropCaptured = false; // this cell is a normal launch we own, not a borrowed window
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
        var parentHwnd = new WindowInteropHelper(this).Handle;
        foreach (var runtime in _runtimes.Values)
        {
            // A drag-and-drop-captured cell needs the same liveness checking even when its
            // *persisted* binding isn't NativeApp (or isn't anything at all, for a cell that
            // was Unassigned before capture) — confirmed while reviewing this: without the
            // IsDragDropCaptured check here, a dragged-in window dying went completely
            // undetected, since the very first line skipped the cell entirely.
            if (runtime.Cell.Binding?.Type != BindingType.NativeApp && !runtime.IsDragDropCaptured) continue;
            if (runtime.GaveUp) continue;
            if (runtime.AttemptInProgress) continue; // a launch is already awaiting — don't start another

            if (runtime.TrackedWindowHandle is { } hwnd && NativeAppHost.IsWindowAlive(hwnd))
            {
                continue; // still showing the window we last reparented — genuinely fine
            }

            // The window we were showing (if any) is gone. Confirmed live with Outlook: a
            // still-running process can legitimately swap to a brand new window — a splash/
            // loading frame gets destroyed once the real one is ready — without ever exiting.
            // Checking only Process.HasExited (the old behavior) missed this entirely: the cell
            // stayed "Healthy" while showing nothing. Look for that process's *current* main
            // window before assuming the app actually died.
            if (runtime.Process is { HasExited: false } process)
            {
                process.Refresh();
                var currentHandle = process.MainWindowHandle;
                if (currentHandle != IntPtr.Zero && currentHandle != runtime.TrackedWindowHandle
                    && NativeAppHost.Reparent(currentHandle, parentHwnd, InsetForBorder(runtime.Cell.Bounds)))
                {
                    ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label,
                        "Reparented a new window from the same still-running process (the previous one was likely a splash/loading window)");
                    runtime.TrackedWindowHandle = currentHandle;
                    runtime.ConsecutiveFailures = 0;
                    SetBorderStatus(runtime, CellStatus.Healthy, Brushes.Green);
                    continue;
                }

                // Alive but no usable window yet (or reparenting it failed) — could just be
                // mid-swap; give it a few more ticks rather than tearing down a live process,
                // but not forever.
                runtime.ConsecutiveFailures++;
                if (runtime.ConsecutiveFailures > MaxConsecutiveFailures)
                {
                    LogAndGiveUp(runtime, "Process is still running but never produced a window we could show");
                    continue;
                }
                SetBorderStatus(runtime, CellStatus.Restarting, Brushes.Orange);
                continue;
            }

            var wasDragDropCaptured = runtime.IsDragDropCaptured;

            if (runtime.TrackedWindowHandle is not null)
            {
                ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label,
                    wasDragDropCaptured ? "Dragged-in window closed" : "Tracked window closed unexpectedly; relaunching");
            }
            else if (runtime.Process is { HasExited: true })
            {
                ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, "Native app exited unexpectedly; relaunching");
            }
            // else: Process and TrackedWindowHandle both null — this cell never attached in the
            // first place (e.g. a bad path); retry it too, rather than leaving "Restarting…" up forever.

            runtime.TrackedWindowHandle = null;
            runtime.Process = null;
            runtime.IsDragDropCaptured = false;

            if (wasDragDropCaptured)
            {
                // There's no path to "relaunch" an arbitrary window someone dragged in — it
                // isn't a path or URL we own, just whatever they happened to have open. Fall
                // back to this cell's actual configured binding instead (which might just be
                // Unassigned), the same dispatch BuildCells itself uses, rather than blindly
                // calling StartNativeAppCellAsync with a binding that may not even be a native
                // app path (or may be null entirely, which would throw).
                StartContent(runtime);
            }
            else
            {
                _ = StartNativeAppCellAsync(runtime);
            }
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

    private void LogAndGiveUp(CellRuntime runtime, string reason, string? displayText = null)
    {
        runtime.GaveUp = true;
        ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"Giving up: {reason}");
        runtime.Border.Child = new TextBlock
        {
            Text = displayText ?? "Dark",
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetBorderStatus(runtime, CellStatus.Dark, Brushes.Red);
    }

    /// <summary>Shrinks a cell's bounds by the border thickness on every side, so a reparented native window sits inside the colored status border instead of potentially covering it.</summary>
    private static CellBounds InsetForBorder(CellBounds bounds) => new(
        bounds.X + CellBorderThickness,
        bounds.Y + CellBorderThickness,
        bounds.Width - (2 * CellBorderThickness),
        bounds.Height - (2 * CellBorderThickness));

    /// <summary>Updates only the border color and the cell's recorded status — never touches Child.</summary>
    private static void SetBorderStatus(CellRuntime runtime, CellStatus status, Brush borderBrush)
    {
        runtime.Border.BorderBrush = borderBrush;
        runtime.Cell = new Cell
        {
            Row = runtime.Cell.Row,
            Col = runtime.Cell.Col,
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
        _dragWatcher?.Dispose();
        foreach (var runtime in _runtimes.Values)
        {
            try
            {
                TeardownCellContent(runtime);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    /// <summary>
    /// Releases/closes/disposes whatever a cell is currently showing, honoring the same
    /// ownership rules as everywhere else: give back a drag-and-drop-borrowed window rather
    /// than closing it (see <see cref="CellRuntime.IsDragDropCaptured"/>), close a window or
    /// process we actually launched ourselves. Resets the runtime's tracking fields so it's
    /// ready for <see cref="StartContent"/> to populate again. Shared by <see cref="OnClosed"/>
    /// (the whole wall shutting down) and <see cref="UpdateCellBinding"/> (one cell's binding
    /// changing live) — the exact same teardown either way, just at different scopes.
    /// </summary>
    private static void TeardownCellContent(CellRuntime runtime)
    {
        if (runtime.TrackedWindowHandle is { } hwnd && NativeAppHost.IsWindowAlive(hwnd))
        {
            if (runtime.IsDragDropCaptured)
            {
                // Confirmed live: sending WM_CLOSE to a drag-and-drop-captured window
                // (a window we only borrowed, never launched) took down every other
                // window of that same app too — closing the wall with a dragged-in
                // Chrome tab closed the whole browser, including tabs never touched by
                // the wall. Chrome-family multi-window apps decide whether to fully quit
                // based on how many top-level windows they still have, and reparenting
                // silently drops a window from that count without the app knowing, so it
                // can conclude its last real window just closed even when others are
                // still open elsewhere. Give it back instead — never ask it to close.
                NativeAppHost.Release(hwnd);
            }
            else
            {
                // A window we explicitly launched or that Explorer opened for this cell
                // — safe to ask to close, since it exists only because of this cell.
                NativeAppHost.CloseWindow(hwnd);
            }
        }
        else if (runtime.Process is { HasExited: false } process)
        {
            process.CloseMainWindow();
        }

        runtime.WebView?.Dispose();
        runtime.WebView = null;
        runtime.TrackedWindowHandle = null;
        runtime.Process = null;
        runtime.IsDragDropCaptured = false;
    }

    /// <summary>
    /// Pushes a binding change to an already-running wall for one cell, without restarting the
    /// whole compositor. Confirmed live: the only way to see a cell's URL change take effect
    /// used to be a full Stop/Launch Wall cycle, which tears down every cell's WebView2 (and its
    /// underlying browser process) just to change one — for a site using session-scoped cookies
    /// or tokens, that silently logs every authenticated cell out, not just the one being edited.
    ///
    /// If the cell already has a live WebView2 and the new binding is also Browser, navigates the
    /// EXISTING control to the new URL in place — the same top-level browsing context, the same
    /// profile — exactly like typing a new address into an already-open tab, so cookies AND
    /// sessionStorage both survive. Anything else (a type change, or no live WebView2 to reuse)
    /// falls back to a full teardown-and-restart, but still scoped to just this one cell — every
    /// other cell's content, and its session, is completely untouched either way.
    /// </summary>
    public void UpdateCellBinding(int row, int col, CellBinding? binding)
    {
        if (!_runtimes.TryGetValue(new CellKey(row, col), out var runtime)) return;

        if (binding?.Type == BindingType.Browser
            && runtime.Cell.Binding?.Type == BindingType.Browser
            && runtime.WebView?.CoreWebView2 is { } coreWebView)
        {
            runtime.Cell = new Cell { Row = row, Col = col, Bounds = runtime.Cell.Bounds, Binding = binding, Status = runtime.Cell.Status };
            ActivityLog.Write(AppPaths.ActivityLogPath, runtime.Cell.Label, $"URL updated live, in place (no restart): {binding.Value}");
            coreWebView.Navigate(binding.Value);
            return;
        }

        try
        {
            TeardownCellContent(runtime);
        }
        catch
        {
            // Best-effort cleanup only — still proceed to start the new binding below.
        }

        runtime.Cell = new Cell { Row = row, Col = col, Bounds = runtime.Cell.Bounds, Binding = binding, Status = CellStatus.Unbound };
        runtime.GaveUp = false;
        runtime.ConsecutiveFailures = 0;
        StartContent(runtime);
    }

    /// <summary>
    /// Whatever URL a live browser cell has actually navigated to right now — via in-app links,
    /// redirects, JS navigation, anything — not necessarily whatever it was originally bound to.
    /// Confirmed live: even <see cref="UpdateCellBinding"/>'s in-place Navigate() can still trip a
    /// site's own session/referrer logic, since an explicit Navigate() to a URL typed from memory
    /// can look like an out-of-band jump even on the same origin. Reading back the exact current
    /// URL (including any SPA router state, query string, or token the site itself appended) and
    /// persisting *that* — rather than retyping one by hand — never looks any different from
    /// ordinary browsing, so it can't trigger whatever the site's own logic is reacting to. Returns
    /// null if the cell isn't a live, currently-rendering browser cell.
    /// </summary>
    public string? GetCellCurrentUrl(int row, int col) =>
        _runtimes.TryGetValue(new CellKey(row, col), out var runtime) ? runtime.WebView?.CoreWebView2?.Source : null;

    /// <summary>
    /// Reloads a live browser cell in place — the WebView2 equivalent of pressing F5 on whatever
    /// page it's actually showing right now. Deliberately does not touch the cell's binding or
    /// profile.json at all; this is purely "unstick a frozen page," not a way to change what a
    /// cell points at. Safe for a session-sensitive page for the same reason GetCellCurrentUrl is:
    /// reloading the exact page already displayed is indistinguishable from ordinary browsing.
    /// </summary>
    public void ReloadCell(int row, int col)
    {
        if (_runtimes.TryGetValue(new CellKey(row, col), out var runtime))
        {
            runtime.WebView?.CoreWebView2?.Reload();
        }
    }
}
