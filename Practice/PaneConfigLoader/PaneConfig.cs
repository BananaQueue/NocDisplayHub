namespace PaneConfigLoader;

public class PaneConfig
{
    public int MonitorIndex { get; set; }
    public string PaneType { get; set; } = "";
    public string Source { get; set; } = "";    
    public int? RefreshSeconds { get; set; }
}