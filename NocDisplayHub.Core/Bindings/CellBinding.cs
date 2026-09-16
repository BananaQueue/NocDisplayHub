namespace NocDisplayHub.Core.Bindings;

/// <summary>A cell's assigned content: a browser URL or a native app's executable path.</summary>
public sealed record CellBinding(BindingType Type, string Value);
