using Microsoft.Win32;

namespace NocDisplayHub.Startup;

/// <summary>
/// Registers the app to launch at Windows sign-in via a per-user Run key.
/// Pairs with an auto-login kiosk session (an OS-level setting outside this
/// app's control) to satisfy "auto-launches on Windows startup... do not
/// build a 'wait for manual login' path" (see CLAUDE.md). Opt-in via the
/// editor's checkbox rather than forced on every launch, since flipping a
/// user's login behavior silently is not this app's call to make.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NocDisplayHub";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Enable()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine this executable's path.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
