using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private LayoutManager _manager = new();
    private (int Row, int Col)? _selected;
    private (int Row, int Col)? _drilldownTop;

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

            var border = BuildCellBorder(b, label, binding is not null || subGrid is not null, isSelected);
            var r = row;
            var c = col;
            border.MouseLeftButtonUp += (_, _) => SelectTopCell(r, c);

            Canvas.SetLeft(border, b.X);
            Canvas.SetTop(border, b.Y);
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

            var border = BuildCellBorder(b, binding?.Value ?? "Unassigned", binding is not null, isSelected);
            var sr = subRow;
            var sc = subCol;
            border.MouseLeftButtonUp += (_, _) => SelectSubCell(sr, sc);

            Canvas.SetLeft(border, b.X);
            Canvas.SetTop(border, b.Y);
            PreviewCanvas.Children.Add(border);
        }
    }

    private static Border BuildCellBorder(CellBounds b, string label, bool isBound, bool isSelected) => new()
    {
        Width = b.Width,
        Height = b.Height,
        BorderBrush = isSelected ? Brushes.DodgerBlue : Brushes.DimGray,
        BorderThickness = new Thickness(isSelected ? 3 : 1),
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
        Cursor = System.Windows.Input.Cursors.Hand,
        Child = new TextBlock
        {
            Text = label,
            Foreground = isBound ? Brushes.White : Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
        },
    };

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
        ProfileStore.Save(AppPaths.ProfilePath, _manager);
        new CompositorWindow().Show();
    }
}
