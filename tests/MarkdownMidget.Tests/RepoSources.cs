using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Reading the repository's own sources, for the pins that cover what no test can
/// run: the window's handlers (they need a real window) and the names the host and
/// the editor have to spell the same way in two languages.
///
/// A scan proves a call is THERE — never what it does. The behaviour is tested
/// where it lives, and a scan that stood in for that would go green on the opposite
/// of production.
/// </summary>
internal static class RepoSources
{
    /// <summary>The repository, found from the test assembly rather than from the
    /// working directory: `dotnet test` runs from the repo root in CI and from
    /// wherever the developer happens to be locally.</summary>
    public static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("MarkdownMidget.sln*").Any()
                || (dir.EnumerateDirectories("src").Any() && dir.EnumerateFiles("HELP.md").Any()))
                return dir.FullName;
        throw new InvalidOperationException(
            $"No MarkdownMidget.sln[x] (or src/ beside HELP.md) above {AppContext.BaseDirectory}");
    }

    /// <summary>One repository file's text, by path segments below the root.</summary>
    public static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(Root(), Path.Combine(path)));

    /// <summary>A method's text, from its signature to the closing brace at member
    /// indentation.</summary>
    public static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no \"{signature}\" in the source");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no end to \"{signature}\"");
        return source[start..end];
    }

    /// <summary>One switch case's body: from its label to the <c>break;</c> that ends
    /// it — so a pin says the call is inside THAT arm, not merely somewhere in a file
    /// of five thousand lines.</summary>
    public static string CaseBody(string source, string label)
    {
        var start = source.IndexOf(label, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no \"{label}\" in the source");
        var end = source.IndexOf("break;", start, StringComparison.Ordinal);
        Assert.True(end > start, $"no break after \"{label}\"");
        return source[start..end];
    }

    /// <summary>
    /// C# code with its <c>//</c> and <c>/* */</c> comments taken out and everything
    /// else left exactly where it was, line breaks included. A pin that searches the
    /// raw text is satisfied by a call that has been commented out, and a pin that
    /// asks what sits between two statements is defeated by the comment that explains
    /// them; this is the text such a pin asks instead. Comment markers inside string
    /// and character literals — a URL, <c>"//"</c> — are text and stay. Not
    /// understood: raw string literals (<c>"""</c>) and a quote nested inside an
    /// interpolation hole; the method bodies this is read over have neither.
    /// </summary>
    public static string WithoutComments(string code)
    {
        var sb = new StringBuilder(code.Length);
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;   // the line break itself stays
                continue;
            }
            if (c == '/' && next == '*')
            {
                var close = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = close < 0 ? code.Length : close + 2;
                for (var k = i; k < stop; k++)
                    if (code[k] == '\n') sb.Append('\n');
                i = stop;
                continue;
            }
            if (c is '"' or '\'')
            {
                var end = LiteralEnd(code, i);
                sb.Append(code, i, end - i);
                i = end;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>How many braces are open at <paramref name="index"/> in comment-free
    /// code (<see cref="WithoutComments"/>), braces inside string and character
    /// literals not counted. Read over a <see cref="MethodBody"/>, 1 is the method's
    /// own level: a statement there is not inside any <c>if</c>, <c>try</c> or loop.</summary>
    public static int BraceDepth(string code, int index)
    {
        var depth = 0;
        var i = 0;
        while (i < index && i < code.Length)
        {
            var c = code[i];
            if (c is '"' or '\'') { i = LiteralEnd(code, i); continue; }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            i++;
        }
        return depth;
    }

    /// <summary>The index just past the string or character literal opening at
    /// <paramref name="start"/>: backslash escapes in a regular literal, a doubled
    /// quote in a verbatim one (<c>@"</c>, <c>$@"</c>, <c>@$"</c>).</summary>
    private static int LiteralEnd(string code, int start)
    {
        var quote = code[start];
        var verbatim = quote == '"' && start > 0
            && (code[start - 1] == '@' || (code[start - 1] == '$' && start > 1 && code[start - 2] == '@'));
        var i = start + 1;
        while (i < code.Length)
        {
            if (!verbatim && code[i] == '\\') { i += 2; continue; }
            if (code[i] == quote)
            {
                if (verbatim && i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return code.Length;
    }
}
