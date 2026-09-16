namespace NocDisplayHub.Core.Config;

/// <summary>
/// Scheduled refresh for browser cells that otherwise go stale silently.
/// The spec deliberately leaves global-vs-per-cell scoping open until
/// staleness is observed in practice — this is a single global interval for
/// v1, not a design commitment either way.
/// </summary>
public static class RefreshSettings
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
}
