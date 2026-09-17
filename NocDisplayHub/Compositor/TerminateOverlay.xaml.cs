using System.Windows;
using System.Windows.Input;

namespace NocDisplayHub.Compositor;

/// <summary>
/// A tiny always-on-top window layered above the compositor so a NOC operator
/// can end the wall without Alt+F4 or Task Manager. Kept dim until hovered so
/// it doesn't distract from cell content, but never fully hidden — see
/// HubDisplayLocator's sibling doc comment for why this has to be a separate
/// top-level window rather than a control drawn into CompositorWindow itself.
/// </summary>
public partial class TerminateOverlay : Window
{
    public event EventHandler? Terminated;

    public TerminateOverlay()
    {
        InitializeComponent();
    }

    public void PositionAt(double hostLeft, double hostTop, double hostWidth)
    {
        const double margin = 10;
        Left = hostLeft + hostWidth - Width - margin;
        Top = hostTop + margin;
    }

    private void RootBorder_MouseEnter(object sender, MouseEventArgs e) => RootBorder.Opacity = 1.0;

    private void RootBorder_MouseLeave(object sender, MouseEventArgs e) => RootBorder.Opacity = 0.45;

    private void RootBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Terminated?.Invoke(this, EventArgs.Empty);
}
