using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The two readings a wiring pin leans on when it has to say more than "the text is
/// there": the code without its comments, and how deeply a statement is nested. A
/// bug in either makes the pins built on them go green on the thing they refuse, so
/// they are tested on their own. Backticks stand in for double quotes below, so the
/// scanned code reads as the C# it is.
/// </summary>
public class RepoSourcesTests
{
    private static string Cs(string withBackticks) => withBackticks.Replace('`', '"');

    [Fact]
    public void CommentsGoAndTheCodeAndItsLineBreaksStay()
    {
        var code = "a(); // gone\nb(); /* gone\nstill gone */ c();\n";
        Assert.Equal("a(); \nb(); \n c();\n", RepoSources.WithoutComments(code));
    }

    [Fact]
    public void ACommentedOutCallIsNotCode()
        // The shape that a raw-text pin cannot tell from the real thing.
        => Assert.DoesNotContain("SourceBox.Text = own;",
            RepoSources.WithoutComments("x();\n// SourceBox.Text = own;\n/* SourceBox.Text = own; */\n"),
            System.StringComparison.Ordinal);

    [Fact]
    public void CommentMarkersInsideLiteralsAreText()
    {
        // A URL, a verbatim string with a doubled quote before its `//`, a character
        // literal, and an escaped quote before a `//` in a regular string: all code.
        var kept = Cs("var u = `https://x`; var v = @`a /* b */ ``//```; var c = '/'; var e = `\\`//`; ");
        Assert.Equal(kept, RepoSources.WithoutComments(kept + "// gone"));
    }

    [Fact]
    public void BraceDepthCountsTheCodesBracesOnly()
    {
        var code = Cs("void M() { if (x) { var s = `{`; var c = '}'; var i = $`{{`; HERE } THERE }");
        Assert.Equal(0, RepoSources.BraceDepth(code, 0));
        Assert.Equal(2, RepoSources.BraceDepth(code, code.IndexOf("HERE", System.StringComparison.Ordinal)));
        Assert.Equal(1, RepoSources.BraceDepth(code, code.IndexOf("THERE", System.StringComparison.Ordinal)));
    }
}
