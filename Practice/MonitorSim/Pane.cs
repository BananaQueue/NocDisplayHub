namespace MonitorSim;

// An enum restricts a value to a fixed set of named options.
// This mirrors the real project's PaneType idea (web / externalApp / chart).
public enum PaneType
{
    Web,
    ExternalApp,
    Chart
}

// A simple data-holding class. 'record' would also work well here since
// Pane is mostly just data — but we use 'class' to keep things consistent
// with Monitor for this first project.
public class Pane
{
    public required string Name { get; set; }
    public required PaneType Type { get; set; }
    public required string Source { get; set; } // URL or file path, depending on Type

    public override string ToString() => $"{Name} ({Type}) <- {Source}";
}
