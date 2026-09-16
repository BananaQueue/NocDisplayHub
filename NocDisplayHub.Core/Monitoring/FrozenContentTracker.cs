namespace NocDisplayHub.Core.Monitoring;

/// <summary>
/// Flags a cell as frozen once its content hash has been unchanged for
/// longer than the threshold. Pure state machine — the caller supplies
/// whatever content hash it captured (e.g. a WebView2 screenshot hash) and
/// the current time; this class only tracks how long that hash has been
/// stable. A basic heuristic per the spec ("basic frozen-content detection"),
/// not a guarantee — a legitimately static dashboard will also trip it.
/// </summary>
public sealed class FrozenContentTracker
{
    private readonly TimeSpan _threshold;
    private string? _lastHash;
    private DateTime _unchangedSince;

    public FrozenContentTracker(TimeSpan threshold)
    {
        _threshold = threshold;
    }

    public bool IsFrozen { get; private set; }

    /// <summary>Returns true if this observation just changed IsFrozen (either direction) — a good signal to log/update the UI.</summary>
    public bool Observe(string hash, DateTime now)
    {
        if (hash != _lastHash)
        {
            _lastHash = hash;
            _unchangedSince = now;
            var wasFrozen = IsFrozen;
            IsFrozen = false;
            return wasFrozen;
        }

        if (!IsFrozen && now - _unchangedSince >= _threshold)
        {
            IsFrozen = true;
            return true;
        }

        return false;
    }
}
