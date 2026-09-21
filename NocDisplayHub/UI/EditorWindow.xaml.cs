using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;
using NocDisplayHub.Compositor;
using NocDisplayHub.Core.Bindings;
using NocDisplayHub.Core.Config;
using NocDisplayHub.Startup;

namespace NocDisplayHub.UI;

/// <summary>
/// First-pass layout editor: split picker, clickable grid preview, and an
/// assignment panel for whichever cell is selected. Writes through the same
/// LayoutManager/ProfileStore the compositor reads from, so "Launch Wall"
/// always reflects what's on screen here.
/// </summary>
public partial class EditorWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    // Mirrors the palette in EditorWindow.xaml's Window.Resources — hardcoded here rather than
    // looked up per-cell since BuildCellBorder runs on every render of every visible cell.
    private static readonly Brush ControlSurfaceBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x21, 0x26));
    private static readonly Brush HairlineBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x2D, 0x33));
    private static readonly Brush TextPrimaryBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xEF));
    private static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x80));
    private static readonly Brush SignalGreenBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
    private static readonly Brush AccentCyanBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private const double BezelGap = 6; // dark gap between adjacent cells, echoing the physical monitor bezels this grid represents

    private LayoutManager _manager = new();
    private (int Row, int Col)? _selected;
    private CompositorWindow? _wallWindow;

    public EditorWindow()
    {
        InitializeComponent();

        // Confirmed live: with no explicit positioning, WindowStartupLocation="CenterScreen"
        // (the XAML default, left in place below as the single-monitor fallback) is not
        // guaranteed to land on the OS-designated primary/main monitor — for a PerMonitorV2-
        // aware app (our manifest applies process-wide, not just to CompositorWindow), its
        // centering math resolves against whichever monitor Windows' own placement heuristic
        // assigns the new window to at creation. Observed landing squarely on the hub's own
        // extended display instead of the main screen. Pin explicitly to a non-hub screen
        // when one can be unambiguously identified, the same way CompositorWindow already
        // pins itself to the hub — never leave this to ambient defaults.
        if (HubDisplayLocator.FindEditorWorkArea() is { } area)
        {
            // Confirmed live, the first version of this fix (plain Left/Top/Width/Height
            // assignment) produced inconsistent, sometimes off-screen results across otherwise
            // identical launches. Root cause: WPF's Left/Top/Width/Height are device-independent
            // units (96 DPI), but HubDisplayLocator returns raw Win32 physical pixels. Those
            // happen to be numerically identical on a monitor at 100% scaling — which is why
            // CompositorWindow's near-identical assignment against the hub display (100% scaled)
            // has always looked correct — but this machine's primary/laptop monitor runs at 125%,
            // where mixing the two unit systems produced unpredictable results depending on
            // WPF's internal per-monitor-DPI window-creation coercion. Sidestepped entirely by
            // positioning the real HWND directly via SetWindowPos, in physical pixels, forcing
            // the handle to exist first via EnsureHandle() — bypasses WPF's DIP layer completely,
            // the same approach NativeAppHost already uses for reparented native windows.
            WindowStartupLocation = WindowStartupLocation.Manual;
            var width = Math.Min(940, area.Width);
            var height = Math.Min(960, area.Height);
            var left = area.Left + (area.Width - width) / 2;
            var top = area.Top + (area.Height - height) / 2;

            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
        }
        else
        {
            // Single-screen case (the real kiosk, which only has the hub) — nothing to
            // disambiguate, so the XAML's CenterScreen default is already correct.
            // The XAML's 960x940 default is comfortable on a full-size monitor, but on a
            // smaller/laptop screen it can be taller than the actual usable desktop area,
            // pushing the bottom controls (Apply/Unassign, auto-launch checkbox) off-screen.
            // Clamp to the work area (excludes the taskbar) before the window is ever shown,
            // never below MinHeight/MinWidth — the grid preview already scales to fit via
            // its Viewbox, so a smaller window just means a smaller preview, not clipped controls.
            Height = Math.Min(Height, SystemParameters.WorkArea.Height);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        }

        Loaded += (_, _) =>
        {
            _manager = ProfileStore.Load(AppPaths.ProfilePath);
            RenderPreview();
            AutoStartCheckBox.IsChecked = StartupRegistration.IsEnabled();
        };
    }

    private void AutoStartCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (AutoStartCheckBox.IsChecked == true)
        {
            StartupRegistration.Enable();
        }
        else
        {
            StartupRegistration.Disable();
        }
    }

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        _manager.SetPreset(Enum.Parse<Preset>(tag));
        ResetSelection();
        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        RenderPreview();
    }

    private void ResetSelection()
    {
        _selected = null;
        AssignmentPanel.IsEnabled = false;
        SelectedCellLabel.Text = "Select a cell above";
    }

    private void RenderPreview()
    {
        PreviewCanvas.Children.Clear();

        var bounds = PresetLayout.GetBounds(_manager.CurrentPreset, PreviewCanvas.Width, PreviewCanvas.Height);
        foreach (var (row, col) in PresetLayout.GetVisibleSlots(_manager.CurrentPreset))
        {
            var b = bounds[(row, col)];
            var binding = _manager.GetBinding(row, col);
            var isSelected = _selected == (row, col);

            var border = BuildCellBorder(b, binding?.Value ?? "Unassigned", binding is not null, isSelected, row, col);
            var r = row;
            var c = col;
            border.MouseLeftButtonUp += (_, _) => SelectCell(r, c);

            Canvas.SetLeft(border, b.X + BezelGap / 2);
            Canvas.SetTop(border, b.Y + BezelGap / 2);
            PreviewCanvas.Children.Add(border);
        }
    }

    /// <summary>
    /// One cell of the preview, styled as a physical monitor in the array: a status LED
    /// (bound = lit green, unassigned = dark) stands in for "is this screen doing something",
    /// and a rack-style coordinate tag anchors it to a specific physical position — the preview
    /// is a miniature of the real wall, not just an abstract grid of buttons.
    /// </summary>
    private static Border BuildCellBorder(CellBounds b, string label, bool isBound, bool isSelected, int row, int col)
    {
        var content = new Grid();

        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = isBound ? TextPrimaryBrush : TextMutedBrush,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14),
        });

        content.Children.Add(new TextBlock
        {
            Text = $"{row},{col}",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 10,
            Foreground = TextMutedBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8),
        });

        var led = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = isBound ? SignalGreenBrush : HairlineBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 9, 9, 0),
        };
        if (isBound)
        {
            led.Effect = new DropShadowEffect { Color = Color.FromRgb(0x3D, 0xDC, 0x84), BlurRadius = 6, ShadowDepth = 0, Opacity = 0.9 };
        }
        content.Children.Add(led);

        return new Border
        {
            Width = b.Width - BezelGap,
            Height = b.Height - BezelGap,
            BorderBrush = isSelected ? AccentCyanBrush : HairlineBrush,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            Background = ControlSurfaceBrush,
            Cursor = System.Windows.Input.Cursors.Hand,
            Child = content,
        };
    }

    private void SelectCell(int row, int col)
    {
        _selected = (row, col);
        SelectedCellLabel.Text = $"Cell ({row}, {col})";
        AssignmentPanel.IsEnabled = true;

        var binding = _manager.GetBinding(row, col);
        ValueTextBox.Text = binding?.Value ?? "";
        SelectSourceType(binding?.Type ?? BindingType.Browser);
        RenderPreview();
    }

    private void SelectSourceType(BindingType type)
    {
        foreach (ComboBoxItem item in SourceTypeCombo.Items)
        {
            if ((string)item.Tag == type.ToString())
            {
                SourceTypeCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void SourceTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The XAML marks "Browser URL" IsSelected="True", so this fires once during
        // InitializeComponent itself — before ValueTextBox (declared later in the tree)
        // has been assigned yet. Bail out rather than null-refing on startup.
        if (ValueTextBox is null) return;

        // Native app values are a filesystem path, not something worth typing by hand —
        // clicking into the field opens a picker instead (see ValueTextBox_PreviewMouseLeftButtonDown).
        // A browser URL stays free-typed since that's the normal way to enter one.
        if (SourceTypeCombo.SelectedItem is ComboBoxItem { Tag: "NativeApp" })
        {
            ValueTextBox.IsReadOnly = true;
            ValueTextBox.Cursor = Cursors.Hand;
            BrowseFolderButton.Visibility = Visibility.Visible;
        }
        else
        {
            ValueTextBox.IsReadOnly = false;
            ValueTextBox.Cursor = Cursors.IBeam;
            BrowseFolderButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ValueTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SourceTypeCombo.SelectedItem is not ComboBoxItem { Tag: "NativeApp" }) return;

        e.Handled = true; // don't let the click place a caret in a field the user can't type into
        var dialog = new OpenFileDialog
        {
            Title = "Select an application, document, or image",
            // Not just .exe: a cell can also point at a document or image, which opens in
            // whatever app Windows has associated with it (ShellExecute's normal "open" verb —
            // the same mechanism already used to launch a bound .exe or folder).
            Filter = "Applications (*.exe)|*.exe|"
                + "Documents (*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt)|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt|"
                + "Images (*.jpg;*.jpeg;*.png;*.gif;*.bmp)|*.jpg;*.jpeg;*.png;*.gif;*.bmp|"
                + "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ValueTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        // A bound folder path (no .exe at all) opens straight into File Explorer at that
        // folder — NativeAppHost recognizes a directory path the same way it recognizes
        // explorer.exe and routes it through the same shell-window tracking.
        var dialog = new OpenFolderDialog { Title = "Select a folder to open in File Explorer" };
        if (dialog.ShowDialog(this) == true)
        {
            ValueTextBox.Text = dialog.FolderName;
        }
    }

    private void ApplyToCell_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;
        if (string.IsNullOrWhiteSpace(ValueTextBox.Text)) return;

        var typeTag = (string)((ComboBoxItem)SourceTypeCombo.SelectedItem).Tag;
        var type = Enum.Parse<BindingType>(typeTag);
        var value = ValueTextBox.Text.Trim();
        // A bare domain like "www.google.com" is what someone naturally types — normalize it the
        // way a browser address bar would, rather than letting a scheme-less URL reach the
        // compositor's Uri constructor, which throws (confirmed live: it crashed the whole wall).
        if (type == BindingType.Browser) value = UrlNormalizer.NormalizeBrowserUrl(value);
        var binding = new CellBinding(type, value);

        _manager.AssignBinding(r, c, binding);

        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        // Confirmed live: without this, the only way to see a URL change take effect at all was
        // a full Stop/Launch Wall cycle — restarting the whole compositor process just to change
        // one cell, which silently logged every other authenticated browser cell out too (a
        // session-scoped cookie/token gets cleared when the browser process itself restarts).
        // Push it to the running wall instead, scoped to just this one cell.
        _wallWindow?.UpdateCellBinding(r, c, binding);
        RenderPreview();
    }

    private void Unassign_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;

        _manager.ClearBinding(r, c);

        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        _wallWindow?.UpdateCellBinding(r, c, binding: null);
        ValueTextBox.Text = "";
        RenderPreview();
    }

    private void LaunchWall_Click(object sender, RoutedEventArgs e)
    {
        // The wall itself has no title bar or close button (it's a borderless kiosk
        // window), so this same button is how an operator stops it too — the editor
        // is where interaction belongs, not overlaid on top of live cell content.
        if (_wallWindow is not null)
        {
            _wallWindow.Close();
            return;
        }

        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        _wallWindow = new CompositorWindow();
        _wallWindow.Closed += (_, _) =>
        {
            _wallWindow = null;
            SetLaunchButtonState(running: false);
        };
        _wallWindow.Show();
        SetLaunchButtonState(running: true);
    }

    private void SetLaunchButtonState(bool running)
    {
        LaunchWallButton.Content = running ? "■ STOP WALL" : "LAUNCH WALL  ▶";
        LaunchWallButton.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");
    }
}
