using PlanViewer.App;

namespace PlanViewer.Core.Tests;

/// <summary>
/// The About window opens links through the shell, and one of them, the update address,
/// comes from the release server's reply. Only absolute http and https addresses may be
/// opened; the shell runs a program when it is handed a path or another scheme.
/// </summary>
public class AboutWindowLinkTests
{
    [Theory]
    [InlineData("https://github.com/erikdarlingdata/PerformanceStudio/releases/tag/v1.27.0")]
    [InlineData("http://example.com/")]
    [InlineData("HTTPS://GITHUB.COM/erikdarlingdata")]
    public void WebAddressesAreOpened(string url)
    {
        Assert.NotNull(AboutWindow.WebPageAddress(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData(@"\\server\share\setup.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("releases/latest")]
    public void AnythingElseIsNot(string? url)
    {
        Assert.Null(AboutWindow.WebPageAddress(url));
    }

    [Fact]
    public void TheAddressHandedToTheShellIsEscaped()
    {
        var page = AboutWindow.WebPageAddress("https://example.com/a b\"c");

        Assert.Equal("https://example.com/a%20b%22c", page!.AbsoluteUri);
    }
}
