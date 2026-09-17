using System.Diagnostics;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using NocDisplayHub.Compositor;
using NocDisplayHub.Core.Config;
using NocDisplayHub.Core.Logging;
using NocDisplayHub.UI;
using Application = System.Windows.Application;

namespace NocDisplayHub;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    // Confirmed live: launching the exe a second time (e.g. by double-clicking it
    // directly instead of using the editor's "Launch Wall") spins up a fully
    // independent second process. Both instances then read/write the same
    // profile.json and activity.log — a race there swallowed a cell's own error
    // handling in testing. One named, machine-wide mutex is the standard way to
    // detect and refuse a second instance instead of letting that happen.
    private const string SingleInstanceMutexName = "Global\\NocDisplayHub_SingleInstance";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // initiallyOwned only actually grants ownership when this call is the one
            // that creates the mutex — since it already existed, we never own it, so
            // only Dispose (release the handle), never ReleaseMutex (would throw).
            mutex.Dispose();

            // Someone's already running (editor or wall, doesn't matter which) —
            // bring it forward instead of silently doing nothing, so this doesn't
            // just look broken, then exit without ever creating a window.
            ActivateExistingInstance();
            Shutdown();
            return;
        }
        _singleInstanceMutex = mutex;

        // A single unhandled exception on the UI thread would otherwise take
        // down the whole wall (all 6 monitors going dark), which is far worse
        // than one broken cell. Log it and keep the compositor alive.
        DispatcherUnhandledException += (_, args) =>
        {
            ActivityLog.Write(AppPaths.ActivityLogPath, $"Unhandled exception: {args.Exception}");
            args.Handled = true;
        };

        // Auto-launching on Windows startup must land straight on the wall —
        // never wait for someone to open the editor and click "Launch Wall"
        // (see CLAUDE.md). The editor is reached explicitly via --edit, e.g.
        // from a separate Start Menu shortcut, for intentional reconfiguration.
        Window mainWindow = e.Args.Contains("--edit", StringComparer.OrdinalIgnoreCase)
            ? new EditorWindow()
            : new CompositorWindow();
        mainWindow.Show();
    }

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        var existing = Process.GetProcessesByName(current.ProcessName)
            .FirstOrDefault(p => p.Id != current.Id);
        if (existing is { MainWindowHandle: var hwnd } && hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

