namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// A cell's pixel rectangle within the compositor window. A plain struct (not
/// System.Windows.Rect) so the model layer has no WPF/UI dependency and can be
/// unit tested without a windowing framework.
/// </summary>
public readonly record struct CellBounds(double X, double Y, double Width, double Height);
