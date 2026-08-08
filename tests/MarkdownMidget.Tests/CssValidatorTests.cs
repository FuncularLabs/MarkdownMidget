using System;
using System.IO;
using MarkdownMidget.Themes;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// What a theme file has to be before it is allowed anywhere near the editor.
///
/// Two rules run through all of this. Anything wrong must name a line and say what
/// is wrong in a sentence, because the only place the answer surfaces is a tooltip
/// on a greyed-out menu entry — "Invalid CSS" on its own leaves someone staring at
/// a 200-line file. And anything that would reach the network is refused outright
/// rather than stripped, so its author finds out.
/// </summary>
public class CssValidatorTests
{
    private static CssProblem Reject(string css)
    {
        var problem = CssValidator.Validate(css);
        Assert.True(problem.HasValue, "expected this to be rejected, but it passed");
        return problem!.Value;
    }

    private static void Accept(string? css)
        => Assert.Null(CssValidator.Validate(css));

    [Fact]
    public void ThePaletteWeShipPasses()
    {
        // The only test here with real input, and the one that would have caught a
        // validator too strict to be usable: if our own default palette does not
        // pass, the feature is broken before anyone writes a theme of their own.
        // The file is linked from editor-src by the csproj rather than copied, so
        // this is the palette that actually ships.
        var path = Path.Combine(AppContext.BaseDirectory, "theme-default.css");
        Assert.True(File.Exists(path), $"expected the linked palette at {path}");

        var problem = CssValidator.Validate(File.ReadAllText(path));
        Assert.True(problem is null, $"the shipped palette was rejected: {problem}");
    }

    // ---- what a theme normally looks like ----

    [Fact]
    public void TheDefaultThemeShapeIsAccepted()
        => Accept("""
        /* A theme is a palette and nothing else. */
        :root {
          --mdm-page-bg: #ffffff;
          --mdm-text: #1a1a1a;
          --mdm-heading: #4682b4;
        }
        """);

    [Fact]
    public void AnEmptyFileIsNotAnError()
    {
        // A theme that sets nothing renders the default. That is a pointless theme,
        // not a broken one, and refusing it would mean explaining a distinction the
        // user has no reason to care about.
        Accept("");
        Accept("   \n\n  ");
        Accept(null);
    }

    [Fact]
    public void CommentsOnlyIsFine()
        => Accept("/* work in progress */");

    [Fact]
    public void SelectorsNestedBlocksAndAtRulesAreAllFine()
        => Accept("""
        @media (min-width: 40em) {
          .mdm-prosemirror blockquote { background: #f2f7fb; }
        }
        @supports (color: oklch(0 0 0)) {
          :root { --mdm-text: oklch(0.2 0 0); }
        }
        """);

    [Fact]
    public void AnUnknownPropertyIsTheUserBeingClever()
        // Not our business: the browser ignores what it cannot use, and a validator
        // with an opinion about property names goes stale the month it is written.
        => Accept(":root { -webkit-future-thing: 4; --mdm-text: red; }");

    [Fact]
    public void ADeclarationWithoutItsTrailingSemicolonIsFine()
        => Accept(":root { --mdm-text: #111 }");

    // ---- braces ----

    [Fact]
    public void AnUnclosedBlockIsReported()
    {
        var p = Reject(":root {\n  --mdm-text: #111;\n");
        Assert.Contains("never closed", p.Message);
        Assert.Contains("ends inside a block", p.Message);
    }

    [Fact]
    public void SeveralUnclosedBlocksSayHowMany()
    {
        var p = Reject("@media print {\n  :root {\n    --mdm-text: #111;\n");
        Assert.Contains("2 `{` are never closed", p.Message);
    }

    [Fact]
    public void AStrayClosingBraceIsReportedWhereItIs()
    {
        var p = Reject(":root { --mdm-text: #111; }\n}\n");
        Assert.Equal(2, p.Line);
        Assert.Equal(1, p.Column);
        Assert.Contains("never opened", p.Message);
    }

    // ---- comments and strings ----

    [Fact]
    public void AnUnterminatedCommentIsReportedAtItsStart()
    {
        // Reported where the `/*` is, not at the end of the file: everything after
        // it vanished, and the line that matters is the one to go and look at.
        var p = Reject(":root { --mdm-text: #111; }\n/* note\n:root { --mdm-page-bg: #fff; }\n");
        Assert.Equal(2, p.Line);
        Assert.Equal(1, p.Column);
        Assert.Contains("`*/`", p.Message);
    }

    [Fact]
    public void ClosedCommentsDoNotUpsetAnything()
        => Accept("/* a { */ :root { --mdm-text: red; } /* } */");

    [Fact]
    public void BracesInsideCommentsAreNotCounted()
        => Accept(":root { /* } } } */ --mdm-text: red; }");

    [Fact]
    public void AnUnterminatedStringIsReportedAtTheOpeningQuote()
    {
        var p = Reject(":root {\n  --mdm-font: \"Segoe UI;\n  --mdm-text: red;\n}\n");
        Assert.Equal(2, p.Line);
        Assert.Equal(15, p.Column);
        Assert.Contains("never closed", p.Message);
    }

    [Fact]
    public void BracesInsideStringsAreNotCounted()
        // The failure mode this exists for: Tailwind emits `content:'{'`, and a
        // brace counter that reads it as a real brace mismatches everything after.
        => Accept(":root { --mdm-marker: '{'; --mdm-text: red; }");

    [Fact]
    public void AnEscapedQuoteDoesNotEndTheString()
    {
        // An ODD number of quotes once the escaped one is discounted, which is what
        // makes this discriminating. `"say \"hi"` has three: handle the escape and
        // the string closes at the third, everything after it is ordinary CSS, and
        // the theme is valid. Ignore the escape and the first two pair up, the third
        // opens a string that runs to end-of-file, and a good theme is rejected.
        //
        // The obvious version of this test — `"say \"hello\""` — has FOUR quotes,
        // which pair up either way. It passes whether or not escapes are handled and
        // proves nothing; a mutation removing the escape branch survived it.
        Accept(""":root { --a: "say \"hi"; --b: red; }""");
        // And the escape must not swallow a real closing quote: here the backslash
        // escapes a backslash, so the quote after it does close the string.
        Accept(""":root { --a: "back\\"; --b: red; }""");
    }

    [Fact]
    public void BothQuoteStylesWork()
        => Accept(":root { --a: 'x'; --b: \"y\"; }");

    // ---- declarations ----

    [Fact]
    public void ADeclarationWithNoColonIsReportedAtItsStart()
    {
        var p = Reject(":root {\n  --mdm-text #111;\n}\n");
        Assert.Equal(2, p.Line);
        Assert.Equal(3, p.Column);
        Assert.Contains("no `:`", p.Message);
    }

    [Fact]
    public void TheLastDeclarationInABlockIsCheckedToo()
    {
        // No semicolon to trip over, so this one is only caught at the `}` — and it
        // must still be reported where the declaration STARTS. Pointing at the brace
        // that revealed it sends the author to the wrong end of the line.
        var p = Reject(":root { --mdm-text red }");
        Assert.Contains("no `:`", p.Message);
        Assert.Equal(1, p.Line);
        Assert.Equal(9, p.Column);
    }

    [Fact]
    public void ASelectorIsNotADeclaration()
        // The obvious false positive: a selector has no colon either, and is
        // terminated by `{` rather than `;`.
        => Accept(".mdm-prosemirror blockquote { color: red; }");

    [Fact]
    public void AnAtRulePreludeIsNotADeclaration()
        => Accept("@media print { :root { --mdm-text: black; } }");

    [Fact]
    public void ANestedAtRuleWithNoColonIsNotADeclaration()
        => Accept(":root { @media print { --mdm-text: black; } }");

    [Fact]
    public void AnAtRuleStatementInsideABlockIsNotADeclaration()
    {
        // At-rule STATEMENTS end in `;` and have no colon, which is the exact shape
        // of a broken declaration. Both of these are things a theme author could
        // plausibly paste — a nested layer statement, and a Tailwind `@apply` — and
        // rejecting them would be a false accusation about valid CSS.
        Accept(".mdm-prosemirror { @layer overrides; color: red; }");
        Accept(".x { @apply rounded-sm; color: red; }");
    }

    [Fact]
    public void ImportOnlyCountsAsAnAtRuleWhenItStartsOne()
    {
        // A custom property's value is an almost arbitrary token stream, so the text
        // can appear where it is not an at-rule at all. Rejecting that would be
        // refusing a whole theme over a token that fetches nothing.
        Accept(":root { --mdm-fallback: @import; }");
        // ...while the real thing, at the start of a run, is still refused.
        Assert.Contains("@import", Reject("@import 'x.css';").Message);
    }

    [Fact]
    public void EmptyBlocksAndStraySemicolonsAreFine()
        => Accept(":root { }\n.x { ; }\n");

    [Fact]
    public void APseudoClassColonDoesNotExcuseAMissingOne()
    {
        // `:hover` puts a colon in the run, which is exactly how a naive check gets
        // fooled — but it lands in the SELECTOR run, terminated by `{`, so the
        // declaration after it is still judged on its own.
        var p = Reject("a:hover {\n  color red;\n}\n");
        Assert.Contains("no `:`", p.Message);
    }

    // ---- the network rules ----

    [Fact]
    public void AnHttpsUrlIsRefused()
    {
        var p = Reject(":root { --mdm-bg: url(\"https://tracker.example/pixel.png\"); }");
        Assert.Contains("outside the app", p.Message);
        Assert.Contains("data:", p.Message);
    }

    [Fact]
    public void AnHttpUrlIsRefused()
        => Assert.Contains("outside the app", Reject(".x { background: url(http://a/b.png); }").Message);

    [Fact]
    public void AProtocolRelativeUrlIsRefused()
        // `//host/x` inherits the page's scheme and is a request like any other. It
        // is also the form most likely to be mistaken for a path.
        => Assert.Contains("outside the app", Reject(".x { background: url(//a/b.png); }").Message);

    [Fact]
    public void AUrlWithoutQuotesIsStillChecked()
        => Assert.Contains("outside the app", Reject(".x { background: url( https://a/b.png ); }").Message);

    [Fact]
    public void UrlIsMatchedCaseInsensitively()
        => Assert.Contains("outside the app", Reject(".x { background: URL(HTTPS://a/b.png); }").Message);

    [Fact]
    public void ARelativeUrlIsRefusedWithItsOwnReason()
    {
        // Refused for a different reason than the remote one, and the message has to
        // say which: relative is not a security problem, it is a promise the editor
        // cannot keep. A theme's `url(texture.png)` resolves against the open
        // document's folder, never the themes folder.
        var p = Reject(".x { background: url(texture.png); }");
        Assert.Contains("relative", p.Message);
        Assert.DoesNotContain("outside the app", p.Message);
        Assert.Contains("open document", p.Message);
    }

    [Fact]
    public void ADataUrlIsAllowed()
    {
        // The sanctioned way to ship a texture: it carries its own bytes and makes
        // no request, so the rule it would break does not apply to it.
        Accept(".x { background: url(\"data:image/png;base64,iVBORw0KGgo=\"); }");
        Accept(".x { background: url(DATA:image/png;base64,iVBORw0KGgo=); }");
    }

    [Fact]
    public void AnUnquotedDataUrlIsAllowed()
    {
        // The form that matters most, and the one that was refused. A data URI has a
        // semicolon in it — `data:image/gif;base64,…` — and unquoted it sits in the
        // raw text, where a validator that reads every `;` as the end of a statement
        // decides the declaration is missing its colon.
        //
        // Which made the feature circular: reject a remote url() → "embed it as a
        // `data:` URL instead" → paste the data URI → rejected again, this time with
        // a message about a colon that has nothing to do with anything. There was no
        // path from the first error to a working file.
        Accept(".x { background: url(data:image/gif;base64,R0lGODlh); }");
        Accept(".x { background: url(data:image/gif;base64,R0lGODlh) }");
        // A brace inside one must not be counted either.
        Accept(".x { background: url(data:text/plain,}); color: red; }");
    }

    [Fact]
    public void AnUnclosedParenthesisIsReported()
    {
        // The other side of counting parens: once one is open, `;` and `}` stop being
        // punctuation, so the file has to be refused for the paren rather than for
        // whichever brace looks unbalanced afterwards.
        var p = Reject(".x { background: url(data:text/plain,abc; color: red; }");
        Assert.Contains("`url(` is never closed", p.Message);
    }

    [Fact]
    public void AnEmptyUrlIsLeftToTheBrowser()
        => Accept(".x { background: url(); }");

    [Fact]
    public void ImportIsRefused()
    {
        var p = Reject("@import \"https://example.com/theme.css\";\n:root { --mdm-text: red; }");
        Assert.Equal(1, p.Line);
        Assert.Contains("@import", p.Message);
    }

    [Fact]
    public void ImportIsRefusedEvenWithoutUrlParentheses()
        // The string form would slip past a check that only looks for `url(`.
        => Assert.Contains("@import", Reject("@import 'other.css';").Message);

    [Fact]
    public void ImportInsideAStringIsNotAnAtRule()
        // Kept for what it does prove — a string's contents are skipped wholesale —
        // and NOT for what its name used to claim. The unquoted case, which is the
        // one that actually exercises the at-rule guard, is above.
        => Accept(":root { --mdm-note: \"@import is not allowed\"; }");

    [Fact]
    public void AnEscapedUrlFunctionIsRefused()
    {
        // `\75 rl(…)` IS `url(…)`: CSS decodes escapes in identifiers before it
        // tokenizes the function name. An earlier version matched the four literal
        // characters `url(` and let all of these through — verified in Chrome by
        // watching the requests actually arrive.
        foreach (var spelling in new[] { "\\75 rl", "u\\72 l", "\\000075rl" })
        {
            var p = Reject(":root { --a: " + spelling + "(\"https://evil.example/beacon.png\"); }");
            Assert.Contains("backslash escapes", p.Message);
        }
    }

    [Fact]
    public void AStringThatIsAUrlIsRefusedEvenWithNoUrlFunction()
    {
        // `image-set()` takes a bare string, so there is no `url(` token anywhere and
        // the whole reference hides inside quotes — which the tokenizer skips. It
        // loads. Checked as "a string that starts with a scheme" rather than by
        // listing the functions that accept one, because that list keeps growing.
        foreach (var css in new[]
        {
            ".x { background-image: image-set(\"https://evil.example/a.png\" 1x); }",
            ".x { background-image: -webkit-image-set(\"https://evil.example/a.png\" 1x); }",
            ".x { background: src(\"https://evil.example/a.png\"); }",
        })
        {
            Assert.Contains("outside the app", Reject(css).Message);
        }
    }

    [Fact]
    public void EverySchemeExceptDataIsRefusedAsRemote()
    {
        // The first version tested only http, https and `//`, so everything else fell
        // into the relative branch and was told it was a relative path — a confident,
        // wrong sentence about an absolute URL.
        foreach (var url in new[] { "ftp://h/a.png", "file:///C:/windows/win.ini", "blob:https://h/abc" })
        {
            var p = Reject(".x { background: url(" + url + "); }");
            Assert.Contains("outside the app", p.Message);
            Assert.DoesNotContain("relative", p.Message);
        }
    }

    [Fact]
    public void AFragmentReferenceIsAllowed()
        // `url(#blur)` points inside the document it is already in — a filter or mask
        // reference. It makes no request, so the rule here does not reach it.
        => Accept(".x { filter: url(#blur); }");

    [Fact]
    public void AnAbsolutePathIsRefusedButNotCalledRemote()
    {
        // `/logo.png` has no scheme, so it is the relative case — it resolves against
        // the document's origin, which is not the themes folder either.
        var p = Reject(".x { background: url(/logo.png); }");
        Assert.Contains("relative", p.Message);
    }

    [Fact]
    public void WhitespaceInsideTheQuotesIsSkippedToo()
        => Assert.Contains("outside the app",
            Reject(".x { background: url(\" https://a/b.png\"); }").Message);

    [Fact]
    public void ImportIsMatchedCaseInsensitively()
        => Assert.Contains("@import", Reject("@IMPORT 'x.css';").Message);

    [Fact]
    public void AStatementAtTheTopLevelIsNotADeclaration()
        // Outside any block there are no declarations, so a bare at-rule statement
        // must not be judged as one.
        => Accept("@layer mdm-extra;\n:root { --a: red; }");
    [Fact]
    public void TopLevelJunkIsStillReported()
    {
        // The counterpart. An at-rule is the ONLY valid statement out here with no
        // colon, so anything else reaching a `;` with content and no colon is
        // malformed and gets told so — rather than waved through by a guard that
        // existed only to be cautious and that no test could distinguish.
        var p = Reject("nonsense;\n:root { --a: red; }");
        Assert.Equal(1, p.Line);
        Assert.Contains("no `:`", p.Message);
    }

    [Fact]
    public void ADeclarationThatIsOnlyAStringIsStillMissingItsColon()
        // A string counts as run content. Without that, a run whose entire body is
        // quoted looks empty and slips past the colon check.
        => Assert.Contains("no `:`", Reject(".x { \"abc\"; }").Message);


    [Fact]
    public void AStringMayNotRunPastTheEndOfItsLine()
    {
        // CSS ends a string at a newline. Without that, a missing closing quote eats
        // the rest of the file and the first real error is reported hundreds of lines
        // from where the mistake is.
        var p = Reject(":root {\n  --a: \"x;\n  --b: \"y\";\n}\n");
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void ColumnsSurviveAClosedCommentOnTheSameLine()
    {
        // Off-by-one in the comment skip would shift every column after it, which is
        // invisible until someone follows the number to the wrong character.
        var p = Reject(":root { /* hi */ --mdm-text #111; }");
        Assert.Equal(1, p.Line);
        Assert.Equal(18, p.Column);
    }

    [Fact]
    public void AnEscapeInsideAStringDoesNotHideAUrl()
    {
        // Backslashes are refused OUTSIDE a string, and inside one they are ordinary
        // — which left the whole trick intact in the one container the string rule
        // was added to close. Every spelling below reached the network in Chrome
        // while the string check was reading the raw, undecoded text.
        foreach (var url in new[]
        {
            "\\68ttp://evil.example/a.png",      // \68 -> h
            "\\68 ttp://evil.example/a.png",     // hex escape with a space terminator
            "http\\3a //evil.example/a.png",     // the colon itself escaped
            "\\2f /evil.example/a.png",          // escaped protocol-relative
        })
        {
            var css = ".x { background-image: image-set(\"" + url + "\" 1x); }";
            Assert.Contains("outside the app", Reject(css).Message);
        }
    }

    [Fact]
    public void AnEscapedRemoteUrlIsCalledRemoteAndNotRelative()
        // It was rejected before this — but as "relative", because the backslash made
        // the scheme unreadable. Right answer, wrong reason, and the reason is the
        // whole product.
        => Assert.Contains("outside the app",
            Reject(".x { background: url(\"\\68ttp://evil.example/a.png\"); }").Message);

    [Fact]
    public void OrdinaryTextWithAColonIsNotAUrl()
    {
        // The cost of checking strings at all, and it has to be paid carefully: a
        // theme that labels its callouts was being disabled with a message accusing
        // it of making a network request. A URL needs the `//`, not just a colon.
        Accept(".x::before { content: \"Note: \"; }");
        Accept(".x::before { content: \"Warning:\"; }");
        Accept(".x::before { content: \"12:30\"; }");
        Accept(":root { --doc: \"note: see the readme\"; }");
        Accept(".x { font-family: \"Foo:Bar\", sans-serif; }");
    }

    [Fact]
    public void ADigitIsNotTheStartOfAScheme()
        // RFC 3986 says a scheme begins with a letter. Reading `12` in `12:30` as one
        // is what made a clock face look like a URL.
        => Accept(".x::before { content: \"12:30\"; }");

    [Fact]
    public void ABraceInsideAnUnquotedUrlIsNotABlock()
    {
        // The realistic case, and the one mutation that showed `{` was unguarded
        // where `}` was: an inline SVG texture carries a whole stylesheet inside it.
        Accept(".x { background: url(data:image/svg+xml;utf8,<svg><style>a{fill:red}</style></svg>); }");
    }

    [Fact]
    public void SingleQuotesWorkEverywhereDoubleQuotesDo()
    {
        // Two separate places assumed a double quote: the quote skip in the url()
        // reader, and the string scanner that feeds the scheme check.
        Assert.Contains("outside the app", Reject(".x { background: url('https://a/b.png'); }").Message);
        Assert.Contains("outside the app", Reject(".x { background-image: image-set('https://a/b.png' 1x); }").Message);
        Accept(".x { background: url('data:image/gif;base64,R0lGODlh'); }");
    }

    [Fact]
    public void AStrayClosingParenthesisDoesNotUnderflow()
        // Without the `paren > 0` guard the counter goes negative and every `;` and
        // `}` after it stops being punctuation, so the rest of the file is accepted
        // no matter what is in it.
        => Assert.Contains("no `:`", Reject(".x { ) ; --a red; }").Message);

    [Fact]
    public void AnUnclosedParenthesisIsReportedWhereItOpens()
    {
        // Reported at the `(`, not at the end of the file — everything after it was
        // swallowed, and the character to go and look at is the one that opened.
        var p = Reject(":root {\n  --a: calc(1px;\n  --b: red;\n}\n");
        Assert.Equal(2, p.Line);
        Assert.Equal(12, p.Column);
    }

    [Fact]
    public void ARemoteStringIsReportedAtTheQuoteThatOpensIt()
    {
        var p = Reject(":root {\n  --a: red;\n  --b: image-set(\"https://a/b.png\" 1x);\n}\n");
        Assert.Equal(3, p.Line);
        Assert.Equal(18, p.Column);
    }

    [Fact]
    public void ADeclarationStartingWithAStringIsReportedAtTheString()
    {
        // A run that begins with a quote has to record where the quote was. Without
        // it the position is whatever was left over from the previous run — which,
        // once top-level junk started being reported, could be lines away.
        var p = Reject(".x { \"abc\"; }");
        Assert.Equal(1, p.Line);
        Assert.Equal(6, p.Column);

        var far = Reject("a { color: red; }\n\n\n\"orphan\";");
        Assert.Equal(4, far.Line);
    }

    [Fact]
    public void ASchemeMayContainPlusMinusAndDot()
        // `SchemeOf` is what decides remote-vs-relative, so its character set is not
        // cosmetic: drop the `.` and `soap.beep://x` becomes "a relative path".
        => Assert.Contains("outside the app",
            Reject(".x { background: url(soap.beep+v1-x://h/a.png); }").Message);

    [Fact]
    public void AnEscapedNewlineInsideAStringIsAContinuation()
        // CSS lets a string span lines when the newline is escaped, and the escape
        // disappears — so a backslash followed by a REAL line break inside
        // `"htt…p://…"` IS `"http://…"`, and it loads. The first version of this
        // used the two-character `\n`, which in CSS escapes the letter `n` — a
        // different rule, and one that already passed, so it proved nothing.
        => Assert.Contains("outside the app",
            Reject(".x { background-image: image-set(\"htt\\\np://evil.example/a.png\" 1x); }").Message);

    [Fact]
    public void SomethingThatIsNotASchemeIsCalledRelative()
        // `12:30` has no scheme — a scheme starts with a letter — so a url() holding
        // one is a relative reference and gets the message that says so, not the one
        // about network requests.
        => Assert.Contains("relative", Reject(".x { background: url(12:30); }").Message);

    [Fact]
    public void NestedUnclosedParenthesesReportTheOuterOne()
    {
        // The one that was never closed is the outer one; the inner is a consequence.
        // Pointing at the inner sends the author to a `(` that is fine on its own.
        var p = Reject(":root { --a: calc(min(1px; --b: red; }");
        Assert.Equal(18, p.Column);
    }

    [Fact]
    public void AnImpossibleEscapeIsRejectedRatherThanThrown()
    {
        // `\\D800` is a lone surrogate and `\\110000` is past the last code
        // point; rebuilding either as a character throws. An exception out of a pure
        // validator is not a rejected theme — it is a crash on the menu-building path.
        Accept(".x::before { content: \"\\D800\"; }");
        Accept(".x::before { content: \"\\110000\"; }");
        Accept(".x::before { content: \"\\0\"; }");
        // ...and one of those must not become a way through, either.
        Assert.Contains("outside the app",
            Reject(".x { background-image: image-set(\"\\D800https://evil.example/a.png\" 1x); }").Message);
    }

    [Fact]
    public void TheUrlParserNormalisesBeforeItParses()
    {
        // The rule this replaced was "a URL has `//` right after the scheme", which
        // is a claim about text the browser rewrites first. Chromium strips leading
        // C0 controls, deletes every tab/CR/LF from anywhere in a URL, treats `\` as
        // `/` for a special scheme, and lets that scheme's `//` be missing entirely.
        // Every spelling below was watched arriving at a local listener.
        foreach (var url in new[]
        {
            "http:\t//evil.example/a.png",          // a real tab after the colon
            "http:/\t/evil.example/a.png",          // a real tab between the slashes
            "\u0001https://evil.example/a.png",     // a C0 control, and no escape at all
            "\\9 http://evil.example/a.png",        // the same, written as a CSS escape
            "https:evil.example/a.png",             // no // at all: legal for a special scheme
            "https:\\\\evil.example/a.png",         // backslashes standing in for slashes
            "http:/\\evil.example/a.png",
            // Protocol-relative, where the scheme rule cannot help and deleting the
            // tab is the only thing standing between this and a fetch.
            "/\t/evil.example/a.png",
        })
        {
            var css = ".x { background-image: image-set(\"" + url + "\" 1x); }";
            Assert.Contains("outside the app", Reject(css).Message);
        }
    }

    [Fact]
    public void AllFourSpellingsOfALineContinuationAreOne()
    {
        // CSS folds CR, FF and CRLF into a single LF before tokenizing, so a
        // backslash before any of them is the same continuation and disappears.
        // Handling only LF left two of the four fetching.
        foreach (var newline in new[] { "\n", "\r", "\f", "\r\n" })
        {
            var css = ".x { background-image: image-set(\"htt\\" + newline +
                      "p://evil.example/a.png\" 1x); }";
            Assert.Contains("outside the app", Reject(css).Message);
        }
    }

    [Fact]
    public void AThemeSavedOnWindowsCanStillSpanALine()
    {
        // The same fold, in the direction that costs a working theme rather than
        // leaking one. CRLF is what a hand-edited file on Windows contains, and
        // hand-edited on Windows is the whole distribution story for themes — reading
        // the CR as an escaped character and then tripping over the LF reports an
        // unterminated string, lines from anything wrong.
        Accept(".x::before { content: \"foo\\\r\nbar\"; }");
        Accept(".x::before { content: \"foo\\\rbar\"; }");
        Accept(".x::before { content: \"foo\\\fbar\"; }");
    }

    [Fact]
    public void AnAttributeSelectorMayNotTestAValue()
    {
        // This test used to assert the opposite, and the reversal is the point.
        //
        // `a[href^=\"https://\"]` is a reasonable thing for a documentation theme to
        // write and is harmless in itself — but it is also the exact shape of the
        // exfiltration oracle, and nothing downstream can tell the two apart: each
        // match fires an image request, and images have to stay allowed because
        // documents legitimately reference them. So the comparison goes.
        //
        // Over-rejecting, deliberately. The cost is a message; the cost of the other
        // answer is a stylesheet that reads a document out one character at a time.
        foreach (var css in new[]
        {
            "a[href^=\"https://\"]::after { content: \" x\"; }",
            "span[data-value^=\"a\"] { background: red; }",
            "input[value$=\"9\"] { color: red; }",
            "div[class*=\"secret\"] { color: red; }",
            "p[lang|=\"en\"] { color: red; }",
            "p[rel~=\"tag\"] { color: red; }",
            "a[title=\"x\"] { color: red; }",
        })
        {
            Assert.Contains("tests a VALUE", Reject(css).Message);
        }
    }

    [Fact]
    public void AnAttributeSelectorMayStillTestPresence()
        // Still allowed, because it reads nothing back: whether an attribute EXISTS
        // is structure, not content.
        => Accept("details[open] > summary { color: red; }\ninput[disabled] { color: gray; }");

    [Fact]
    public void HexEscapesStopAtSixDigits()
    {
        // `\000068` is six digits and ends there, so a seventh hex character is
        // ordinary text: `\000068a` is `ha`, not one enormous code point.
        Accept(".x::before { content: \"\\000068a\"; }");
        // And the character after a SHORT escape must not be eaten — `\41z` is `Az`.
        // Swallowing the `z` would quietly change what the value says.
        Accept(".x::before { content: \"\\41z\"; }");
        // The decoder still has to join what it should: `\68` + `ttps://` is a URL.
        Assert.Contains("outside the app",
            Reject(".x { background-image: image-set(\"\\68ttps://evil.example/a\" 1x); }").Message);
    }

    [Fact]
    public void AUrlFunctionIsNormalisedToo()
        // The `url()` path had every reason the string path did and none of the
        // normalisation, so a tab after the colon made a remote URL look relative.
        => Assert.Contains("outside the app",
            Reject(".x { background: url(\"http:\t//evil.example/a.png\"); }").Message);

    [Fact]
    public void ATrailingBackslashDoesNotRunOffTheEnd()
        // A file that ends mid-escape is malformed, not a crash.
        => Reject(".x::before { content: \"abc\\");

    [Fact]
    public void NullAndOutOfRangeEscapesDropRatherThanThrow()
    {
        // CSS substitutes U+FFFD for these; dropping them is equally safe here, since
        // neither can spell a scheme. What matters is that neither throws.
        Accept(".x::before { content: \"\\0\"; }");
        Accept(".x::before { content: \"\\0hello\"; }");
    }

    [Fact]
    public void ABracketInsideAUrlTokenDoesNotDisarmTheRestOfTheFile()
    {
        // The attribute-selector skip, turned into a bypass. `url(data:text/plain,[)`
        // is a legal url-token that Chromium applies without complaint, but counting
        // that `[` left the bracket depth stuck above zero — and every later string
        // then skipped the URL check. A false-positive fix that became the hole.
        //
        // Confirmed fetching in Chrome, in all of these shapes, while the validator
        // returned "no problem".
        foreach (var css in new[]
        {
            ".a { background: url(data:text/plain,[); }\n" +
            ".b { background-image: image-set(\"https://evil.example/a.png\" 1x); }",

            // Same declaration, so a reset at `;` or `}` alone would not save it.
            ".b { background-image: image-set(url(data:text/plain,[) 2x, \"https://evil.example/a.png\" 1x); }",
            ".b { background-image: url(data:text/plain,[) , image-set(\"https://evil.example/a.png\" 1x); }",
            ".a { background: url(data:image/svg+xml,%3Csvg%3E[); }\n" +
            "@media print { .b { background-image: image-set(\"https://evil.example/a.png\" 1x); } }",
        })
        {
            Assert.Contains("outside the app", Reject(css).Message);
        }
    }

    [Fact]
    public void AStrayBracketDoesNotSurviveTheStatementItIsIn()
    {
        // Belt and braces on the same failure: an attribute selector cannot span a
        // `{`, `}` or `;`, so a `[` that never closes is confined to its statement
        // rather than switching the URL check off for the remainder of the file.
        Assert.Contains("outside the app", Reject(
            "a[href { color: red; }\n.b { background-image: image-set(\"https://evil.example/a.png\" 1x); }").Message);
        Assert.Contains("outside the app", Reject(
            ".x { color: red[ }\n.b { background-image: image-set(\"https://evil.example/a.png\" 1x); }").Message);
    }

    [Fact]
    public void ABracketDepthCannotGoNegative()
        // Without the guard the counter underflows and never returns to zero, which
        // disables the URL check for everything after it.
        => Assert.Contains("outside the app", Reject(
            "a[href]] { color: red; }\n.b { background-image: image-set(\"https://evil.example/a.png\" 1x); }").Message);

    [Fact]
    public void ALoneCarriageReturnOrFormFeedEndsAString()
    {
        // CR and FF ARE newlines to CSS — folded to LF before tokenizing — so each
        // ends an unterminated string exactly as LF does. Stepping over one made this
        // scanner pair quotes differently from the browser: three quotes consumed
        // where Chromium consumes one, which shifted every later string and hid the
        // payload's URL inside a span that was never inspected. Both fetched.
        foreach (var newline in new[] { "\r", "\f" })
        {
            var css = ".a::before{content:\"P" + newline + "\"q\"}" +
                      ".b{background-image:image-set(\"https://evil.example/a.png\" 1x)}" +
                      ".c::before{content:\"r" + newline + "\"s\"}";
            var p = Reject(css);
            // NOT A PIN, and labelled so rather than left to look like one: this
            // payload ends on an unbalanced quote, so it is rejected with or without
            // the CR/FF handling — a mutation restoring the old behaviour survives it.
            // The fix is in the code and is right; this asserts only that the file
            // does not sail through. A payload whose quotes balance under BOTH
            // pairings is what would pin it, and I have not built one.
            Assert.Contains("never closed", p.Message);
        }
    }

    [Fact]
    public void EveryLeadingControlCharacterIsStripped()
        // The URL parser strips ALL leading C0 controls and spaces, not one — so a
        // single-character trim leaves two of them hiding a scheme.
        => Assert.Contains("outside the app", Reject(
            ".b { background-image: image-set(\"\u0001\u0002https://evil.example/a.png\" 1x); }").Message);

    [Fact]
    public void TheSafetyScanRefusesWhatAThemeDoesNotNeed()
    {
        // Over-broad on purpose. Each of these is refused whether or not the spelling
        // in front of us is exploitable, because the cost of refusing a construct a
        // palette does not need is a message, and the cost of allowing one it can
        // misuse is a surface that four review rounds could not close by cleverness.
        Assert.Contains(":has()", Reject("div:has(> img) { color: red; }").Message);
        Assert.Contains("@font-face", Reject("@font-face { font-family: X; }").Message);
        Assert.Contains("run script", Reject(".x { width: expression(alert(1)); }").Message);
        Assert.Contains("attach script", Reject(".x { -moz-binding: url(x.xml#y); }").Message);
        Assert.Contains("attach script", Reject(".x { behavior: url(x.htc); }").Message);
    }

    [Fact]
    public void TheSafetyScanLeavesAnOrdinaryPaletteAlone()
        // The other half of over-rejecting: it has to not reject the thing people
        // actually write. A palette is variables and plain selectors.
        => Accept("""
        :root { --mdm-page-bg: #ffffff; --mdm-text: #1a1a1a; }
        .mdm-prosemirror blockquote { background: #f2f7fb; border-left: 6px solid #569ad4; }
        .mdm-prosemirror h1, .mdm-prosemirror h2 { color: #4682b4; }
        .mdm-prosemirror a:hover { color: #2f5f87; }
        details[open] { color: red; }
        @media print { :root { --mdm-text: #000; } }
        """);

    // ---- the message itself ----

    [Fact]
    public void ProblemsReadAsASentenceWithAPlace()
    {
        // This string is the entire tooltip. If it does not say where and what, the
        // feature is "your theme is broken, good luck".
        var p = Reject(":root {\n  --mdm-text #111;\n}\n");
        var text = p.ToString();
        Assert.Contains("line 2", text);
        Assert.Contains("column 3", text);
        Assert.Contains("no `:`", text);
    }

    [Fact]
    public void TheFirstProblemIsTheOneReported()
    {
        // A file with three faults should send the author to the first one; fixing
        // it reveals the next. Reporting the last would be actively misleading.
        var p = Reject(":root {\n  --mdm-text #111;\n  --mdm-bg: url(http://a/b.png);\n");
        Assert.Equal(2, p.Line);
        Assert.Contains("no `:`", p.Message);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void LineNumbersSurviveEitherLineEnding(string newline)
    {
        var css = string.Join(newline, ":root {", "  --a: 1;", "  --b: 2;", "  --c 3;", "}");
        Assert.Equal(4, Reject(css).Line);
    }
}
