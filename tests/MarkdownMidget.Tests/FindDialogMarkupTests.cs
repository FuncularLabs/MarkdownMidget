using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// What the Find dialog's markup declares, read as text. Nothing here shows a window
/// — the two things worth checking cannot be seen from one anyway: that no NEW
/// control quietly shares an Alt access key with another, and that the tooltip
/// strings are written down once rather than twice (#5 F-12, F-15).
///
/// The files are copied next to the test assembly by the csproj, under a .txt
/// extension so the test SDK leaves them alone.
/// </summary>
public class FindDialogMarkupTests
{
    private static string Xaml() => Read("FindDialog.xaml.txt");
    private static string CodeBehind() => Read("FindDialog.xaml.cs.txt");

    private static string Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path), $"{name} must be copied to the test output");
        return File.ReadAllText(path);
    }

    /// <summary>Every access key the markup declares, in document order: the character
    /// after a single underscore in a Content attribute. WPF writes a literal
    /// underscore as "__", so those are removed first.</summary>
    private static List<string> AccessKeys() =>
        Regex.Matches(Xaml(), "Content=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value.Replace("__", ""))
            .Select(content => Regex.Match(content, "_(?<c>[A-Za-z0-9])"))
            .Where(k => k.Success)
            .Select(k => k.Groups["c"].Value.ToUpperInvariant())
            .ToList();

    [Fact]
    public void EveryControlWithAnAccessKeyIsFound()
    {
        // If the scan stops finding them — a rename, a restyle, Content moved into a
        // setter — the two tests below would pass over nothing.
        Assert.True(AccessKeys().Count >= 14, "the access-key scan found almost nothing");
    }

    [Theory]
    [InlineData("L")]   // Replace with
    [InlineData("R")]   // Replace
    [InlineData("A")]   // Replace All
    public void TheAccessKeysReplaceAddedAreEachUsedOnce(string key)
        => Assert.Equal(1, AccessKeys().Count(k => k == key));

    [Fact]
    public void TheOnlySharedAccessKeysAreTheOnesThatWereAlreadyShared()
    {
        // W three times (Wildcards, whole word, Wrap around) and N twice (Normal,
        // Find Next) predate Replace. A NEW collision should fail here rather than be
        // found by someone pressing Alt and watching the wrong control take it.
        var shared = AccessKeys().GroupBy(k => k).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(new Dictionary<string, int> { ["W"] = 3, ["N"] = 2 }, shared);
    }

    [Fact]
    public void TheAccessKeyCommentSaysWhatTheMarkupActuallyDeclares()
    {
        // It said "(W three times, as before)" while N appeared twice as well — a
        // comment that reads as a complete account of the duplicates and is not (#5 F-12).
        var m = Regex.Match(Xaml(), @"Access keys in use on this window:(?<list>[^(]*)\((?<note>[^)]*)\)",
            RegexOptions.Singleline);
        Assert.True(m.Success, "the access-key comment is gone, or no longer names its duplicates");

        var listed = Regex.Matches(m.Groups["list"].Value, "(?<![A-Za-z])([A-Z])(?![A-Za-z])")
            .Select(x => x.Value).ToHashSet();
        Assert.Equal(AccessKeys().ToHashSet(), listed);
        Assert.Contains("W three times", m.Groups["note"].Value);
        Assert.Contains("N twice", m.Groups["note"].Value);
    }

    [Fact]
    public void TheReplaceTooltipsAreWrittenDownOnce()
    {
        // They sat in the markup AND in the code-behind consts: two places to change,
        // one to forget, and nothing to tell you they had drifted (#5 F-15).
        foreach (var tip in new[] { "The current match, then find the next", "as one undo step" })
        {
            Assert.DoesNotContain(tip, Xaml());
            Assert.Equal(1, Occurrences(CodeBehind(), tip));
        }
        // SetReadOnly is the one place that puts them on the buttons, and the
        // constructor calls it so they are there before the host ever does.
        Assert.Contains("SetReadOnly(false);", CodeBehind());
        Assert.Equal(1, Occurrences(CodeBehind(), "ReplaceButton.ToolTip = readOnly"));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
