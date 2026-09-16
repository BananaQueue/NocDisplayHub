namespace MonitorSim;

// A class is a blueprint for an object. Each Monitor instance represents
// one physical screen in the NOC setup.
public class Monitor
{
    // Properties: like fields, but with get/set accessors.
    // 'required' means the caller MUST set this when creating a Monitor.
    public required int Index { get; set; }
    public required int Width { get; set; }
    public required int Height { get; set; }

    // A property with no setter — computed from other properties, read-only.
    public string Resolution => $"{Width}x{Height}";

    // A pane can be null (no content assigned yet) — nullable reference type.
    public Pane? AssignedPane { get; set; }

    // A method: behavior that belongs to this class.
    public void Assign(Pane pane)
    {
        AssignedPane = pane;
        Console.WriteLine($"Monitor {Index} ({Resolution}) now showing: {pane.Name}");
    }

    // Override ToString() so Console.WriteLine(monitor) prints something useful.
    public override string ToString()
    {
        var content = AssignedPane?.Name ?? "empty";
        return $"Monitor {Index} [{Resolution}] -> {content}";
    }
}
