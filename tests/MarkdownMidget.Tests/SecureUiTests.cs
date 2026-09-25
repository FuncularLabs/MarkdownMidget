using System.Collections.Generic;
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
    public void OnlyTheOpenFiltersMarkdownTypeIsOptIn()
    {
        // The Markdown type is EXACTLY what it always was unless the user opts in;
        // Secure Markdown is a type of its own either way (PickerParityTests).
        Assert.Equal("*.md;*.markdown", SecureUi.OpenFilter(includeEncrypted: false).Split('|')[1]);
        Assert.Equal("*.md;*.markdown;*.mdenc", SecureUi.OpenFilter(includeEncrypted: true).Split('|')[1]);
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

    // ---- what a save to a picked name asks first: Save As and "Save your current version as…" ----

    private static SecureUi.SaveChoice Choose(string path, bool docEncrypted, List<string> asked,
        string? typedPassword = "new", bool continueReadable = true) =>
        SecureUi.ChooseSave(path, docEncrypted, docEncrypted ? "old" : null,
            () => { asked.Add("password"); return typedPassword; },
            () => { asked.Add("warning"); return continueReadable; });

    [Theory]
    [InlineData(false, @"C:\d\x.mdenc", "password", true, "new")]   // readable document, .mdenc name: new password
    [InlineData(false, @"C:\d\x.md", "", false, null)]              // readable, any other name: nothing
    [InlineData(true, @"C:\d\x.md", "warning", false, null)]        // encrypted, any other name: the warning
    [InlineData(true, @"C:\d\x.MDENC", "", true, "old")]            // encrypted, .mdenc name: nothing
    public void TheNameDecidesWhatASaveAsksAndThePasswordTheWindowKeeps(
        bool docEncrypted, string path, string expectedAsk, bool encrypt, string? password)
    {
        var asked = new List<string>();
        var choice = Choose(path, docEncrypted, asked);
        Assert.Equal(expectedAsk.Length == 0 ? [] : [expectedAsk], asked);
        Assert.True(choice.Write);
        Assert.Equal(encrypt, choice.Encrypt);
        // What the file is sealed with, and what Save As and "keep editing your saved
        // version" both leave the window with (null: a readable document).
        Assert.Equal(password, choice.Password);
    }

    [Fact]
    public void BackingOutOfThePasswordOrTheWarningWritesNothing()
    {
        var asked = new List<string>();
        Assert.False(Choose(@"C:\d\x.mdenc", docEncrypted: false, asked, typedPassword: null).Write);
        Assert.False(Choose(@"C:\d\x.md", docEncrypted: true, asked, continueReadable: false).Write);
        Assert.Equal(["password", "warning"], asked);
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
