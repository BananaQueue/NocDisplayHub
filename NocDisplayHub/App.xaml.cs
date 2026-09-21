using System.Diagnostics;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
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

    // A second launch's own message loop never gets a chance to run (it calls Shutdown()
    // immediately) — RegisterWindowMessage/PostMessage is the standard way to reach an
    // ALREADY-running process's message pump from a brand new one without any shared
    // memory or named pipe of our own, the same category of purpose-built Windows API this
    // app already reaches for elsewhere (GlobalHotkey, WindowDragWatcher).
    private const string OpenEditorMessageName = "NocDisplayHub_OpenEditorRequest";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly uint OpenEditorMessage = RegisterWindowMessage(OpenEditorMessageName);

    private Mutex? _singleInstanceMutex;
    private Window? _currentWindow;
    private HwndSource? _currentWindowSource;

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
            // just look broken, then exit without ever creating a window. If THIS
            // launch specifically asked for --edit, tell that other process to switch
            // to the editor too (see SwitchToEditor) — confirmed live this used to be
            // silently dropped: a second "--edit" launch while the wall was already
            // running (the normal state on an actual kiosk, which auto-launches the
            // wall on every boot) just refocused the wall and never opened the editor
            // at all, making the wall unreachable to reconfigure without killing it
            // first via Task Manager.
            var wantsEditor = e.Args.Contains("--edit", StringComparer.OrdinalIgnoreCase);
            ActivateExistingInstance(wantsEditor);
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
        TrackWindow(mainWindow);
        mainWindow.Show();
    }

    /// <summary>
    /// Installs a message hook on whichever window is currently this process's top-level
    /// window, so a later --edit request from a second launch (see ActivateExistingInstance)
    /// can reach it, and remembers the window itself so SwitchToEditor knows what it's dealing
    /// with. Re-called by SwitchToEditor whenever the top-level window is actually replaced
    /// (wall closed, editor opened in its place) — the hook is tied to a specific HWND, so it
    /// has to move with it.
    /// </summary>
    private void TrackWindow(Window window)
    {
        _currentWindowSource?.RemoveHook(WndProc);
        _currentWindow = window;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _currentWindowSource = HwndSource.FromHwnd(hwnd);
        _currentWindowSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == OpenEditorMessage)
        {
            SwitchToEditor();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Handles a --edit request arriving from a second launch while this process is already
    /// running (see ActivateExistingInstance/OpenEditorMessage). If the current window is
    /// already the editor, just brings it forward. Otherwise — the common case, since the wall
    /// auto-launches standalone on every kiosk boot — closes the wall and opens the editor in
    /// its place, in this SAME process, rather than trying to run both at once: staying in one
    /// process is exactly what lets the editor control the wall live (UpdateCellBinding, the
    /// fullscreen hotkey's SecondaryCellLocator, ReloadFromProfile) — a second, separate process
    /// could never do any of that, so there's no way to keep the wall up AND gain editor access
    /// without an ownership handoff like this one. Confirmed with the user this trade-off
    /// (wall stops while the editor comes up) is acceptable — reconfiguring the kiosk is
    /// exactly the situation where you'd ask for --edit in the first place.
    /// </summary>
    private void SwitchToEditor()
    {
        if (_currentWindow is EditorWindow existingEditor)
        {
            existingEditor.Activate();
            return;
        }

        // Confirmed live: closing the old wall FIRST hit WPF's default ShutdownMode
        // (OnLastWindowClose) — for one instant it was the only open window, which triggered
        // automatic app shutdown before the new EditorWindow below ever got a chance to show
        // itself, and its own Activate() call then threw ("Cannot call DragMove or Activate
        // before a Window is shown") since the app was already mid-shutdown underneath it.
        // Opening the replacement BEFORE closing the old one means at least one window is
        // always open, so that automatic shutdown check never sees zero — no need to touch
        // ShutdownMode at all.
        var oldWindow = _currentWindow;
        var editor = new EditorWindow();
        TrackWindow(editor);
        editor.Show();
        editor.Activate();
        oldWindow?.Close();
    }

    private static void ActivateExistingInstance(bool requestEditor)
    {
        var current = Process.GetCurrentProcess();
        var existing = Process.GetProcessesByName(current.ProcessName)
            .FirstOrDefault(p => p.Id != current.Id);
        if (existing is not { MainWindowHandle: var hwnd } || hwnd == IntPtr.Zero) return;

        if (requestEditor)
        {
            PostMessage(hwnd, OpenEditorMessage, IntPtr.Zero, IntPtr.Zero);
        }
        SetForegroundWindow(hwnd);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _currentWindowSource?.RemoveHook(WndProc);
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
