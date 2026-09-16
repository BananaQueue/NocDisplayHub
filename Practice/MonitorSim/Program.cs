namespace MonitorSim;

public class Program
{
    // 'async Task' instead of 'void' lets Main itself use 'await'.
    static async Task Main()
    {
        Console.WriteLine("=== NOC Monitor Simulator ===\n");

        // A List<T> is a resizable collection — you'll use this exact pattern
        // for Screen.AllScreens.ToList() in the real WPF app later.
        var monitors = new List<Monitor>
        {
            new Monitor { Index = 0, Width = 1920, Height = 1080 },
            new Monitor { Index = 1, Width = 2560, Height = 1440 },
            new Monitor { Index = 2, Width = 1920, Height = 1080 },
        };

        var panes = new List<Pane>
        {
            new Pane { Name = "Grafana Overview",   Type = PaneType.Web,         Source = "https://grafana.local/d/overview" },
            new Pane { Name = "Network Topology",    Type = PaneType.ExternalApp, Source = "C:\\Apps\\NetMon.exe" },
            new Pane { Name = "Ticket Queue Chart",  Type = PaneType.Chart,       Source = "internal://tickets" },
        };

        // Loop over both lists together by index — assign one pane per monitor.
        for (int i = 0; i < monitors.Count; i++)
        {
            monitors[i].Assign(panes[i]);
        }

        Console.WriteLine("\n--- Current layout ---");
        foreach (var monitor in monitors)
        {
            Console.WriteLine(monitor); // uses the ToString() override
        }

        Console.WriteLine("\n--- Running health checks ---");

        // Run an async "health check" against each monitor's pane.
        // In the real app this is where you'd ping a dashboard URL or
        // check that an external process is still responding.
        foreach (var monitor in monitors)
        {
            bool healthy = await CheckHealthAsync(monitor);

            // Conditional logic driving what would become AlertState in the real app.
            if (!healthy)
            {
                Console.WriteLine($"  ALERT: Monitor {monitor.Index} pane is unresponsive!");
            }
            else
            {
                Console.WriteLine($"  OK: Monitor {monitor.Index} pane is healthy.");
            }
        }

        Console.WriteLine("\nDone.");
    }

    // Simulates a health check that takes real time (like a network call would).
    // Returns Task<bool> because it's async and eventually produces a bool.
    static async Task<bool> CheckHealthAsync(Monitor monitor)
    {
        // Task.Delay simulates waiting on I/O (e.g. an HTTP request).
        // 'await' pauses this method WITHOUT blocking the thread — the app
        // stays responsive while "waiting."
        await Task.Delay(300);

        // Fake some variety: pretend monitor 2's pane is having trouble.
        return monitor.Index != 2;
    }
}
