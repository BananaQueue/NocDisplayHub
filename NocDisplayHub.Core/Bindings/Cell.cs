namespace NocDisplayHub.Core.Bindings;

/// <summary>A single visible cell to render, with its absolute pixel bounds.</summary>
public sealed class Cell
{
    public required int Row { get; init; }
    public required int Col { get; init; }
    public required CellBounds Bounds { get; init; }
    public CellBinding? Binding { get; init; }
    public CellStatus Status { get; init; }

    public string Label => $"Cell({Row},{Col})";
}
