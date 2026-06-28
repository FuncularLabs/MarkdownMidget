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
