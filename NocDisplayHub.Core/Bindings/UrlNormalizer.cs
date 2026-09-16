namespace NocDisplayHub.Core.Bindings;

/// <summary>
/// Normalizes a browser binding's value the way an address bar would. Typing
/// a bare domain like "google.com" (no scheme) is the natural thing for a
/// user to do, but WebView2's Uri constructor requires an explicit scheme or
/// it throws — confirmed live: that exception crashed the whole wall, not
/// just its own cell, since nothing downstream caught it. Normalizing at
/// entry means the bad value never reaches the compositor in the first place.
/// </summary>
public static class UrlNormalizer
{
    public static string NormalizeBrowserUrl(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : $"https://{trimmed}";
    }
}
