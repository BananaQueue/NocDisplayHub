using NocDisplayHub.Core.Logging;

namespace NocDisplayHub.Tests;

public class ActivityLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"noc-activity-test-{Guid.NewGuid()}.log");

    [Fact]
    public void Write_WithCell_IncludesTimestampCellAndMessage()
    {
        ActivityLog.Write(_path, "Cell(1,2)", "Native app exited unexpectedly; relaunching");

        var line = File.ReadAllText(_path);
        Assert.Contains("Cell(1,2)", line);
        Assert.Contains("Native app exited unexpectedly; relaunching", line);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", line);
    }

    [Fact]
    public void Write_AppendsRatherThanOverwrites()
    {
        ActivityLog.Write(_path, "Cell(0,0)", "first event");
        ActivityLog.Write(_path, "Cell(0,0)", "second event");

        var lines = File.ReadAllLines(_path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first event", lines[0]);
        Assert.Contains("second event", lines[1]);
    }

    [Fact]
    public void Write_CreatesParentDirectoryIfMissing()
    {
        var nested = Path.Combine(Path.GetTempPath(), $"noc-activity-dir-{Guid.NewGuid()}", "activity.log");

        ActivityLog.Write(nested, "Cell(0,0)", "hello");

        Assert.True(File.Exists(nested));
        Directory.Delete(Path.GetDirectoryName(nested)!, recursive: true);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
