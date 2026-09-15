using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Which links open outside the app, and the address the prompt shows (<see cref="LinkOpening"/>).</summary>
public class LinkOpeningTests
{
    [Theory][InlineData("https://example.com/a?b=1#c", "https://example.com/a?b=1#c")][InlineData("HTTP://Example.com", "http://example.com/")]
    [InlineData("https://bücher.example/café", "https://xn--bcher-kva.example/caf%C3%A9")]   // an IDN host is shown, and opened, in punycode
    [InlineData("http://localhost:8080/x", "http://localhost:8080/x")][InlineData("http://127.0.0.1/", "http://127.0.0.1/")][InlineData("http://[::1]/", "http://[::1]/")]
    [InlineData("http://my_server/?q=1#f", "http://my_server/?q=1#f")][InlineData("http://2130706433/", "http://127.0.0.1/")]
    [InlineData("https://a1.example.com/", "https://a1.example.com/")][InlineData("https://example.com./", "https://example.com./")]
    public void WebLinksAreAccepted(string url, string shown) =>   // what the prompt shows is exactly what opens
        Assert.Equal((true, shown, shown), (LinkOpening.TryValidate(url, out var uri, out var display), display, uri?.AbsoluteUri));

    [Theory][InlineData(null)][InlineData("")][InlineData("file:///C:/Windows/win.ini")][InlineData("javascript:alert(1)")][InlineData("data:text/html,x")][InlineData("ms-settings:privacy")]
    [InlineData("mdm-custom://open")][InlineData("other.md")][InlineData("../docs/other.md")][InlineData(@"\\server\share\x.md")][InlineData(@"C:\x.md")][InlineData("mailto:")]
    [InlineData("https://example.com/a b")][InlineData(" https://example.com/")][InlineData("https://exa\tmple.com/")][InlineData("https://example.com/\u0007")]
    [InlineData("https://example.com/\u202Egpj.exe")][InlineData("https://")][InlineData("https://[bad")][InlineData(@"http:\\example.com")][InlineData("https://good.com@evil.com/")]
    [InlineData("https://a..test/")]
    // Email links are copy-only: plain, with a harmless query, and carrying the attacks that made them so.
    [InlineData("mailto:a@b.com")][InlineData("mailto:a@b.com?subject=Hi%20there&body=Line%0D%0Atwo&CC=c@d.com&bcc=e@f.com")][InlineData("mailto:a@b.com?attach=C:/Users/x/secret.txt")]
    [InlineData("mailto:a@b.com?%61ttach=file:///C:/secret")][InlineData("mailto:a@b.com?to=x@evil.com")][InlineData("mailto:a@b.com?subject=hi%26attach%3DC:/secret")]
    [InlineData("mailto:a@b.com?subject=x#&attach=C:/secret")][InlineData("mailto:a@b.com#?attach=C:/secret")][InlineData("mailto:a@b.com?subject=x#?attach=C:/secret")]
    public void EverythingElseIsRefused(string? url) => Assert.False(LinkOpening.TryValidate(url, out _, out _));

    [Theory][InlineData("https://good.com.{0}@evil.com/")][InlineData("mailto:a@b.com?subject={0}&bcc=attacker@evil.com&attach=C:/secret")]
    public void PaddingCannotHideAHostOrAnAttachment(string probe) => Assert.False(LinkOpening.TryValidate(string.Format(probe, new string('a', 290)), out _, out _));

    private static string Host(string probe, string part, int count) => string.Format(probe, string.Concat(Enumerable.Repeat(part, count)));

    // Refused, never thrown: hosts IDN rejects (a label ending in a hyphen, a name too long once in punycode), hosts that are neither a DNS name nor
    // an IP address (a leading hyphen, an empty or over-long label), and names whose punycode reads as something else (0127.0.0.1, a space).
    [Theory][InlineData("https://ехample-.test/", "", 0)][InlineData("https://ü-.test/", "", 0)][InlineData("https://{0}.{0}.{0}.{0}.{0}.test/", "büaaaaaaaaaa", 4)]
    [InlineData("http://\uFF10127.0.0.1/", "", 0)][InlineData("http://\uFF1010.0.0.1:8080/", "", 0)][InlineData("https://\u1FC0-a.test/", "", 0)]
    [InlineData("https://-ехample.test/", "", 0)][InlineData("https://-example.test/", "", 0)][InlineData("https://a.-ех.test/", "", 0)][InlineData("https://ü..test/", "", 0)]
    [InlineData("https://{0}.test/", "a", 64)][InlineData("https://{0}.test/", "ü", 60)]
    [InlineData("http://0127.0.0.1./", "", 0)][InlineData("http://010.0.0.1./", "", 0)][InlineData("http://0x/", "", 0)][InlineData("http://0177.0x.0.1/", "", 0)]   // a browser reads as IPv4
    public void HostsThatCannotShowInPunycodeAreRefused(string probe, string part, int count) => Assert.False(LinkOpening.TryValidate(Host(probe, part, count), out _, out _));

    [Fact]
    public void AnOverlongUrlIsRefusedAndALongOneIsShownWhole()
    {
        Assert.False(LinkOpening.TryValidate("https://example.com/" + new string('a', LinkOpening.MaxLength), out _, out _));
        Assert.True(LinkOpening.TryValidate("https://example.com/" + new string('a', 1000), out _, out var shown));
        Assert.Equal(1020, shown.Length);
    }
}
