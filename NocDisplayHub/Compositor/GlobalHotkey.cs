using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NocDisplayHub.Compositor;

/// <summary>
/// Registers one system-wide hotkey via RegisterHotKey/WM_HOTKEY. Unlike a WPF KeyDown handler,
/// which only fires while the target window itself owns keyboard focus, WM_HOTKEY is delivered to
/// the registering window's message queue regardless of which window currently has focus — needed
/// here because the wall's own cells routinely steal keyboard focus by design (an embedded WebView2
/// dashboard, a reparented native app), so a normal KeyDown handler on CompositorWindow would almost
/// never actually fire.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;

    /// <summary>False if another application already holds this exact key combination — the caller should log it and carry on, not treat a missing shortcut as fatal.</summary>
    public bool IsRegistered { get; }

    public event Action? Pressed;

    /// <summary>Must be constructed after <paramref name="window"/> has a live HWND (e.g. from its Loaded event) — there's nothing to attach a message hook to before then.</summary>
    public GlobalHotkey(Window window, int id, uint modifiers, uint virtualKey)
    {
        _id = id;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)
            ?? throw new InvalidOperationException("Window must have a live HWND before a hotkey can be attached to it.");
        _source.AddHook(WndProc);
        IsRegistered = RegisterHotKey(_source.Handle, _id, modifiers, virtualKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        if (IsRegistered) UnregisterHotKey(_source.Handle, _id);
    }
}
