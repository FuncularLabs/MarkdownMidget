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

    [Theory]
    // A verbatim string ending in a backslash. The backslash is not an escape there, so
    // the literal ends at the quote after it and the comment that follows goes. Read as
    // a regular literal, the backslash escapes that quote and the literal swallows the
    // rest of the line, comment and all.
    [InlineData("var p = @`C:\\`; ")]
    // And one whose content is a doubled quote (one quote) then a backslash: it ends at
    // the LAST quote, not the first of the pair.
    [InlineData("var q = @```\\`; ")]
    public void VerbatimStringsEndWhereCSharpEndsThem(string code)
    {
        var kept = Cs(code);
        Assert.Equal(kept, RepoSources.WithoutComments(kept + "// gone"));
    }

    // ===== "and it runs every time": the reading the wiring pins share =====

    private const string Wired = "Wired(x);";

    /// <summary>The statement's verdict inside a method built from these body lines.</summary>
    private static string? Why(params string[] lines) => RepoSources.WhyNotUnconditional(
        "private void M(bool on)\n    {\n        " + string.Join("\n        ", lines), Wired);

    [Fact]
    public void AStatementAtTheMethodsOwnLevelRunsEveryTime()
        => Assert.Null(Why("a();", "if (on) { b(); }", Wired, "c();"));

    [Fact]
    public void AStatementThatIsOnlyInACommentIsRefused()
        => Assert.Contains("is not in the method", Why("a();", "// " + Wired)!, System.StringComparison.Ordinal);

    [Fact]
    public void AStatementThatAppearsTwiceIsRefused()
        => Assert.Contains("more than once", Why(Wired, "a();", Wired)!, System.StringComparison.Ordinal);

    [Fact]
    public void AStatementInsideABlockIsRefusedAndSaysWhatToDo()
    {
        // The message has to separate the two cases: a block that always runs is moved
        // out of, a conditional one is the thing this refuses.
        var why = Why("try", "{", "    " + Wired, "}", "finally { }")!;
        Assert.Contains("brace depth 2", why, System.StringComparison.Ordinal);
        Assert.Contains("always runs", why, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("if (on)")]
    [InlineData("else")]
    [InlineData("while (on)")]
    [InlineData("for (;;)")]
    [InlineData("foreach (var i in xs)")]
    [InlineData("using (d)")]
    [InlineData("lock (l)")]
    public void ABracelessOwnerOnTheLineAboveIsRefused(string owner)
        => Assert.Contains("does not end in", Why("a();", owner, Wired)!, System.StringComparison.Ordinal);

    [Fact]
    public void ABracelessOwnerOnTheSameLineIsRefused()
        => Assert.Contains("not first on its line", Why("if (on) " + Wired)!, System.StringComparison.Ordinal);

    [Theory]
    [InlineData("return;")]
    [InlineData("goto skip;")]
    [InlineData("break;")]
    [InlineData("continue;")]
    [InlineData("throw new System.Exception();")]
    public void AWayOutBeforeTheStatementIsRefused(string jump)
        => Assert.Contains("before", Why("if (on) { " + jump + " }", Wired)!, System.StringComparison.Ordinal);

    [Fact]
    public void AReturnInALambdaIsRefusedLoudlyRatherThanGuessedAt()
    {
        // It skips nothing, and this reading cannot tell it from one that does, so it
        // says which shape it cannot rule out instead of passing on a guess.
        var why = Why("System.Func<int> f = () => { return 1; };", Wired)!;
        Assert.Contains("lambda", why, System.StringComparison.Ordinal);
    }

    [Fact]
    public void APreprocessorDirectiveInTheMethodIsRefused()
        => Assert.Contains("preprocessor", Why("#if NEVER", Wired, "#endif", Wired)!, System.StringComparison.Ordinal);

    [Fact]
    public void WaysOutAndHashesInsideLiteralsAreText()
    {
        // A string that mentions return, or a verbatim one holding a markdown heading,
        // is not a way out and not a directive.
        var why = Why(Cs("var s = `return; goto x; break; continue; throw`;"),
                      Cs("var md = @`"), Cs("# Heading`;"), Wired);
        Assert.Null(why);
    }

    [Fact]
    public void ARawStringInTheMethodIsRefused()
    {
        // A raw string reads as an empty literal and then another one, and with an odd
        // number of quotes that one never closes: everything after it is blanked, an
        // early return included, and the pins of that method all go quiet. Not
        // understood, so refused out loud rather than read wrong.
        Assert.Contains("raw string", Why(Cs("var probe = ```a ` b```;"), "if (on) return;", Wired)!,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteInsideAnInterpolationHoleIsRefused()
    {
        // The hole holds a literal of its own, which ends the outer one as this reading
        // walks it — the raw string's problem in a shape that compiles every day. Also
        // refused rather than guessed at.
        Assert.Contains("interpolation", Why(Cs("var s = $`{(on ? `a` : `b`)}`;"), "if (on) return;", Wired)!,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void AStatementCopiedIntoAStringIsNotTheStatement()
    {
        // The call deleted and its text left inside a verbatim string, whose first line
        // ends in `;` so the line-above rule is happy. A copy in a comment was already
        // refused; a copy in a literal was taken for the call itself.
        Assert.Contains("is not in the method", Why(Cs("var s = @`x;"), Cs("Wired(x);`;"), "a();")!,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void AStatementThatAlsoAppearsInAStringIsStillFoundInTheCode()
    {
        // And the other way round: a message that quotes the statement is not a second
        // copy of it, so the real one is still the one.
        Assert.Null(Why(Cs("Log(`Wired(x);`);"), Wired, "a();"));
    }

    [Fact]
    public void AfterAnAnchorOnlyWhatFollowsItHasToBeUnconditional()
    {
        // For a method with early returns of its own: "once the anchor has run, this
        // runs too". A way out BEFORE the anchor is the method's business.
        const string anchor = "Anchored();";
        string? Ask(params string[] lines) => RepoSources.WhyNotUnconditional(
            "private void M(bool on)\n    {\n        " + string.Join("\n        ", lines), Wired, anchor);

        Assert.Null(Ask("if (on) return;", anchor, "b();", Wired));
        Assert.Contains("before", Ask(anchor, "if (on) return;", Wired)!, System.StringComparison.Ordinal);
        Assert.Contains("after", Ask(Wired, anchor)!, System.StringComparison.Ordinal);
        Assert.Contains("is not in the method", Ask("if (on) return;", Wired)!, System.StringComparison.Ordinal);
    }
}
