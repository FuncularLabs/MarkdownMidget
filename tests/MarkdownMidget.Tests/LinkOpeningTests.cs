using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Which links open outside the app, and the address the prompt shows (<see cref="LinkOpening"/>).</summary>
public class LinkOpeningTests
{
    [Theory][InlineData("https://example.com/a?b=1#c", "https://example.com/a?b=1#c")][InlineData("HTTP://Example.com", "http://example.com/")][InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
    [InlineData("https://bücher.example/café", "https://xn--bcher-kva.example/caf%C3%A9")]   // an IDN host is shown in punycode
    public void WebAndMailLinksAreAccepted(string url, string shown) =>
        Assert.Equal((true, shown, new Uri(url).AbsoluteUri), (LinkOpening.TryValidate(url, out var uri, out var display), display, uri?.AbsoluteUri));

    [Theory][InlineData(null)][InlineData("")][InlineData("file:///C:/Windows/win.ini")][InlineData("javascript:alert(1)")][InlineData("data:text/html,x")][InlineData("ms-settings:privacy")]
    [InlineData("mdm-custom://open")][InlineData("other.md")][InlineData("../docs/other.md")][InlineData(@"\\server\share\x.md")][InlineData(@"C:\x.md")][InlineData("mailto:")]
    [InlineData("https://example.com/a b")][InlineData(" https://example.com/")][InlineData("https://exa\tmple.com/")][InlineData("https://example.com/\u0007")]
    [InlineData("https://example.com/\u202Egpj.exe")][InlineData("https://")][InlineData("https://[bad")][InlineData(@"http:\\example.com")]
    public void EverythingElseIsRefused(string? url) => Assert.False(LinkOpening.TryValidate(url, out _, out _));

    [Fact]
    public void AnOverlongUrlIsRefusedAndALongOneIsShownCut()
    {
        Assert.False(LinkOpening.TryValidate("https://example.com/" + new string('a', LinkOpening.MaxLength), out _, out _));
        Assert.True(LinkOpening.TryValidate("https://example.com/" + new string('a', 1000), out _, out var shown));
        Assert.Equal((LinkOpening.MaxShown, '…'), (shown.Length, shown[^1]));
    }
}
