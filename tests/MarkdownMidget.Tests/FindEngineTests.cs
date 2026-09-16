using System.IO;
using System.Linq;
using System.Text.Json;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Covers the Regex builder for each of the four Find modes. The regex object
/// itself is a runtime-composable value, so we just build it and probe with
/// <c>IsMatch</c> on representative samples.
/// </summary>
public class FindEngineTests
{
    // ===== Empty / invalid input =====

    [Fact]
    public void Empty_query_returns_null()
        => Assert.Null(FindEngine.Build("", FindEngine.Mode.Normal, false, false));

    [Fact]
    public void Invalid_regex_returns_null()
        => Assert.Null(FindEngine.Build("(unclosed", FindEngine.Mode.Regex, false, false));

    // ===== Normal =====

    [Theory]
    [InlineData("hello", "say hello world", true)]
    [InlineData("Hello", "say hello world", true)]    // case-insensitive by default
    [InlineData("Hello", "say HELLO world", true)]
    [InlineData("xyz", "nothing here", false)]
    public void Normal_case_insensitive_by_default(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Theory]
    [InlineData("Hello", "Hello world", true)]
    [InlineData("Hello", "hello world", false)]
    public void Normal_match_case_requires_exact(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, matchCase: true, wholeWord: false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Theory]
    [InlineData("foo", "foobar", false)]      // partial word — reject
    [InlineData("foo", "foo bar", true)]      // whole word — accept
    [InlineData("foo", "(foo)", true)]        // word boundary at parens
    public void WholeWord_wraps_boundary(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Normal, false, wholeWord: true);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Normal_escapes_regex_specials()
    {
        // The literal string "a.b" should NOT match "aXb" (the '.' is escaped).
        var re = FindEngine.Build("a.b", FindEngine.Mode.Normal, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "a.b");
        Assert.DoesNotMatch(re, "aXb");
    }

    // ===== Extended =====

    [Theory]
    [InlineData(@"line\nbreak", "line\nbreak")]
    [InlineData(@"tab\there", "tab\there")]
    [InlineData(@"cr\rreturn", "cr\rreturn")]
    public void Extended_recognizes_common_escapes(string query, string sample)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, sample);
    }

    [Fact]
    public void Extended_hex_byte_escape()
    {
        // \x20 = space
        var re = FindEngine.Build(@"has\x20a\x20space", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "has a space here");
    }

    [Fact]
    public void Extended_unicode_escape()
    {
        // — = em dash (—)
        var re = FindEngine.Build(@"em—dash", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "an em—dash");
    }

    [Fact]
    public void Extended_literal_backslash()
    {
        var re = FindEngine.Build(@"a\\b", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, @"a\b");
        Assert.DoesNotMatch(re, "aXb");
    }

    [Fact]
    public void Extended_unknown_escape_treats_char_literally()
    {
        // \. in Extended mode → literal '.', not "any char"
        var re = FindEngine.Build(@"a\.b", FindEngine.Mode.Extended, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "a.b");
        Assert.DoesNotMatch(re, "aXb");
    }

    // ===== Wildcards =====

    [Theory]
    [InlineData("h*o", "hello", true)]                   // '*' = any chars
    [InlineData("h*o", "ho", true)]                       // '*' = also zero chars
    [InlineData("h?llo", "hello", true)]                  // '?' = exactly one
    [InlineData("h?llo", "heello", false)]                // '?' does NOT match two
    [InlineData("*star*", "big star!", true)]
    public void Wildcards_expand_star_and_question(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Wildcards_escape_star()
    {
        // \* → literal '*', not "any chars"
        var re = FindEngine.Build(@"say\*star", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say*starred");
        Assert.DoesNotMatch(re, "say Xstarred");
    }

    [Fact]
    public void Wildcards_escape_question()
    {
        var re = FindEngine.Build(@"say\?done", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say?done");
        Assert.DoesNotMatch(re, "sayXdone");
    }

    [Fact]
    public void Wildcards_escape_regex_specials()
    {
        // Non-wildcard regex metachars should still be treated literally.
        var re = FindEngine.Build("(hello)", FindEngine.Mode.Wildcards, false, false);
        Assert.NotNull(re);
        Assert.Matches(re!, "say (hello) yo");
    }

    // ===== Regex =====

    [Theory]
    [InlineData(@"\d{4}", "year 2026", true)]
    [InlineData(@"^Heading", "Heading text", true)]
    [InlineData(@"^Heading", "  Heading indented", false)]
    [InlineData(@"foo|bar", "just bar here", true)]
    public void Regex_passes_through(string query, string sample, bool expected)
    {
        var re = FindEngine.Build(query, FindEngine.Mode.Regex, false, false);
        Assert.NotNull(re);
        if (expected) Assert.Matches(re!, sample); else Assert.DoesNotMatch(re!, sample);
    }

    [Fact]
    public void Regex_multiline_anchors_work_line_by_line()
    {
        var re = FindEngine.Build(@"^bar", FindEngine.Mode.Regex, false, false);
        Assert.NotNull(re);
        // Multiline mode is on so ^ matches the start of each line, not just the doc.
        Assert.Matches(re!, "foo\nbar\nbaz");
    }

    // ===== Replace (#5): the per-mode replacement text and the Replace All edit plan =====
    //
    // The pure half of Replace. A ReplaceSpec is a query and its replacement prepared
    // for one mode; ReplaceAllEdits plans the edits for a text (optionally within a
    // scope) and ApplyEdits is the reference application the views mirror.

    private static string ReplaceAll(string text, string query, FindEngine.Mode mode, string replacement,
        int scopeStart = 0, int scopeLength = -1, bool matchCase = false, bool wholeWord = false)
    {
        var spec = FindEngine.Prepare(query, mode, matchCase, wholeWord, replacement);
        Assert.NotNull(spec);
        return FindEngine.ApplyEdits(text, spec!.ReplaceAllEdits(text, scopeStart, scopeLength));
    }

    [Fact]
    public void ReplaceNormalIsLiteral()
    {
        // A '$' in a Normal-mode replacement is a dollar sign, never a group reference.
        var spec = FindEngine.Prepare("a.b", FindEngine.Mode.Normal, false, false, "$1-$&");
        Assert.NotNull(spec);
        Assert.True(spec!.Literal);
        Assert.Equal("$1-$& aXb", ReplaceAll("a.b aXb", "a.b", FindEngine.Mode.Normal, "$1-$&"));
    }

    [Fact]
    public void ReplaceWildcardsIsLiteral()
    {
        var spec = FindEngine.Prepare("h*o", FindEngine.Mode.Wildcards, false, false, "$&");
        Assert.NotNull(spec);
        Assert.True(spec!.Literal);
        Assert.Equal("$& there", ReplaceAll("hello there", "h*o", FindEngine.Mode.Wildcards, "$&"));
    }

    [Theory]
    [InlineData(@"a\tb", "a\tb")]
    [InlineData(@"a\nb", "a\nb")]
    [InlineData(@"a\rb", "a\rb")]
    [InlineData(@"a\\b", @"a\b")]
    [InlineData(@"a\0b", "a\0b")]
    [InlineData(@"\x41", "A")]
    [InlineData(@"\u2014", "—")]
    [InlineData(@"a\.b", "a.b")]          // unknown escape: the character itself, as in the query
    [InlineData(@"\xZZ", "xZZ")]          // not two hex digits: the 'x' is literal, like the query side
    [InlineData(@"trailing\", @"trailing\")]
    [InlineData(@"$1", "$1")]             // Extended is literal apart from its escapes
    public void ReplaceExtendedExpandsEscapes(string replacement, string expected)
    {
        var spec = FindEngine.Prepare("x", FindEngine.Mode.Extended, false, false, replacement);
        Assert.NotNull(spec);
        Assert.True(spec!.Literal);
        Assert.Equal(expected, spec.Replacement);
        Assert.Equal(expected, ReplaceAll("x", "x", FindEngine.Mode.Extended, replacement));
    }

    [Fact]
    public void ReplaceGroups()
    {
        // Regex mode hands the template to .NET: numbered and named groups, $0 / $& for
        // the whole match, $$ for a dollar sign.
        Assert.Equal("host at me", ReplaceAll("me@host", @"(\w+)@(\w+)", FindEngine.Mode.Regex, "$2 at $1"));
        Assert.Equal("me!host", ReplaceAll("me@host", @"(?<user>\w+)@", FindEngine.Mode.Regex, "${user}!"));
        Assert.Equal("[cat] [cat]", ReplaceAll("cat cat", "cat", FindEngine.Mode.Regex, "[$0]"));
        Assert.Equal("<cat>", ReplaceAll("cat", "cat", FindEngine.Mode.Regex, "<$&>"));
        Assert.Equal("$5", ReplaceAll("cost", "cost", FindEngine.Mode.Regex, "$$5"));

        // Same thing one match at a time, the way Replace (not All) uses it.
        var spec = FindEngine.Prepare(@"(\w+)@(\w+)", FindEngine.Mode.Regex, false, false, "$2 at $1");
        Assert.NotNull(spec);
        Assert.False(spec!.Literal);
        var m = spec.Regex.Match("me@host");
        Assert.Equal("host at me", spec.ReplacementFor(m));
    }

    [Fact]
    public void ReplaceAllWithinSelection()
    {
        // "cat" at 0, 4, 8, 12. A scope of [4, 11) holds the middle two whole.
        const string text = "cat cat cat cat";
        Assert.Equal("cat dog dog cat", ReplaceAll(text, "cat", FindEngine.Mode.Normal, "dog", scopeStart: 4, scopeLength: 7));
        // A match that only partly overlaps the scope is outside it: [5, 11) cuts the
        // second "cat" in half, so only the third is replaced.
        Assert.Equal("cat cat dog cat", ReplaceAll(text, "cat", FindEngine.Mode.Normal, "dog", scopeStart: 5, scopeLength: 6));
        // No scope (the default) is the whole text.
        Assert.Equal("dog dog dog dog", ReplaceAll(text, "cat", FindEngine.Mode.Normal, "dog"));
        // An empty scope replaces nothing.
        Assert.Equal(text, ReplaceAll(text, "cat", FindEngine.Mode.Normal, "dog", scopeStart: 4, scopeLength: 0));
    }

    [Fact]
    public void AMatchThatEndsOneCharacterPastTheScopeIsOutsideIt()
    {
        // The edge itself. '>=' instead of '>', or a stray +1 on the scope end, lets a
        // match that overruns the selection by exactly one character through — and the
        // case above, where the matches overrun by more, never notices (#5 F-8).
        Assert.Equal("catx", ReplaceAll("catx", "cat", FindEngine.Mode.Normal, "dog", scopeStart: 0, scopeLength: 2));
        // One character more of scope and the same match is inside it.
        Assert.Equal("dogx", ReplaceAll("catx", "cat", FindEngine.Mode.Normal, "dog", scopeStart: 0, scopeLength: 3));
    }

    [Fact]
    public void ReplaceAllEditsAreInDocumentOrderWithTheMatchedLength()
    {
        var spec = FindEngine.Prepare("cat", FindEngine.Mode.Normal, false, false, "tiger");
        var edits = spec!.ReplaceAllEdits("cat, cat");
        Assert.Equal(2, edits.Count);
        Assert.Equal(new FindEngine.Edit(0, 3, "tiger"), edits[0]);
        Assert.Equal(new FindEngine.Edit(5, 3, "tiger"), edits[1]);
        // And applying them yields the text a plain Regex.Replace would.
        Assert.Equal("tiger, tiger", FindEngine.ApplyEdits("cat, cat", edits));
    }

    [Fact]
    public void ReplaceAllOfAnEmptyMatchInserts()
    {
        // ^ in multiline mode matches at every line start with zero length; replacing it
        // inserts, exactly as Regex.Replace does — the "prefix every line" idiom.
        Assert.Equal("> a\n> b", ReplaceAll("a\nb", "^", FindEngine.Mode.Regex, "> "));
    }

    [Fact]
    public void ReplaceRemovesWhenTheReplacementIsEmpty()
    {
        Assert.Equal("a b", ReplaceAll("a cat b", "cat ", FindEngine.Mode.Normal, ""));
    }

    [Fact]
    public void ReplaceHonoursMatchCaseAndWholeWord()
    {
        Assert.Equal("Cat dog", ReplaceAll("Cat cat", "cat", FindEngine.Mode.Normal, "dog", matchCase: true));
        Assert.Equal("dog catalog", ReplaceAll("cat catalog", "cat", FindEngine.Mode.Normal, "dog", wholeWord: true));
    }

    [Fact]
    public void MalformedPatternIsRefused()
    {
        // A malformed regex never becomes a ReplaceSpec, so there is nothing to apply;
        // the host shows the same message Find shows, and that message is this one.
        Assert.Null(FindEngine.Prepare("(unclosed", FindEngine.Mode.Regex, false, false, "x"));
        Assert.Null(FindEngine.Prepare("", FindEngine.Mode.Normal, false, false, "x"));
        Assert.Equal("Invalid pattern.", FindEngine.InvalidPatternMessage);
    }

    [Fact]
    public void ApplyEditsWithNoEditsReturnsTheTextUnchanged()
    {
        Assert.Equal("abc", FindEngine.ApplyEdits("abc", System.Array.Empty<FindEngine.Edit>()));
    }

    // ===== The replacement-template subset, shared with the formatted view (#5 F-1, F-4, F-13) =====
    //
    // editor-src/test/fixtures/replace-templates.json is linked into this test project's
    // output and read by editor-src/test/find.test.mjs as well. Both sides build the
    // regex case-sensitively and multiline, take the first match in `input`, expand
    // `template` against it and must produce `expected`. A row only one side satisfies
    // is exactly the divergence the table exists to catch.

    public static TheoryData<string, string, string, string, string> TemplateRows()
    {
        var data = new TheoryData<string, string, string, string, string>();
        foreach (var row in ReadTemplateTable())
            data.Add(row.Name, row.Pattern, row.Input, row.Template, row.Expected);
        return data;
    }

    private static (string Name, string Pattern, string Input, string Template, string Expected)[] ReadTemplateTable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "replace-templates.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => (
                r.GetProperty("name").GetString()!,
                r.GetProperty("pattern").GetString()!,
                r.GetProperty("input").GetString()!,
                r.GetProperty("template").GetString()!,
                r.GetProperty("expected").GetString()!))
            .ToArray();
    }

    [Fact]
    public void TheSharedTemplateTableIsActuallyRead()
    {
        // A theory over an empty table is a green run that proves nothing: if the
        // csproj ever stops copying the fixture next to the assembly, fail here
        // rather than pass twenty-seven times over nothing.
        Assert.True(ReadTemplateTable().Length >= 20,
            "editor-src/test/fixtures/replace-templates.json must be copied to the test output");
    }

    [Theory]
    [MemberData(nameof(TemplateRows))]
    public void TheReplacementTemplateSubsetIsTheSameInBothEngines(
        string name, string pattern, string input, string template, string expected)
    {
        var re = FindEngine.Build(pattern, FindEngine.Mode.Regex, matchCase: true, wholeWord: false);
        Assert.True(re is not null, $"{name}: the pattern did not compile");
        var m = re!.Match(input);
        Assert.True(m.Success, $"{name}: the pattern finds nothing in {input}");
        Assert.Equal(expected, FindEngine.ExpandTemplate(template, m));
    }

    // ===== The two scope decisions the host makes, lifted out of the window (#5 F-5, F-14) =====

    [Fact]
    public void CtrlFKeepsTheRangeItHasWhenTheSelectionIsFindsOwn()
    {
        // Find's own selection of a ZERO-WIDTH match is a caret. Asking "is it empty?"
        // before "is it Find's own?" read that as the user deselecting and threw the
        // kept Replace All range away (#5 F-5).
        Assert.Equal(FindEngine.ScopeCapture.Keep, FindEngine.CaptureDecision(0, isFindSelection: true));
        Assert.Equal(FindEngine.ScopeCapture.Keep, FindEngine.CaptureDecision(3, isFindSelection: true));
        Assert.Equal(FindEngine.ScopeCapture.Drop, FindEngine.CaptureDecision(0, isFindSelection: false));
        Assert.Equal(FindEngine.ScopeCapture.Take, FindEngine.CaptureDecision(3, isFindSelection: false));
    }

    [Fact]
    public void ReplaceAllScopeIsTheUsersSelectionOrTheRangeKeptForThem()
    {
        // The user's own selection is the scope.
        Assert.Equal((4, 7), FindEngine.ResolveScope(4, 7, isFindSelection: false, captured: null));
        // A caret of the user's own is no scope: the whole document.
        Assert.Null(FindEngine.ResolveScope(4, 0, isFindSelection: false, captured: (1, 9)));
        // Find's selection is not the user's: the range kept when the dialog opened is.
        Assert.Equal((1, 9), FindEngine.ResolveScope(4, 3, isFindSelection: true, captured: (1, 9)));
        // …including when Find's selection is a caret on a zero-width match.
        Assert.Equal((1, 9), FindEngine.ResolveScope(4, 0, isFindSelection: true, captured: (1, 9)));
        // Nothing kept, or a kept range edited away to nothing: the whole document.
        Assert.Null(FindEngine.ResolveScope(4, 3, isFindSelection: true, captured: null));
        Assert.Null(FindEngine.ResolveScope(4, 3, isFindSelection: true, captured: (1, 0)));
    }

    [Fact]
    public void ReplaceAllStatusSaysWhichKindOfLeftAloneEachOneWas()
    {
        Assert.Equal("Replaced 3 occurrences.", FindEngine.ReplaceAllStatus(3, false, 0, 0));
        Assert.Equal("Replaced 1 occurrence in the selection.", FindEngine.ReplaceAllStatus(1, true, 0, 0));
        Assert.Equal("Nothing to replace.", FindEngine.ReplaceAllStatus(0, false, 0, 0));
        Assert.Equal("Nothing to replace in the selection.", FindEngine.ReplaceAllStatus(0, true, 0, 0));

        // A match that runs from one paragraph into the next is never joined.
        Assert.Equal("Replaced 2 occurrences. 1 left alone: it spans paragraphs.",
            FindEngine.ReplaceAllStatus(2, false, 1, 0));
        Assert.Equal("Replaced 2 occurrences. 2 left alone: they span paragraphs.",
            FindEngine.ReplaceAllStatus(2, false, 2, 0));

        // A match whose text is no longer where the search left it is a different
        // thing, and saying it "spans paragraphs" was simply untrue (#5 F-10).
        Assert.Equal("Replaced 2 occurrences. 1 left alone: the text moved.",
            FindEngine.ReplaceAllStatus(2, false, 0, 1));
        Assert.Equal("Nothing to replace. 1 left alone: it spans paragraphs. 2 left alone: the text moved.",
            FindEngine.ReplaceAllStatus(0, false, 1, 2));
    }

    // ===== Which constructs Find accepts at all (#5 F-6) =====
    //
    // The source view runs .NET's Regex, the formatted view JavaScript's. A pattern
    // that means one thing in one and something else — or nothing — in the other is
    // refused outright, with the message Find already shows for a malformed pattern.
    // editor-src/test/fixtures/regex-constructs.json is the list, and find.test.mjs
    // reads the same file to check the accepted ones really do compile over there —
    // and, since #5 NF-6, that each refused row behaves in the browser engine the way
    // its "refusedBy" says it does.

    private static JsonElement Constructs()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "regex-constructs.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static TheoryData<string, string> ConstructRows(string section)
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Constructs().GetProperty(section).EnumerateArray())
            data.Add(row.GetProperty("pattern").GetString()!,
                     row.TryGetProperty("why", out var w) ? w.GetString()! : "");
        return data;
    }

    public static TheoryData<string, string> AcceptedConstructs() => ConstructRows("accepted");
    public static TheoryData<string, string> RefusedConstructs() => ConstructRows("refused");

    public static TheoryData<string, string, bool, string> LiteralPatterns()
    {
        var data = new TheoryData<string, string, bool, string>();
        foreach (var row in Constructs().GetProperty("literals").EnumerateArray())
            data.Add(row.GetProperty("query").GetString()!,
                     row.GetProperty("mode").GetString()!,
                     row.TryGetProperty("wholeWord", out var ww) && ww.GetBoolean(),
                     row.GetProperty("pattern").GetString()!);
        return data;
    }

    [Fact]
    public void TheSharedConstructTableIsActuallyRead()
    {
        var root = Constructs();
        Assert.True(root.GetProperty("accepted").GetArrayLength() >= 20, "accepted rows");
        Assert.True(root.GetProperty("refused").GetArrayLength() >= 20, "refused rows");
        Assert.True(root.GetProperty("literals").GetArrayLength() >= 8, "literal rows");

        // Every refused row says which engine refuses it — "javascript", "dotnet", or
        // "host" for one both engines compile and only FindEngine.JsCompatible stops.
        // find.test.mjs asserts the browser half against this field, and a row without
        // it would assert nothing over there (#5 NF-6).
        foreach (var row in root.GetProperty("refused").EnumerateArray())
        {
            var pattern = row.GetProperty("pattern").GetString();
            Assert.True(row.TryGetProperty("refusedBy", out var by), $"{pattern}: no refusedBy");
            Assert.True(by.GetString() is "javascript" or "dotnet" or "host",
                $"{pattern}: refusedBy is {by.GetString()}");
        }
    }

    [Theory]
    [MemberData(nameof(AcceptedConstructs))]
    public void AcceptedRegexConstructsStillCompile(string pattern, string why)
    {
        Assert.True(FindEngine.Build(pattern, FindEngine.Mode.Regex, true, false) is not null,
            $"{pattern} should be accepted — {why}");
        Assert.True(FindEngine.JsCompatible(pattern), $"{pattern} — {why}");
    }

    [Theory]
    [MemberData(nameof(RefusedConstructs))]
    public void RefusedRegexConstructsAreRefusedBeforeEitherViewSeesThem(string pattern, string why)
    {
        // Build is the one gate: the host asks it before it dispatches to either view,
        // so a null here is "Invalid pattern." in the source view AND the formatted one.
        Assert.True(FindEngine.Build(pattern, FindEngine.Mode.Regex, true, false) is null,
            $"{pattern} should be refused — {why}");
    }

    [Theory]
    [MemberData(nameof(LiteralPatterns))]
    public void LiteralModesEscapeOnlyWhatBothEnginesCallSyntax(string query, string mode, bool wholeWord, string expected)
    {
        // .NET's own Regex.Escape writes "\ " for a space and "\#" for a hash. Neither
        // is an escape JavaScript's unicode mode recognises, so a Normal-mode search
        // for two words would have stopped compiling in the formatted view.
        var m = mode switch
        {
            "Normal" => FindEngine.Mode.Normal,
            "Extended" => FindEngine.Mode.Extended,
            "Wildcards" => FindEngine.Mode.Wildcards,
            _ => FindEngine.Mode.Regex,
        };
        var re = FindEngine.Build(query, m, matchCase: true, wholeWord: wholeWord);
        Assert.True(re is not null, $"{mode} {query} did not compile");
        Assert.Equal(expected, re!.ToString());
    }

    [Fact]
    public void JsCompatibleJudgesAPatternOnItsOwn()
    {
        // The check the host can run without asking the editor anything.
        Assert.True(FindEngine.JsCompatible(@"\bword\b"));
        Assert.True(FindEngine.JsCompatible(@"(?<x>a)\k<x>"));
        Assert.False(FindEngine.JsCompatible(@"\Acat"));
        Assert.False(FindEngine.JsCompatible("(?>ab)"));
        Assert.False(FindEngine.JsCompatible(@"trailing\"));
        Assert.False(FindEngine.JsCompatible("[a-z-[aeiou]]"));
    }

    [Fact]
    public void TheSourceViewsAnchorsCountEveryLine()
    {
        // HELP's "^ is the start of every line and $ the end of every line" for the
        // source view, against the formatted view's one-each. Three of each in "one",
        // a blank line, "two" — where find.test.mjs counts one over the same document
        // (#5 NF-3).
        var caret = FindEngine.Build("^", FindEngine.Mode.Regex, matchCase: true, wholeWord: false)!;
        var dollar = FindEngine.Build("$", FindEngine.Mode.Regex, matchCase: true, wholeWord: false)!;
        Assert.Equal(3, caret.Matches("one\n\ntwo").Count);
        Assert.Equal(3, dollar.Matches("one\n\ntwo").Count);
    }

    [Fact]
    public void TheDivergencesHelpDocumentsRatherThanRefusesAreRealOnThisSide()
    {
        // Two HELP now names, both left alone because the formatted view's own
        // refusal — or a count — is a safe answer rather than a different document.
        //
        // Duplicate group names compile here and are refused over there, where the
        // host's backstop turns the refusal into the usual "Invalid pattern."
        Assert.NotNull(FindEngine.Build(@"(?<a>x)(?<a>y)", FindEngine.Mode.Regex, matchCase: true, wholeWord: false));

        // Line terminators: .NET's Multiline starts a line after a line feed and
        // nothing else, where the browser engine also counts a carriage return and
        // the U+2028 / U+2029 separators.
        var caret = FindEngine.Build("^", FindEngine.Mode.Regex, matchCase: true, wholeWord: false)!;
        Assert.Equal(2, caret.Matches("a\nb").Count);
        Assert.Single(caret.Matches("a\rb"));
        Assert.Single(caret.Matches("a\u2028b"));
        Assert.Single(caret.Matches("a\u2029b"));
    }

    [Fact]
    public void ANumberedBackreferenceIsRefusedWhereTheTwoEnginesNumberGroupsDifferently()
    {
        // .NET numbers the unnamed groups first and the named ones after; JavaScript
        // numbers them all in source order. Only a named group written BEFORE an
        // unnamed one reorders anything — and then a number means a different group in
        // each view: "(?<a>x)(y)\1" matches "xyy" in the source view and "xyx" in the
        // formatted one. Both engines compile it, so nothing but this refuses it, and
        // the same renumbering dotnetGroupMap handles for replacement templates was
        // missed on the pattern side (#5 NF-1).
        Assert.False(FindEngine.JsCompatible(@"(?<a>x)(y)\1"));
        Assert.False(FindEngine.JsCompatible(@"\1(?<a>x)(y)"));      // the number may be written first
        Assert.False(FindEngine.JsCompatible(@"(a)(?<b>x)(c)\3"));   // a named group in the middle counts
        Assert.False(FindEngine.JsCompatible(@"(?<a>x)(y)\12"));     // two digits, same story

        // Arrangements the two engines number the same way stay allowed.
        Assert.True(FindEngine.JsCompatible(@"(x)(?<b>y)\1"));       // every named group comes last
        Assert.True(FindEngine.JsCompatible(@"(?<a>x)\1"));          // nothing unnamed to reorder
        Assert.True(FindEngine.JsCompatible(@"(?<a>x)(?<b>y)\2"));   // all named, source order in both
        Assert.True(FindEngine.JsCompatible(@"(?<a>x)(y)\k<a>"));    // \k<name> is one group in both
        Assert.True(FindEngine.JsCompatible(@"(?<a>x)(?:y)\1"));     // (?: captures nothing

        // And Build — the one gate both views go through — says no.
        Assert.Null(FindEngine.Build(@"(?<a>x)(y)\1", FindEngine.Mode.Regex, matchCase: true, wholeWord: false));
    }

    [Fact]
    public void ReportsInvalidPatternReadsTheEditorsRefusal()
    {
        // findReset answers { total: 0, current: 0, error: 'Invalid pattern' } for a
        // pattern JavaScript will not compile. Before this, nothing read that field and
        // the user saw "No matches found." for a pattern that was never run.
        Assert.True(FindEngine.ReportsInvalidPattern("{\"total\":0,\"current\":0,\"error\":\"Invalid pattern\"}"));
        Assert.False(FindEngine.ReportsInvalidPattern("{\"total\":3,\"current\":1}"));
        Assert.False(FindEngine.ReportsInvalidPattern("{\"replaced\":0,\"error\":\"no editor\"}"));
        Assert.False(FindEngine.ReportsInvalidPattern(null));
        Assert.False(FindEngine.ReportsInvalidPattern(""));
        Assert.False(FindEngine.ReportsInvalidPattern("not json at all"));
    }

    [Fact]
    public void ReportsTruncatedReadsTheEditorsCap()
    {
        // find.js stops indexing at 50 000 matches and says so on every answer that
        // carries a count. Nothing read that field: Find reported "Match 1 of 50001"
        // and Replace All changed the part of the document the index reached and
        // called it "Replaced 50001 occurrences" (#5 NF-10).
        Assert.True(FindEngine.ReportsTruncated("{\"total\":50000,\"current\":1,\"truncated\":true}"));
        Assert.True(FindEngine.ReportsTruncated(
            "{\"replaced\":0,\"skipped\":0,\"moved\":0,\"total\":50000,\"inSelection\":false,\"truncated\":true}"));
        Assert.False(FindEngine.ReportsTruncated("{\"total\":3,\"current\":1}"));
        Assert.False(FindEngine.ReportsTruncated("{\"total\":3,\"current\":1,\"truncated\":false}"));
        Assert.False(FindEngine.ReportsTruncated(null));
        Assert.False(FindEngine.ReportsTruncated(""));
        Assert.False(FindEngine.ReportsTruncated("not json at all"));

        // Both messages name the cap, so the number the user is told to narrow below
        // is the number the editor actually applied.
        Assert.Equal(50000, FindEngine.WysiwygMatchLimit);
        Assert.Contains("50000", FindEngine.TruncatedFindNote);
        Assert.Contains("50000", FindEngine.TruncatedReplaceAllMessage);
        Assert.Contains("Nothing was replaced", FindEngine.TruncatedReplaceAllMessage);
        // It follows "Match m of n", which ends without a full stop.
        Assert.StartsWith(" — ", FindEngine.TruncatedFindNote);
    }

    [Fact]
    public void TheSubsetIsWhatReplaceApplies()
    {
        // Not only ExpandTemplate in isolation: the Replace path runs through it too,
        // so Match.Result's wider set ($_ and friends) cannot come back in by the door.
        Assert.Equal("x [$_] y", ReplaceAll("x cat y", "cat", FindEngine.Mode.Regex, "[$_]", matchCase: true));
        Assert.Equal("x [$`][$'] y", ReplaceAll("x cat y", "cat", FindEngine.Mode.Regex, "[$`][$']", matchCase: true));
    }

    [Fact]
    public void TheSelectionFillsFindWhatOnlyWhenItIsOneLineOfAtMostTheLimit()
    {
        Assert.Equal("cat food", FindEngine.SeedQuery("cat food", FindEngine.Mode.Normal));
        Assert.NotNull(FindEngine.SeedQuery(new string('x', 500), FindEngine.Mode.Normal));   // with 501 below, SeedLimit is 500 exactly
        foreach (var leftAlone in new[] { null, "", "one\ntwo", "one\rtwo", new string('x', 501) })
            Assert.Null(FindEngine.SeedQuery(leftAlone, FindEngine.Mode.Normal));
    }

    [Fact]   // a selection on one line, however long, is the whole document: MainWindow and find.js gate the scope on this
    public void OnlyASelectionOverALineBreakLimitsReplaceAll() => Assert.Equal(new[] { false, false, false, false, true, true },
        new[] { null, "", "cat food", new string('x', 501), "one\ntwo", "one\r\ntwo" }.Select(FindEngine.IsReplaceScope));

    [Theory]
    [InlineData(FindEngine.Mode.Normal)][InlineData(FindEngine.Mode.Extended)]
    [InlineData(FindEngine.Mode.Wildcards)][InlineData(FindEngine.Mode.Regex)]
    public void TheSeededQueryFindsTheSelectedTextItselfInEveryMode(FindEngine.Mode mode)
    {
        const string selected = @"a*b?c\nd.(e)[f]{2}^$|+ #g";   // the decoy after it is what this matches read as wildcards; as Extended, \n is a newline
        var regex = FindEngine.Build(FindEngine.SeedQuery(selected, mode)!, mode, matchCase: true, wholeWord: false);   // null: refused, as the formatted view would
        Assert.Equal(new[] { selected }, regex!.Matches(selected + @" aZZbQc\nd.(e)[f]{2}^$|+ #g").Select(m => m.Value));
    }
}
