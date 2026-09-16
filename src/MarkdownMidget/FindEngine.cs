using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarkdownMidget;

/// <summary>
/// Builds a <see cref="Regex"/> from a query + search mode for the Find dialog.
/// Modes mirror Notepad++ where applicable:
///  - <see cref="Mode.Normal"/>: literal substring.
///  - <see cref="Mode.Extended"/>: literal substring with C-style escapes
///    (\n \r \t \0 \\ \xNN \uNNNN). Anything else after a backslash is treated
///    as the literal character (so \. matches '.').
///  - <see cref="Mode.Wildcards"/>: '*' matches any run of characters, '?'
///    matches exactly one. Backslash escapes a literal '*' or '?'.
///  - <see cref="Mode.Regex"/>: the query is .NET regex syntax verbatim.
/// </summary>
public static class FindEngine
{
    public enum Mode { Normal, Extended, Wildcards, Regex }

    public static Regex? Build(string query, Mode mode, bool matchCase, bool wholeWord)
    {
        if (string.IsNullOrEmpty(query)) return null;

        var pattern = mode switch
        {
            Mode.Normal => EscapeLiteral(query),
            Mode.Extended => BuildExtended(query),
            Mode.Wildcards => BuildWildcards(query),
            Mode.Regex => query,
            _ => EscapeLiteral(query),
        };

        if (wholeWord)
            pattern = $@"\b(?:{pattern})\b";

        // Whatever the source view runs, the formatted view runs too. A pattern only
        // one of them understands is refused here, before either sees it (#5 F-6).
        if (!JsCompatible(pattern)) return null;

        var opts = RegexOptions.Compiled | RegexOptions.Multiline;
        if (!matchCase) opts |= RegexOptions.IgnoreCase;

        try { return new Regex(pattern, opts); }
        catch { return null; } // invalid user pattern (esp. in Regex mode)
    }

    /// <summary>
    /// True when <paramref name="pattern"/> means the same thing to the formatted
    /// view's JavaScript engine (which compiles it with the <c>u</c> flag) as it does
    /// to .NET. Conservative on purpose: a construct this does not recognise is
    /// refused rather than allowed to diverge, and over-rejecting says
    /// "<see cref="InvalidPatternMessage"/>" where under-rejecting would quietly
    /// write two different documents.
    ///
    /// Refused: <c>\A</c> <c>\Z</c> <c>\z</c> <c>\G</c> <c>\a</c> <c>\e</c> and every
    /// other escape JavaScript does not have; atomic groups <c>(?&gt;…)</c>; inline
    /// options <c>(?i)</c> / <c>(?i:…)</c>; comment groups <c>(?#…)</c>; conditionals
    /// <c>(?(…)…)</c>; .NET's <c>(?'name'…)</c> and <c>\k'name'</c> spellings;
    /// balancing groups; class subtraction <c>[a-z-[aeiou]]</c>; possessive
    /// quantifiers; Unicode blocks and long category names in <c>\p{…}</c>; and the
    /// loose <c>{</c>, <c>}</c> and <c>]</c> that .NET reads as literal characters and
    /// JavaScript reads as errors.
    ///
    /// Also refused: a <c>\1</c>-style backreference in a pattern where a NAMED group
    /// is written before an unnamed one. Both engines compile such a pattern and each
    /// reads the number as a different group — .NET numbers the unnamed groups first
    /// and the named ones after, JavaScript numbers them all in source order — so
    /// <c>(?&lt;a&gt;x)(y)\1</c> matches "xyy" in the source view and "xyx" in the
    /// formatted one. <c>\k&lt;name&gt;</c> names the same group in both and stays
    /// allowed; so does <c>\1</c> in a pattern whose named groups all come after its
    /// unnamed ones, where the two numberings agree (#5 NF-1). This is the pattern-side
    /// half of what <c>dotnetGroupMap</c> does for replacement templates.
    /// </summary>
    public static bool JsCompatible(string pattern)
    {
        var inClass = false;
        // The two facts that together make a number mean different groups in the two
        // engines. Collected in this one scan rather than a second pass of the same
        // escape/character-class rules, which would be a copy to keep in step.
        var sawNamedGroup = false;
        var namedBeforeUnnamed = false;
        var sawNumberedBackreference = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (!EscapeIsShared(pattern, ref i, inClass, ref sawNumberedBackreference)) return false;
                continue;
            }
            if (inClass)
            {
                if (c == ']') { inClass = false; continue; }
                // "[a-z-[aeiou]]": .NET subtracts the second class, JavaScript unions it.
                if (c == '-' && i + 1 < pattern.Length && pattern[i + 1] == '[') return false;
                continue;
            }
            switch (c)
            {
                case '[':
                    inClass = true;
                    break;
                case ']':
                case '}':
                    return false;          // literal in .NET, a syntax error in JavaScript
                case '{':
                    if (!SkipQuantifier(pattern, ref i)) return false;
                    break;
                case '*':
                case '+':
                case '?':
                    // A '+' after a quantifier is .NET's possessive form; '?' is lazy
                    // and both engines have it.
                    if (i + 1 < pattern.Length && pattern[i + 1] == '+') return false;
                    break;
                case '(':
                    if (!GroupOpeningIsShared(pattern, ref i, ref sawNamedGroup, ref namedBeforeUnnamed))
                        return false;
                    break;
            }
        }
        // A number written for a group the two engines number differently (#5 NF-1).
        // Checked at the end because the backreference may be written before the
        // groups that make it ambiguous — "\1(?<a>x)(y)" is the same divergence.
        if (sawNumberedBackreference && namedBeforeUnnamed) return false;
        return !inClass;
    }

    /// <summary>The escape starting at <paramref name="i"/> (which points at the
    /// backslash), advanced past. False when the two engines do not share it.
    /// <paramref name="sawNumberedBackreference"/> is set when the escape is a
    /// <c>\1</c>-style group number.</summary>
    private static bool EscapeIsShared(string pattern, ref int i, bool inClass, ref bool sawNumberedBackreference)
    {
        if (i + 1 >= pattern.Length) return false;     // a trailing backslash: invalid in both
        var e = pattern[i + 1];
        i++;                                            // the escaped character
        switch (e)
        {
            // The characters JavaScript's unicode mode lets a backslash stand before.
            case '^': case '$': case '\\': case '.': case '*': case '+': case '?':
            case '(': case ')': case '[': case ']': case '{': case '}': case '|': case '/':
            case 'n': case 'r': case 't': case 'f': case 'v':
            case 'd': case 'D': case 's': case 'S': case 'w': case 'W':
                return true;
            case 'b':
                return true;                            // word boundary, or backspace in a class
            case 'B':
                return !inClass;                        // not a class escape in JavaScript
            case '-':
                return inClass;                         // only inside a class
            case '0':
                // NUL, but "\01" is an octal escape in .NET and an error in JavaScript.
                return i + 1 >= pattern.Length || !char.IsAsciiDigit(pattern[i + 1]);
            case 'x':
                return TakeHex(pattern, ref i, 2);
            case 'u':
                return TakeHex(pattern, ref i, 4);      // "\u{1F600}" is JavaScript's alone
            case 'c':
                if (i + 1 >= pattern.Length || !char.IsAsciiLetter(pattern[i + 1])) return false;
                i++;
                return true;
            case 'k':
                return !inClass && TakeGroupName(pattern, ref i, '<', '>');
            case 'p':
            case 'P':
                return TakeUnicodeCategory(pattern, ref i);
            default:
                // \1..\9: a backreference outside a class, an octal escape (.NET) or an
                // error (JavaScript) inside one.
                if (char.IsAsciiDigit(e))
                {
                    if (inClass) return false;
                    while (i + 1 < pattern.Length && char.IsAsciiDigit(pattern[i + 1])) i++;
                    sawNumberedBackreference = true;
                    return true;
                }
                return false;                           // \A \Z \z \G \a \e \Q … — .NET's own
        }
    }

    private static bool TakeHex(string pattern, ref int i, int digits)
    {
        if (i + digits >= pattern.Length) return false;
        for (var k = 1; k <= digits; k++) if (!IsHex(pattern[i + k])) return false;
        i += digits;
        return true;
    }

    /// <summary>A one- or two-letter Unicode general category — the only spelling both
    /// engines read the same way. Blocks (<c>\p{IsGreek}</c>), long names
    /// (<c>\p{Letter}</c>) and <c>Script=…</c> belong to one engine or the other.</summary>
    private static bool TakeUnicodeCategory(string pattern, ref int i)
    {
        if (i + 1 >= pattern.Length || pattern[i + 1] != '{') return false;
        var close = pattern.IndexOf('}', i + 2);
        if (close < 0) return false;
        var name = pattern.AsSpan(i + 2, close - i - 2);
        if (name.Length is < 1 or > 2) return false;
        foreach (var ch in name) if (!char.IsAsciiLetter(ch)) return false;
        i = close;
        return true;
    }

    /// <summary>Reads <c>&lt;name&gt;</c> (or the delimiters given) after
    /// <paramref name="i"/>, requiring a name both engines accept: a letter or
    /// underscore, then letters, digits or underscores. Rules out .NET's balancing
    /// groups (<c>(?&lt;a-b&gt;…)</c>) and numbered group names.</summary>
    private static bool TakeGroupName(string pattern, ref int i, char open, char close)
    {
        if (i + 1 >= pattern.Length || pattern[i + 1] != open) return false;
        var end = pattern.IndexOf(close, i + 2);
        if (end <= i + 2) return false;
        var name = pattern.AsSpan(i + 2, end - i - 2);
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (var ch in name) if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
        i = end;
        return true;
    }

    /// <summary>A <c>{n}</c> / <c>{n,}</c> / <c>{n,m}</c> quantifier, advanced past its
    /// closing brace. False for a loose <c>{</c> (literal in .NET, an error in
    /// JavaScript) and for the possessive <c>{n,m}+</c>.</summary>
    private static bool SkipQuantifier(string pattern, ref int i)
    {
        var j = i + 1;
        var digits = 0;
        while (j < pattern.Length && char.IsAsciiDigit(pattern[j])) { j++; digits++; }
        if (digits == 0) return false;
        if (j < pattern.Length && pattern[j] == ',')
        {
            j++;
            while (j < pattern.Length && char.IsAsciiDigit(pattern[j])) j++;
        }
        if (j >= pattern.Length || pattern[j] != '}') return false;
        if (j + 1 < pattern.Length && pattern[j + 1] == '+') return false;   // possessive
        i = j;
        return true;
    }

    /// <summary>The '(' at <paramref name="i"/> and whatever '?' form follows it.
    /// Ordinary groups, <c>(?:</c>, <c>(?=</c>, <c>(?!</c>, <c>(?&lt;=</c>,
    /// <c>(?&lt;!</c> and <c>(?&lt;name&gt;</c> are shared; every other <c>(?</c>
    /// form is .NET's own. Records the capture order the two engines disagree about:
    /// <paramref name="sawNamedGroup"/> once a named capture has been opened, and
    /// <paramref name="namedBeforeUnnamed"/> when an unnamed one follows it — the
    /// arrangement that makes .NET's group numbers differ from JavaScript's.</summary>
    private static bool GroupOpeningIsShared(string pattern, ref int i, ref bool sawNamedGroup, ref bool namedBeforeUnnamed)
    {
        if (i + 1 >= pattern.Length || pattern[i + 1] != '?')
        {
            if (sawNamedGroup) namedBeforeUnnamed = true;  // a plain capture after a named one
            return true;
        }
        if (i + 2 >= pattern.Length) return false;
        var k = pattern[i + 2];
        if (k is ':' or '=' or '!') { i += 2; return true; }
        if (k != '<') return false;                       // (?> (?# (?i (?' (?( …
        var after = i + 3 < pattern.Length ? pattern[i + 3] : '\0';
        if (after is '=' or '!') { i += 3; return true; } // lookbehind
        i += 1;                                           // at '?', so '<' is next
        if (!TakeGroupName(pattern, ref i, '<', '>')) return false;
        sawNamedGroup = true;
        return true;
    }

    /// <summary>
    /// True when the formatted view answered with the refusal <c>findReset</c> raises
    /// for a pattern its engine will not compile. The host shows
    /// <see cref="InvalidPatternMessage"/> for it, rather than the "No matches found."
    /// a zero count would otherwise read as.
    /// </summary>
    public static bool ReportsInvalidPattern(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String
                && e.GetString() == "Invalid pattern";
        }
        catch { return false; }
    }

    /// <summary>
    /// The most matches the formatted view indexes in one scan — find.js's own cap,
    /// repeated here for the two messages that report it. A pattern matching at every
    /// position (<c>x*</c>, <c>\b</c>) would otherwise build a list as long as the
    /// document. The source view has no such cap: it searches the markdown directly.
    /// </summary>
    public const int WysiwygMatchLimit = 50000;

    /// <summary>Appended to Find's status when the editor's index stopped at the cap:
    /// the count beside it is of the matches it reached, not of the document. Written
    /// to follow "Match m of n", which carries no full stop of its own.</summary>
    public static string TruncatedFindNote => $" — stopped after {WysiwygMatchLimit} matches";

    /// <summary>What Replace All says when it refuses to work from a truncated index.
    /// Changing the part of the document the index reached and reporting the number as
    /// though it were all of them is the outcome this exists to prevent (#5 NF-10).</summary>
    public static string TruncatedReplaceAllMessage =>
        $"Too many matches — the search stopped after {WysiwygMatchLimit}. " +
        "Nothing was replaced; narrow the search and try again.";

    /// <summary>
    /// True when the formatted view answered that its index stopped at the cap. Every
    /// answer carrying a count carries the flag, so whichever call the status is being
    /// built from is the one to ask.
    /// </summary>
    public static bool ReportsTruncated(string? json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.ValueKind == JsonValueKind.Object
                && d.RootElement.TryGetProperty("truncated", out var t)
                && t.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>Documentation for tooltips. Kept in code so it stays in sync.</summary>
    public const string ExtendedTooltip =
        "Treats the query as a literal string, but interprets these C-style escapes:\n" +
        "  \\n  newline\n" +
        "  \\r  carriage return\n" +
        "  \\t  tab\n" +
        "  \\0  null\n" +
        "  \\\\  literal backslash\n" +
        "  \\xNN  character by 2-digit hex code (e.g. \\x20 = space)\n" +
        "  \\uNNNN  Unicode code point by 4-digit hex (e.g. \\u2014 = em dash)\n" +
        "Other characters after \\ are treated as that character literal.";

    public const string WildcardsTooltip =
        "Treats the query as a literal string with two wildcards:\n" +
        "  *  matches any run of characters (including none)\n" +
        "  ?  matches exactly one character\n" +
        "Escape a literal '*', '?', or '\\' by prefixing with '\\'.";

    public const string RegexTooltip =
        "Regular expression syntax. Example patterns:\n" +
        "  ^Title           line starting with 'Title'\n" +
        "  \\b\\d{4}\\b        a four-digit number on a word boundary\n" +
        "  [Hh]ello         'Hello' or 'hello'\n" +
        "  (foo|bar)        'foo' or 'bar'\n" +
        "Groups, backreferences, lookahead and lookbehind all work. A handful of\n" +
        ".NET-only constructs are refused as Invalid pattern, because the formatted\n" +
        "view would read them differently: \\A \\Z \\z \\G, (?>…), inline options\n" +
        "(?i), (?#comments), (?'name'…), \\k'name', class subtraction [a-z-[aeiou]],\n" +
        "possessive quantifiers, and Unicode blocks like \\p{IsGreek}. So is \\1 in a\n" +
        "pattern that writes a named group before an unnamed one — the two views\n" +
        "number the groups differently there; \\k<name> works everywhere.";

    // ===== Replace (#5) =====

    /// <summary>The status shown for a pattern that does not compile — by Find and
    /// by Replace, which refuses the same patterns and applies nothing.</summary>
    public const string InvalidPatternMessage = "Invalid pattern.";

    /// <summary>
    /// The line Replace All reports, in the status bar and in the dialog. There are
    /// two reasons a match is left alone and they are counted apart (#5 F-10):
    /// <paramref name="spanningBlocks"/> ran from one paragraph into the next, which
    /// a replacement would have to join; <paramref name="moved"/> is no longer where
    /// the search left it. Calling the second "they span paragraphs" was simply
    /// untrue, and neither number tells the user anything the other explains.
    /// </summary>
    public static string ReplaceAllStatus(int replaced, bool inSelection, int spanningBlocks, int moved)
    {
        var where = inSelection ? " in the selection" : "";
        var msg = replaced == 0
            ? $"Nothing to replace{where}."
            : $"Replaced {replaced} occurrence{(replaced == 1 ? "" : "s")}{where}.";
        if (spanningBlocks > 0)
            msg += $" {spanningBlocks} left alone: {(spanningBlocks == 1 ? "it spans" : "they span")} paragraphs.";
        if (moved > 0)
            msg += $" {moved} left alone: the text moved.";
        return msg;
    }

    public const int SeedLimit = 500;   // the longest selection that fills Find what

    /// <summary>What Find what becomes when Find or Replace opens on <paramref name="selection"/>, or null to leave it: a caret, a line
    /// break, or over <see cref="SeedLimit"/>. Escaped so <paramref name="mode"/> finds the text itself; Regex by <see cref="EscapeLiteral"/>,
    /// as Normal is, because Regex.Escape writes patterns the formatted view refuses.</summary>
    public static string? SeedQuery(string? selection, Mode mode) =>
        string.IsNullOrEmpty(selection) || selection.Length > SeedLimit || IsReplaceScope(selection) ? null
        : mode switch { Mode.Regex => EscapeLiteral(selection), Mode.Extended => selection.Replace(@"\", @"\\"),
                        Mode.Wildcards => Regex.Replace(selection, @"[\\*?]", @"\$0"), _ => selection };

    /// <summary>Whether the user's selected text limits Replace All: only over a line break. A selection on one line, however long,
    /// means the whole document, as a caret does; it is the kind <see cref="SeedQuery"/> puts in Find what (find.js spansLines).</summary>
    public static bool IsReplaceScope(string? selection) => selection.AsSpan().IndexOfAny('\r', '\n') >= 0;

    /// <summary>What Ctrl+F does with the selection it finds: <see cref="Take"/> it as
    /// the Replace All scope, <see cref="Keep"/> whatever was already kept, or
    /// <see cref="Drop"/> it.</summary>
    public enum ScopeCapture { Drop, Keep, Take }

    /// <summary>
    /// The decision behind MainWindow.CaptureReplaceScope and find.js's
    /// findCaptureScope, in one place so both views make it the same way.
    ///
    /// "Is this Find's own selection?" is asked FIRST: Find's selection of a
    /// zero-width match is a caret, and asking "is it empty?" first read that as the
    /// user having deselected and threw the kept range away (#5 F-5). <paramref name="scopeLength"/> is the
    /// selection's length when <see cref="IsReplaceScope"/>, and 0 otherwise, in both methods.
    /// </summary>
    public static ScopeCapture CaptureDecision(int scopeLength, bool isFindSelection)
        => isFindSelection ? ScopeCapture.Keep
         : scopeLength <= 0 ? ScopeCapture.Drop
         : ScopeCapture.Take;

    /// <summary>
    /// The Replace All scope: the user's own selected text over a line break, or — when the selection is
    /// Find's own, which it is as soon as a search has run — the range kept when the
    /// dialog opened. Null means the whole document. Same reason as
    /// <see cref="CaptureDecision"/> for the order of the tests.
    /// </summary>
    public static (int Start, int Length)? ResolveScope(
        int selectionStart, int scopeLength, bool isFindSelection, (int Start, int Length)? captured)
    {
        if (!isFindSelection)
            return scopeLength > 0 ? (selectionStart, scopeLength) : null;
        return captured is { Length: > 0 } c ? c : null;
    }

    /// <summary>One replacement: the <see cref="Length"/> characters at
    /// <see cref="Index"/> become <see cref="Text"/>. Indices are into the text the
    /// edits were planned for; a view applies them last to first so earlier indices
    /// stay valid.</summary>
    public readonly record struct Edit(int Index, int Length, string Text);

    /// <summary>
    /// A query and its replacement prepared for one search mode. <see cref="Literal"/>
    /// says whether <see cref="Replacement"/> is inserted verbatim (Normal, Wildcards,
    /// and Extended once its escapes are expanded) or is a replacement template
    /// (Regex): <c>$1</c>…<c>$99</c>, <c>${name}</c>, <c>$0</c>/<c>$&amp;</c>,
    /// <c>$$</c> — and nothing else, every other <c>$</c> form being literal. See
    /// <see cref="ExpandTemplate"/> for the whole subset and why it is not
    /// <see cref="Match.Result"/>. The formatted view is handed the same two values
    /// and expands the same subset, over the same group numbers.
    /// </summary>
    public sealed record ReplaceSpec(Regex Regex, string Replacement, bool Literal)
    {
        /// <summary>The text one match becomes.</summary>
        public string ReplacementFor(Match m) => Literal ? Replacement : ExpandTemplate(Replacement, m);

        /// <summary>
        /// Plans Replace All over <paramref name="text"/>: one edit per match, in
        /// document order. With a scope, only matches lying wholly inside
        /// [<paramref name="scopeStart"/>, <paramref name="scopeStart"/> +
        /// <paramref name="scopeLength"/>) are planned — a match cut by the scope's
        /// edge is outside it. A negative <paramref name="scopeLength"/> means the
        /// whole text. Zero-length matches are planned too (they insert), which is
        /// what <see cref="Regex.Replace(string, string)"/> does with them.
        /// </summary>
        public IReadOnlyList<Edit> ReplaceAllEdits(string text, int scopeStart = 0, int scopeLength = -1)
        {
            var scopeEnd = scopeLength < 0 ? text.Length : scopeStart + scopeLength;
            var edits = new List<Edit>();
            foreach (Match m in Regex.Matches(text))
            {
                if (m.Index < scopeStart || m.Index + m.Length > scopeEnd) continue;
                edits.Add(new Edit(m.Index, m.Length, ReplacementFor(m)));
            }
            return edits;
        }
    }

    /// <summary>
    /// Builds the regex for the query (as <see cref="Build"/> does) and readies the
    /// replacement for the mode. Null under exactly the conditions Find reports
    /// "Type to search." or <see cref="InvalidPatternMessage"/>: an empty query or a
    /// pattern that does not compile. Nothing can be applied from a null.
    /// </summary>
    public static ReplaceSpec? Prepare(string query, Mode mode, bool matchCase, bool wholeWord, string replacement)
    {
        var regex = Build(query, mode, matchCase, wholeWord);
        if (regex is null) return null;
        return mode switch
        {
            Mode.Regex => new ReplaceSpec(regex, replacement, Literal: false),
            Mode.Extended => new ReplaceSpec(regex, UnescapeExtended(replacement), Literal: true),
            _ => new ReplaceSpec(regex, replacement, Literal: true),
        };
    }

    /// <summary>
    /// Expands a Regex-mode replacement template against one match, in the one subset
    /// the formatted view also implements (find.js <c>expandTemplate</c>), pinned for
    /// both by <c>editor-src/test/fixtures/replace-templates.json</c>:
    /// <list type="bullet">
    ///   <item><c>$$</c> — a dollar sign.</item>
    ///   <item><c>$&amp;</c> and <c>$0</c> — the whole match.</item>
    ///   <item><c>$1</c>…<c>$99</c> — a capture group: two digits when they name a
    ///     group that exists, otherwise one digit and the rest of the number literal.</item>
    ///   <item><c>${name}</c> — a named group, or a group number spelled in braces.</item>
    /// </list>
    /// Anything else after a <c>$</c> is literal — <c>$&lt;name&gt;</c>, <c>$`</c>,
    /// <c>$'</c>, <c>$+</c>, <c>$_</c> and a <c>$</c> at the end of the template
    /// included. A group that did not take part is empty.
    ///
    /// Deliberately not <see cref="Match.Result"/>: that implements .NET's full set,
    /// which the formatted view's JavaScript engine has no equivalent of, and the two
    /// views would then write different documents for the same replacement.
    /// </summary>
    public static string ExpandTemplate(string template, Match m)
    {
        var groupCount = m.Groups.Count - 1;         // group 0 is the whole match
        var sb = new StringBuilder(template.Length + 16);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c != '$' || i + 1 >= template.Length) { sb.Append(c); continue; }
            var n = template[i + 1];
            if (n == '$') { sb.Append('$'); i++; continue; }
            if (n == '&') { sb.Append(m.Value); i++; continue; }
            if (n == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end > i + 2 && TryGroup(m, template.AsSpan(i + 2, end - i - 2), groupCount, out var named))
                {
                    sb.Append(named);
                    i = end;
                    continue;
                }
                sb.Append(c);
                continue;
            }
            if (char.IsAsciiDigit(n))
            {
                // Two digits when they name a group that exists, else one, else literal.
                if (i + 2 < template.Length && char.IsAsciiDigit(template[i + 2]))
                {
                    var two = (n - '0') * 10 + (template[i + 2] - '0');
                    if (two <= groupCount) { sb.Append(m.Groups[two].Value); i += 2; continue; }
                }
                var one = n - '0';
                if (one <= groupCount) { sb.Append(m.Groups[one].Value); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The group <paramref name="name"/> names, by name or by number.
    /// False — so the template text stays literal — when nothing does.</summary>
    private static bool TryGroup(Match m, ReadOnlySpan<char> name, int groupCount, out string value)
    {
        value = "";
        if (name.IsEmpty) return false;
        var allDigits = true;
        foreach (var ch in name) if (!char.IsAsciiDigit(ch)) { allDigits = false; break; }
        if (allDigits)
        {
            if (!int.TryParse(name, out var n) || n < 0 || n > groupCount) return false;
            value = m.Groups[n].Value;
            return true;
        }
        if (!m.Groups.TryGetValue(name.ToString(), out var g)) return false;
        value = g.Value;
        return true;
    }

    /// <summary>
    /// Applies planned edits to a string, last to first — the reference the views
    /// mirror (the source view runs the same order through its document; the
    /// formatted view through one ProseMirror transaction).
    /// </summary>
    public static string ApplyEdits(string text, IReadOnlyList<Edit> edits)
    {
        var sb = new StringBuilder(text);
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var e = edits[i];
            sb.Remove(e.Index, e.Length).Insert(e.Index, e.Text);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The Extended-mode escapes, applied to a replacement: the same set the query
    /// side recognises (\n \r \t \0 \\ \xNN \uNNNN), with any other character after a
    /// backslash standing for itself and a trailing backslash kept. Produces the
    /// characters themselves, where <see cref="BuildExtended"/> produces regex text.
    /// </summary>
    public static string UnescapeExtended(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            var nxt = s[i + 1];
            switch (nxt)
            {
                case 'n': sb.Append('\n'); i++; continue;
                case 'r': sb.Append('\r'); i++; continue;
                case 't': sb.Append('\t'); i++; continue;
                case '0': sb.Append('\0'); i++; continue;
                case '\\': sb.Append('\\'); i++; continue;
                case 'x':
                    if (i + 3 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]))
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 2), 16));
                        i += 3; continue;
                    }
                    break;
                case 'u':
                    if (i + 5 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]) && IsHex(s[i + 5]))
                    {
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                        i += 5; continue;
                    }
                    break;
            }
            // Unknown escape — the following character itself.
            sb.Append(nxt);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a literal so that it matches itself in BOTH engines. .NET's own
    /// <see cref="Regex.Escape(string)"/> writes <c>"\ "</c> for a space and
    /// <c>"\#"</c> for a hash; the formatted view compiles its regex with JavaScript's
    /// unicode flag, which calls both of those a syntax error — so a Normal-mode
    /// search for two words would stop working there. This escapes only what one of
    /// the two engines calls syntax (adding <c>]</c> and <c>}</c>, which .NET leaves
    /// loose and JavaScript will not), and writes control characters as the escapes
    /// both spell the same way.
    /// </summary>
    private static string EscapeLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s) AppendLiteral(sb, c);
        return sb.ToString();
    }

    private static void AppendLiteral(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '\\': case '^': case '$': case '.': case '|': case '?':
            case '*': case '+': case '(': case ')': case '[': case ']':
            case '{': case '}':
                sb.Append('\\').Append(c);
                return;
            case '\n': sb.Append("\\n"); return;
            case '\r': sb.Append("\\r"); return;
            case '\t': sb.Append("\\t"); return;
            case '\f': sb.Append("\\f"); return;
            case '\v': sb.Append("\\v"); return;
        }
        if (c < ' ' || c == '\u007f')
            sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        else
            sb.Append(c);
    }

    private static string BuildExtended(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var nxt = s[i + 1];
                switch (nxt)
                {
                    case 'n': sb.Append("\\n"); i++; continue;
                    case 'r': sb.Append("\\r"); i++; continue;
                    case 't': sb.Append("\\t"); i++; continue;
                    case '0': sb.Append("\\u0000"); i++; continue;
                    case '\\': sb.Append("\\\\"); i++; continue;
                    case 'x':
                        if (i + 3 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]))
                        {
                            sb.Append("\\x").Append(s, i + 2, 2);
                            i += 3; continue;
                        }
                        break;
                    case 'u':
                        if (i + 5 < s.Length && IsHex(s[i + 2]) && IsHex(s[i + 3]) && IsHex(s[i + 4]) && IsHex(s[i + 5]))
                        {
                            sb.Append("\\u").Append(s, i + 2, 4);
                            i += 5; continue;
                        }
                        break;
                }
                // Unknown escape — treat the following char as a literal.
                AppendLiteral(sb, nxt);
                i++;
                continue;
            }
            AppendLiteral(sb, c);
        }
        return sb.ToString();
    }

    private static string BuildWildcards(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && (s[i + 1] is '*' or '?' or '\\'))
            {
                AppendLiteral(sb, s[i + 1]);
                i++;
                continue;
            }
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: AppendLiteral(sb, c); break;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
