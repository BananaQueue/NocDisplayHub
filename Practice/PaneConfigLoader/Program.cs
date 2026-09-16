using System.Text.Json;

namespace PaneConfigLoader;

public class Program
{
    static void Main()
    {
        List<PaneConfig>? panes;

        try
        {
            var json = File.ReadAllText("config.json");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            panes = JsonSerializer.Deserialize<List<PaneConfig>>(json);
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine("Error: config.json not found next to the executable.");
            return;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"Error: config.json contains invalid JSON - {ex.Message}");
            return;
        }

        if (panes == null || panes.Count == 0)
        {
            Console.WriteLine("No panes found in config.json.");
            return;
        }

        Console.WriteLine($"Loaded {panes.Count} pane(s) from config.json.\n");

        // Just prove it loaded correctly for now — validation logic comes next.
        foreach (var pane in panes)
        {
            Console.WriteLine($"Monitor {pane.MonitorIndex}: {pane.PaneType} -> {pane.Source}");
        }
    }
}