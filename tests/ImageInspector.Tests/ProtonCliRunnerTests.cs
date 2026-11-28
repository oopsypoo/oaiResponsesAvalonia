using oaiResponsesAvalonia.Services;

namespace ImageInspector.Tests;

public sealed class ProtonCliRunnerTests
{
    [Theory]
    [InlineData("Complete login at https://account.proton.me/drive?token=abc", "https://account.proton.me/drive?token=abc")]
    [InlineData("Open https://example.com/auth).", "https://example.com/auth")]
    [InlineData("No authentication link was emitted", null)]
    [InlineData("Not a web URL: ftp://example.com", null)]
    public void ExtractFirstHttpUrlFindsOnlyHttpOrHttpsLinks(string output, string? expected)
    {
        Assert.Equal(expected, ProtonCliRunner.ExtractFirstHttpUrl(output));
    }
}
