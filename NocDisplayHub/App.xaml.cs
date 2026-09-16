using System.Configuration;
using System.Data;
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
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}

