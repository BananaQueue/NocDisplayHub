using NocDisplayHub.Core.Monitoring;

namespace NocDisplayHub.Tests;

public class FrozenContentTrackerTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    [Fact]
    public void Observe_ChangingHash_NeverFreezes()
    {
        var tracker = new FrozenContentTracker(Threshold);
        var start = new DateTime(2026, 1, 1, 0, 0, 0);

        tracker.Observe("hash-a", start);
        tracker.Observe("hash-b", start.AddMinutes(10));
        tracker.Observe("hash-c", start.AddMinutes(20));

        Assert.False(tracker.IsFrozen);
    }

    [Fact]
    public void Observe_SameHashPastThreshold_BecomesFrozen()
    {
        var tracker = new FrozenContentTracker(Threshold);
        var start = new DateTime(2026, 1, 1, 0, 0, 0);

        tracker.Observe("hash-a", start);
        tracker.Observe("hash-a", start.AddMinutes(2));
        Assert.False(tracker.IsFrozen);

        var justChanged = tracker.Observe("hash-a", start.AddMinutes(6));
        Assert.True(tracker.IsFrozen);
        Assert.True(justChanged);
    }

    [Fact]
    public void Observe_HashChangesAfterFreezing_Unfreezes()
    {
        var tracker = new FrozenContentTracker(Threshold);
        var start = new DateTime(2026, 1, 1, 0, 0, 0);

        tracker.Observe("hash-a", start);
        tracker.Observe("hash-a", start.AddMinutes(6));
        Assert.True(tracker.IsFrozen);

        var justChanged = tracker.Observe("hash-b", start.AddMinutes(7));

        Assert.False(tracker.IsFrozen);
        Assert.True(justChanged);
    }

    [Fact]
    public void Observe_WhileAlreadyFrozenWithSameHash_DoesNotReportChangeAgain()
    {
        var tracker = new FrozenContentTracker(Threshold);
        var start = new DateTime(2026, 1, 1, 0, 0, 0);

        tracker.Observe("hash-a", start);
        tracker.Observe("hash-a", start.AddMinutes(6));
        Assert.True(tracker.IsFrozen);

        var justChanged = tracker.Observe("hash-a", start.AddMinutes(10));

        Assert.False(justChanged);
        Assert.True(tracker.IsFrozen);
    }
}
