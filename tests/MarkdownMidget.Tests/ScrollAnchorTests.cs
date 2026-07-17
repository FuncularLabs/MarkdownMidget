using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The auto-reload feature exists for documents that get rewritten under the reader
/// (an AI tool regenerating a file). These tests model that: capture a position in
/// one version, then resolve it against a rewritten version.
/// </summary>
public class ScrollAnchorTests
{
    private const string V1 = """
        # Report

        Intro paragraph.

        ## Findings

        The first finding is here.
        The second finding is here.

        ## Next Steps

        Do the thing.
        """;

    // Same topics, prose rewritten and a section grown — line numbers all shift.
    private const string V2 = """
        # Report

        A completely rewritten intro that is now
        several lines long instead of one.

        ## Findings

        Entirely different finding text now.

        ## Next Steps

        Do the thing.
        """;

    private static int LineOf(string doc, string needle)
    {
        var lines = doc.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++) if (lines[i].Contains(needle)) return i;
        return -1;
    }

    [Fact]
    public void Capture_UsesNearestHeadingAbove()
    {
        var a = ScrollAnchor.Capture(V1, LineOf(V1, "The first finding"), 0.5);
        Assert.Equal("findings", a.Heading);
        Assert.Equal("the first finding is here.", a.Fingerprint);
    }

    [Fact]
    public void Resolve_LandsOnSameTopic_WhenProseRewritten()
    {
        // Reader was under "## Findings" in V1; that exact line is gone in V2.
        var a = ScrollAnchor.Capture(V1, LineOf(V1, "The first finding"), 0.5);
        var line = ScrollAnchor.ResolveLine(V2, a);
        Assert.Equal(LineOf(V2, "## Findings"), line);   // same topic, not same line number
    }

    [Fact]
    public void Resolve_PrefersExactLine_WhenItSurvived()
    {
        // "Do the thing." survives under "## Next Steps" — land exactly there, not on the heading.
        var a = ScrollAnchor.Capture(V1, LineOf(V1, "Do the thing."), 0.9);
        var line = ScrollAnchor.ResolveLine(V2, a);
        Assert.Equal(LineOf(V2, "Do the thing."), line);
    }

    [Fact]
    public void Resolve_IgnoresIdenticalLineInAnotherTopic()
    {
        // The same sentence appears under two headings; the anchor must stay in its own.
        const string dup = """
            ## Alpha

            shared sentence.

            ## Beta

            shared sentence.
            """;
        var a = ScrollAnchor.Capture(dup, LineOf(dup, "## Beta") + 2, 0.8);
        Assert.Equal("beta", a.Heading);
        var line = ScrollAnchor.ResolveLine(dup, a);
        Assert.True(line > LineOf(dup, "## Beta"));   // resolved inside Beta, not Alpha
    }

    [Fact]
    public void Resolve_DisambiguatesRepeatedHeadingsByOrdinal()
    {
        const string repeated = """
            ## Notes

            first notes body.

            ## Notes

            second notes body.
            """;
        var a = ScrollAnchor.Capture(repeated, LineOf(repeated, "second notes body."), 0.9);
        Assert.Equal("notes", a.Heading);
        Assert.Equal(1, a.HeadingOrdinal);            // the SECOND "Notes"
        Assert.Equal(LineOf(repeated, "second notes body."), ScrollAnchor.ResolveLine(repeated, a));
    }

    [Fact]
    public void Resolve_FallsBackToRatio_WhenNothingSurvives()
    {
        var a = ScrollAnchor.Capture(V1, LineOf(V1, "The first finding"), 0.5);
        const string unrecognizable = "totally\ndifferent\ncontent\nwith\nno\nheadings\nat\nall\nhere\nnow";
        var line = ScrollAnchor.ResolveLine(unrecognizable, a);
        Assert.InRange(line, 0, 9);                   // approximate, but not lost
    }

    [Fact]
    public void Resolve_MatchesHeadingDespiteWhitespaceAndCaseChange()
    {
        var a = ScrollAnchor.Capture(V1, LineOf(V1, "The first finding"), 0.5);
        const string reflowed = "# Report\n\n##   FINDINGS\n\nnew body text.\n";
        Assert.Equal(2, ScrollAnchor.ResolveLine(reflowed, a));
    }

    [Fact]
    public void Resolve_ReturnsMinusOne_ForEmptyDocument()
    {
        var a = ScrollAnchor.Capture(V1, 5, 0.5);
        Assert.Equal(-1, ScrollAnchor.ResolveLine("", a));
    }

    [Fact]
    public void Capture_HandlesDocumentWithNoHeadings()
    {
        const string plain = "just one line\nand another\n";
        var a = ScrollAnchor.Capture(plain, 1, 0.5);
        Assert.Null(a.Heading);
        Assert.Equal("and another", a.Fingerprint);
        Assert.Equal(1, ScrollAnchor.ResolveLine(plain, a));   // exact line still found
    }
}
