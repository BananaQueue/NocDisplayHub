using NocDisplayHub.Core.Bindings;

namespace NocDisplayHub.Tests;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("www.google.com", "https://www.google.com")]
    [InlineData("google.com", "https://google.com")]
    [InlineData("  google.com  ", "https://google.com")]
    public void NormalizeBrowserUrl_BareDomain_PrependsHttps(string input, string expected)
    {
        Assert.Equal(expected, UrlNormalizer.NormalizeBrowserUrl(input));
    }

    [Theory]
    [InlineData("https://google.com")]
    [InlineData("http://google.com")]
    [InlineData("file:///C:/dashboard.html")]
    public void NormalizeBrowserUrl_AlreadyHasScheme_LeavesUnchanged(string input)
    {
        Assert.Equal(input, UrlNormalizer.NormalizeBrowserUrl(input));
    }
}
