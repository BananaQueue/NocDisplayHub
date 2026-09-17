using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
///
/// A top-level cell can also be split into a sub-grid of smaller widgets
/// (Phase 3). Selecting a split cell disables the single-binding fields and
/// offers "Edit sub-cells", which drills the whole preview into that cell's
/// sub-grid — the same click/select/assign flow, one level down, with a
/// "Back to wall" button to return. v1 doesn't nest sub-grids further.
/// </summary>
public partial class EditorWindow : Window
{
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
    private (int Row, int Col)? _drilldownTop;
    private CompositorWindow? _wallWindow;

    public EditorWindow()
    {
        InitializeComponent();
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

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ResetSelection();
        RenderPreview();
    }

    private void ResetSelection()
    {
        _drilldownTop = null;
        _selected = null;
        AssignmentPanel.IsEnabled = false;
        SplitPanel.IsEnabled = false;
        SelectedCellLabel.Text = "Select a cell above";
    }

    private void RenderPreview()
    {
        PreviewCanvas.Children.Clear();
        BackButton.Visibility = _drilldownTop is null ? Visibility.Collapsed : Visibility.Visible;

        if (_drilldownTop is { } top)
        {
            RenderSubGrid(top);
        }
        else
        {
            RenderTopGrid();
        }
    }

    private void RenderTopGrid()
    {
        var bounds = PresetLayout.GetBounds(_manager.CurrentPreset, PreviewCanvas.Width, PreviewCanvas.Height);
        foreach (var (row, col) in PresetLayout.GetVisibleSlots(_manager.CurrentPreset))
        {
            var b = bounds[(row, col)];
            var subGrid = _manager.GetSubGrid(row, col);
            var binding = _manager.GetBinding(row, col);
            var label = subGrid is not null ? $"[Split {DescribePreset(subGrid.Preset)}]" : binding?.Value ?? "Unassigned";
            var isSelected = _drilldownTop is null && _selected == (row, col);

            var border = BuildCellBorder(b, label, binding is not null || subGrid is not null, isSelected, row, col);
            var r = row;
            var c = col;
            border.MouseLeftButtonUp += (_, _) => SelectTopCell(r, c);

            Canvas.SetLeft(border, b.X + BezelGap / 2);
            Canvas.SetTop(border, b.Y + BezelGap / 2);
            PreviewCanvas.Children.Add(border);
        }
    }

    private void RenderSubGrid((int Row, int Col) top)
    {
        var subGrid = _manager.GetSubGrid(top.Row, top.Col);
        if (subGrid is null)
        {
            // The split was cleared from under us (shouldn't normally happen); fall back to the top view.
            ResetSelection();
            RenderTopGrid();
            return;
        }

        var bounds = PresetLayout.GetBounds(subGrid.Preset, PreviewCanvas.Width, PreviewCanvas.Height);
        foreach (var (subRow, subCol) in PresetLayout.GetVisibleSlots(subGrid.Preset))
        {
            var b = bounds[(subRow, subCol)];
            var binding = subGrid.GetBinding(subRow, subCol);
            var isSelected = _selected == (subRow, subCol);

            var border = BuildCellBorder(b, binding?.Value ?? "Unassigned", binding is not null, isSelected, subRow, subCol);
            var sr = subRow;
            var sc = subCol;
            border.MouseLeftButtonUp += (_, _) => SelectSubCell(sr, sc);

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

    private static string DescribePreset(Preset preset) => preset switch
    {
        Preset.OneByOne => "1x1",
        Preset.OneByTwo => "1x2",
        Preset.TwoByOne => "2x1",
        Preset.TwoByTwo => "2x2",
        Preset.TwoByThree => "2x3",
        _ => preset.ToString(),
    };

    private void SelectTopCell(int row, int col)
    {
        _selected = (row, col);
        SelectedCellLabel.Text = $"Cell ({row}, {col})";
        SplitPanel.IsEnabled = true;

        var subGrid = _manager.GetSubGrid(row, col);
        if (subGrid is not null)
        {
            AssignmentPanel.IsEnabled = false;
            ValueTextBox.Text = "";
        }
        else
        {
            AssignmentPanel.IsEnabled = true;
            var binding = _manager.GetBinding(row, col);
            ValueTextBox.Text = binding?.Value ?? "";
            SelectSourceType(binding?.Type ?? BindingType.Browser);
        }
        RenderPreview();
    }

    private void SelectSubCell(int subRow, int subCol)
    {
        if (_drilldownTop is not { } top) return;

        _selected = (subRow, subCol);
        SelectedCellLabel.Text = $"Cell ({top.Row}, {top.Col}) → Widget ({subRow}, {subCol})";
        AssignmentPanel.IsEnabled = true;

        var subGrid = _manager.GetSubGrid(top.Row, top.Col)!;
        var binding = subGrid.GetBinding(subRow, subCol);
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
        }
        else
        {
            ValueTextBox.IsReadOnly = false;
            ValueTextBox.Cursor = Cursors.IBeam;
        }
    }

    private void ValueTextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SourceTypeCombo.SelectedItem is not ComboBoxItem { Tag: "NativeApp" }) return;

        e.Handled = true; // don't let the click place a caret in a field the user can't type into
        var dialog = new OpenFileDialog
        {
            Title = "Select an application",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ValueTextBox.Text = dialog.FileName;
        }
    }

    private void SubSplitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_drilldownTop is not null) return; // splitting only applies at the top level
        if (_selected is not (int row, int col)) return;

        var tag = ((Button)sender).Tag as string;
        if (tag is null)
        {
            _manager.ClearSubGrid(row, col);
            ProfileStore.Save(AppPaths.ProfilePath, _manager);
            SelectTopCell(row, col);
            return;
        }

        _manager.SetSubGrid(row, col, Enum.Parse<Preset>(tag));
        ProfileStore.Save(AppPaths.ProfilePath, _manager);

        _drilldownTop = (row, col);
        _selected = null;
        AssignmentPanel.IsEnabled = false;
        SelectedCellLabel.Text = "Select a widget above";
        RenderPreview();
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

        if (_drilldownTop is { } top)
        {
            _manager.GetSubGrid(top.Row, top.Col)!.AssignBinding(r, c, binding);
        }
        else
        {
            _manager.AssignBinding(r, c, binding);
        }

        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        RenderPreview();
    }

    private void Unassign_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not (int r, int c)) return;

        if (_drilldownTop is { } top)
        {
            _manager.GetSubGrid(top.Row, top.Col)!.ClearBinding(r, c);
        }
        else
        {
            _manager.ClearBinding(r, c);
        }

        ProfileStore.Save(AppPaths.ProfilePath, _manager);
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
