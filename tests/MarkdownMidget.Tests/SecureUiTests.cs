using MarkdownMidget.Secure;
using Xunit;

namespace MarkdownMidget.Tests;

public class SecureUiTests
{
    [Theory]
    [InlineData(@"C:\docs\a.mdenc", true)]
    [InlineData(@"C:\docs\a.MDENC", true)]
    [InlineData(@"C:\docs\a.md", false)]
    [InlineData(@"C:\docs\a.markdown", false)]
    [InlineData(@"C:\docs\a.mdenc.md", false)]     // extension is the LAST one
    [InlineData(null, false)]
    public void EncryptedPathDetection(string? path, bool expected) =>
        Assert.Equal(expected, SecureUi.IsEncryptedPath(path));

    [Fact]
    public void PathConversionsRoundTrip()
    {
        Assert.Equal(@"C:\docs\a.mdenc", SecureUi.EncryptedPathFor(@"C:\docs\a.md"));
        Assert.Equal(@"C:\docs\a.md", SecureUi.PlaintextPathFor(@"C:\docs\a.mdenc"));
        // A file with dots in its stem keeps the stem.
        Assert.Equal(@"C:\docs\notes.v2.mdenc", SecureUi.EncryptedPathFor(@"C:\docs\notes.v2.md"));
    }

    [Fact]
    public void TheOpenFilterIsOptIn()
    {
        // The default filter is EXACTLY what it always was — the opt-in only adds.
        Assert.DoesNotContain("mdenc", SecureUi.OpenFilter(includeEncrypted: false));
        Assert.Contains("*.mdenc", SecureUi.OpenFilter(includeEncrypted: true));
        // Both keep the escape hatch.
        Assert.Contains("All files (*.*)", SecureUi.OpenFilter(false));
        Assert.Contains("All files (*.*)", SecureUi.OpenFilter(true));
    }

    [Fact]
    public void TheSaveFilterIndexPointsAtSecureMarkdown()
    {
        // FilterIndex is 1-based; if the filter string is ever reordered this
        // catches the constant going stale.
        var groups = SecureUi.SaveFilter.Split('|');
        Assert.Contains("mdenc", groups[(SecureUi.SaveFilterEncryptedIndex - 1) * 2]);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("P@ss1", "weak")]
    [InlineData("password10", "fair")]
    [InlineData("correct horse battery staple", "strong")]
    [InlineData("Sh0rt!yes8", "fair")]
    [InlineData("A1!aA1!aA1!a", "strong")]   // 12 chars, 4 classes
    public void StrengthBandsAreSane(string password, string expectedFragment)
    {
        var result = SecureUi.DescribeStrength(password);
        if (expectedFragment.Length == 0) Assert.Equal("", result);
        else Assert.Contains(expectedFragment, result);
    }

    [Fact]
    public void LengthBeatsCharacterSoup()
    {
        // The design's stated rule: a long passphrase must never score below a
        // short symbol-heavy password.
        Assert.Contains("strong", SecureUi.DescribeStrength("correct horse battery staple"));
        Assert.Contains("weak", SecureUi.DescribeStrength("P@s5!"));
    }
}
