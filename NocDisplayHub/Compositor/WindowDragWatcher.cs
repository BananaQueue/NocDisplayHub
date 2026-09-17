using System.Runtime.InteropServices;

namespace NocDisplayHub.Compositor;

/// <summary>
/// Watches system-wide for any window finishing a user drag — not just our own —
/// via SetWinEventHook, the purpose-built Windows API for this: it needs no DLL
/// injection, unlike hooking another process's window messages directly. Powers
/// drag-and-drop capture: dragging any window onto a wall cell reparents it there,
/// the same underlying mechanism as a normal launch.
/// </summary>
public sealed class WindowDragWatcher : IDisposable
{
    private const uint EventSystemMoveSizeEnd = 0x000B;
    private const uint WinEventOutOfContext = 0x0000;

    // Confirmed useful here: events from our own process (the compositor's window, and
    // anything already reparented into it) are skipped automatically. A reparented window
    // has no caption left to drag by anyway (see NativeAppHost.Reparent), so this mainly
    // just saves us from ever having to filter our own windows out by hand.
    private const uint WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // Kept alive for the hook's lifetime — the GC could otherwise collect this delegate
    // while native code still holds a reference to it, crashing the process on the next event.
    private readonly WinEventDelegate _callback;
    private readonly IntPtr _hook;

    /// <summary>Fired on the UI thread (the hook delivers callbacks on the thread that installed it, given a running message pump) whenever any window finishes being dragged.</summary>
    public event Action<IntPtr>? WindowDropped;

    public WindowDragWatcher()
    {
        _callback = OnWinEvent;
        _hook = SetWinEventHook(EventSystemMoveSizeEnd, EventSystemMoveSizeEnd, IntPtr.Zero, _callback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW / CHILDID_SELF only — skip sub-object noise
        WindowDropped?.Invoke(hwnd);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }
}
