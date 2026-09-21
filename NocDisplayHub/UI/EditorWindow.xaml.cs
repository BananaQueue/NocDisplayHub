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
/// First-pass layout editor: profile selector, split picker, clickable grid preview, and an
/// assignment panel for whichever cell is selected. Writes through the same
/// LayoutManager/ProfileManager the compositor reads from, so "Launch Wall" always reflects
/// whatever profile is currently active here.
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
        if (HubDisplayLocator.FindEditorWorkArea() is not null)
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
            RepositionOnEditorScreen();
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
            _manager = ProfileManager.LoadActive();
            LoadProfilesIntoSelector();
            RenderPreview();
            AutoStartCheckBox.IsChecked = StartupRegistration.IsEnabled();
        };
    }

    /// <summary>
    /// Pins this window to a non-hub screen, in physical pixels via SetWindowPos (see the
    /// constructor's comment on why WPF's own Left/Top/Width/Height can't be trusted for this).
    /// Confirmed live: creating CompositorWindow on the hub's differently-scaled monitor
    /// retroactively disturbs THIS window's already-correct position — reproduced 2/2 — moving it
    /// back to roughly where it would have landed without the fix at all. Whatever WPF-internal
    /// per-process DPI reconciliation happens when a second top-level window appears on a
    /// different-DPI monitor, it isn't scoped to just the new window. Called again from
    /// LaunchWall_Click, right after showing the wall, to correct for exactly that.
    /// </summary>
    private void RepositionOnEditorScreen()
    {
        if (HubDisplayLocator.FindEditorWorkArea() is not { } area) return;

        var width = Math.Min(940, area.Width);
        var height = Math.Min(1050, area.Height);
        var left = area.Left + (area.Width - width) / 2;
        var top = area.Top + (area.Height - height) / 2;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, SwpNoZOrder | SwpNoActivate);
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
        ProfileManager.SaveActive(_manager);
        RenderPreview();
    }

    /// <summary>Repopulates the profile dropdown from disk and selects whichever is currently active — called after anything that adds, removes, or switches profiles.</summary>
    private void LoadProfilesIntoSelector()
    {
        ProfileSelectorCombo.SelectionChanged -= ProfileSelectorCombo_SelectionChanged;
        ProfileSelectorCombo.ItemsSource = ProfileManager.ListProfiles();
        ProfileSelectorCombo.SelectedItem = ProfileManager.GetActiveProfileName();
        ProfileSelectorCombo.SelectionChanged += ProfileSelectorCombo_SelectionChanged;
    }

    private void ProfileSelectorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileSelectorCombo.SelectedItem is not string name) return;

        ProfileManager.SetActiveProfileName(name);
        _manager = ProfileManager.LoadActive();
        PushProfileToWallIfRunning();
        ResetSelection();
        UpdateLiveCellButtonsVisibility();
        RenderPreview();
    }

    /// <summary>
    /// Pushes the current _manager state to an already-running wall, so switching (or deleting) a
    /// profile doesn't need a Stop/Launch Wall cycle. Unlike a single cell's URL — which
    /// UpdateCellBinding updates in place, cell by cell — a full profile swap can change the
    /// preset and every binding at once, too broad a change to reconcile incrementally; see
    /// CompositorWindow.ReloadFromProfile, which tears down every existing cell's content
    /// (honoring the same borrowed-vs-owned rules as closing the wall normally does) and rebuilds
    /// the whole grid from scratch, without the window itself ever closing.
    /// </summary>
    private void PushProfileToWallIfRunning() => _wallWindow?.ReloadFromProfile(_manager);

    private void SaveAsProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("Save Profile As", "Profile name:", ProfileManager.GetActiveProfileName()) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.InputText)) return;

        ProfileManager.SaveAs(dialog.InputText, _manager);
        LoadProfilesIntoSelector();
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileSelectorCombo.SelectedItem is not string name) return;

        var confirmed = MessageBox.Show(this, $"Delete profile \"{name}\"? This cannot be undone.",
            "Delete Profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed) return;

        if (!ProfileManager.DeleteProfile(name))
        {
            MessageBox.Show(this, "Can't delete the last remaining profile.", "Delete Profile",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // If the deleted profile was the active one, fall back to whatever's left before reloading.
        if (ProfileManager.GetActiveProfileName() == name)
        {
            ProfileManager.SetActiveProfileName(ProfileManager.ListProfiles()[0]);
        }
        _manager = ProfileManager.LoadActive();
        PushProfileToWallIfRunning();
        ResetSelection();
        UpdateLiveCellButtonsVisibility();
        LoadProfilesIntoSelector();
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

    /// <summary>
    /// Wired into CompositorWindow.SecondaryCellLocator (see LaunchWall_Click) so the fullscreen
    /// hotkey (Ctrl+Alt+F) targets whichever cell is currently selected here — the one clicked in
    /// the grid preview, highlighted with a cyan border, and shown in the assignment panel below —
    /// rather than requiring the mouse to be precisely hovering that cell's tiny preview rectangle
    /// at the exact moment the hotkey fires. Simpler and far more reliable than a hover-based hit
    /// test (an earlier version of this used PreviewCanvas.PointFromScreen for exactly that, but
    /// pixel-precise hovering proved needlessly fragile to rely on, especially across screens with
    /// different DPI scaling) — this reuses the same _selected state Apply/Unassign/Sync/Refresh
    /// already treat as "the cell I'm working on."
    /// </summary>
    private (int Row, int Col)? GetSelectedCellForFullscreen() => _selected;

    private void SelectCell(int row, int col)
    {
        _selected = (row, col);
        SelectedCellLabel.Text = $"Cell ({row}, {col})";
        AssignmentPanel.IsEnabled = true;

        var binding = _manager.GetBinding(row, col);
        ValueTextBox.Text = binding?.Value ?? "";
        SelectSourceType(binding?.Type ?? BindingType.Browser);
        UpdateLiveCellButtonsVisibility(); // SelectSourceType's SelectionChanged won't refire if the combo item didn't change
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
        UpdateLiveCellButtonsVisibility();
    }

    /// <summary>
    /// Shows the "sync from wall" / "refresh" buttons only when they could actually do something
    /// right now: the selected cell has a live, already-rendering WebView2 on the running wall.
    /// Checking the wall's actual current state (via GetCellCurrentUrl) rather than this editor's
    /// own SourceTypeCombo selection matters — that combo defaults to "Browser" for an Unassigned
    /// cell too (SelectCell has no real binding to reflect), which made the buttons appear for
    /// cells with nothing live to sync or refresh. Re-evaluated on cell selection, wall
    /// launch/stop, and after applying/unassigning a binding (both can change what the selected
    /// cell is actually showing).
    /// </summary>
    private void UpdateLiveCellButtonsVisibility()
    {
        var hasLiveContent = _selected is (int r, int c) && _wallWindow?.GetCellCurrentUrl(r, c) is not null;
        var visibility = hasLiveContent ? Visibility.Visible : Visibility.Collapsed;
        SyncFromWallButton.Visibility = visibility;
        RefreshCellButton.Visibility = visibility;
    }

    private void SyncFromWallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;
        if (_wallWindow?.GetCellCurrentUrl(r, c) is not { } currentUrl) return;

        // Deliberately just fills the field — the user still has to click "APPLY TO CELL" to
        // persist it. That keeps this a plain read (never touches the live cell or its session),
        // and the follow-up Apply then pushes back the EXACT URL the cell is already showing —
        // indistinguishable from an ordinary refresh, so it can't trip whatever session/referrer
        // logic an explicit Navigate() to a hand-typed URL sometimes does (see UpdateCellBinding).
        ValueTextBox.Text = currentUrl;
    }

    private void RefreshCellButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;
        _wallWindow?.ReloadCell(r, c);
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

        ProfileManager.SaveActive(_manager);
        // Confirmed live: without this, the only way to see a URL change take effect at all was
        // a full Stop/Launch Wall cycle — restarting the whole compositor process just to change
        // one cell, which silently logged every other authenticated browser cell out too (a
        // session-scoped cookie/token gets cleared when the browser process itself restarts).
        // Push it to the running wall instead, scoped to just this one cell.
        _wallWindow?.UpdateCellBinding(r, c, binding);
        UpdateLiveCellButtonsVisibility();
        RenderPreview();
    }

    private void Unassign_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;

        _manager.ClearBinding(r, c);

        ProfileManager.SaveActive(_manager);
        _wallWindow?.UpdateCellBinding(r, c, binding: null);
        UpdateLiveCellButtonsVisibility();
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

        ProfileManager.SaveActive(_manager);
        _wallWindow = new CompositorWindow();
        _wallWindow.SecondaryCellLocator = GetSelectedCellForFullscreen;
        _wallWindow.Closed += (_, _) =>
        {
            _wallWindow = null;
            SetLaunchButtonState(running: false);
            UpdateLiveCellButtonsVisibility();
        };
        _wallWindow.Show();
        // Known limitation, not fully solved (see CLAUDE.md): creating CompositorWindow on the
        // hub's differently-scaled monitor can retroactively disturb this window's position —
        // confirmed live, reproduced repeatedly. A fixed-delay re-correction and an event-driven
        // LocationChanged/SizeChanged watcher were both tried and neither reliably caught it, so
        // this call is left as a best-effort correction rather than a proven fix — it's exactly
        // right for the common case (editor launched on its own) and does no harm here.
        RepositionOnEditorScreen();
        SetLaunchButtonState(running: true);
        UpdateLiveCellButtonsVisibility();
    }

    private void SetLaunchButtonState(bool running)
    {
        LaunchWallButton.Content = running ? "■ STOP WALL" : "LAUNCH WALL  ▶";
        LaunchWallButton.Style = (Style)FindResource(running ? "DangerButton" : "PrimaryButton");
    }
}
