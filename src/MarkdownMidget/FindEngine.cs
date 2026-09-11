using System.Text;
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
            Mode.Normal => Regex.Escape(query),
            Mode.Extended => BuildExtended(query),
            Mode.Wildcards => BuildWildcards(query),
            Mode.Regex => query,
            _ => Regex.Escape(query),
        };

        if (wholeWord)
            pattern = $@"\b(?:{pattern})\b";

        var opts = RegexOptions.Compiled | RegexOptions.Multiline;
        if (!matchCase) opts |= RegexOptions.IgnoreCase;

        try { return new Regex(pattern, opts); }
        catch { return null; } // invalid user pattern (esp. in Regex mode)
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
        ".NET regular expression syntax. Example patterns:\n" +
        "  ^Title           line starting with 'Title'\n" +
        "  \\b\\d{4}\\b        a four-digit number on a word boundary\n" +
        "  [Hh]ello         'Hello' or 'hello'\n" +
        "  (foo|bar)        'foo' or 'bar'";

    // ===== Replace (#5) =====

    /// <summary>The status shown for a pattern that does not compile — by Find and
    /// by Replace, which refuses the same patterns and applies nothing.</summary>
    public const string InvalidPatternMessage = "Invalid pattern.";

    /// <summary>One replacement: the <see cref="Length"/> characters at
    /// <see cref="Index"/> become <see cref="Text"/>. Indices are into the text the
    /// edits were planned for; a view applies them last to first so earlier indices
    /// stay valid.</summary>
    public readonly record struct Edit(int Index, int Length, string Text);

    /// <summary>
    /// A query and its replacement prepared for one search mode. <see cref="Literal"/>
    /// says whether <see cref="Replacement"/> is inserted verbatim (Normal, Wildcards,
    /// and Extended once its escapes are expanded) or is a .NET replacement pattern
    /// (Regex: <c>$1</c>, <c>${name}</c>, <c>$0</c>/<c>$&amp;</c>, <c>$$</c>). The
    /// formatted view is handed the same two values and its own expander mirrors the
    /// Regex forms named here.
    /// </summary>
    public sealed record ReplaceSpec(Regex Regex, string Replacement, bool Literal)
    {
        /// <summary>The text one match becomes.</summary>
        public string ReplacementFor(Match m) => Literal ? Replacement : m.Result(Replacement);

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
                sb.Append(Regex.Escape(nxt.ToString()));
                i++;
                continue;
            }
            sb.Append(Regex.Escape(c.ToString()));
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
                sb.Append(Regex.Escape(s[i + 1].ToString()));
                i++;
                continue;
            }
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
}
