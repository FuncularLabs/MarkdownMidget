using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Why <paramref name="statement"/> might not run every time the method does, or null
    /// when nothing in the method's text can get round it. "The call is there, in this
    /// order" is not "the call runs": a braceless <c>if</c> on the line above, an early
    /// <c>return</c>, a <c>goto</c> past it or an <c>#if</c> around it all leave a pin
    /// green on code that never runs. Read over a <see cref="MethodBody"/>, with comments
    /// out (<see cref="WithoutComments"/>) and the inside of every literal blanked, this
    /// asks for: no preprocessor directive in the method; the statement exactly once; at
    /// brace depth 1, the method's own level; first on its line, under a code line ending
    /// in <c>;</c>, <c>{</c> or <c>}</c>; and no <c>return</c>, <c>goto</c>, <c>break</c>,
    /// <c>continue</c> or <c>throw</c> before it.
    ///
    /// <paramref name="after"/> asks the weaker question a method with its own early
    /// returns can answer: "once THIS has run, does the statement always follow?" — the
    /// ways out before the anchor are then the method's business, not the pin's.
    ///
    /// Conservative on purpose: a <c>return</c> that only ends a lambda skips nothing,
    /// and this reading cannot tell, so it refuses and says which shape it cannot rule
    /// out. It proves a path, never behaviour.
    /// </summary>
    public static string? WhyNotUnconditional(string methodBody, string statement, string? after = null)
    {
        var code = WithoutComments(methodBody);
        var bare = WithoutLiterals(code);   // same length: an index into one is an index into the other

        var directive = Regex.Match(bare, @"^[ \t]*#", RegexOptions.Multiline);
        if (directive.Success)
            return $"the method has a preprocessor directive (`{bare[directive.Index..].Split('\n')[0].Trim()}`): the "
                 + $"text cannot say which copy compiles, so nothing here can show `{statement}` runs. Take the "
                 + "directive out of the method.";

        var at = OnlyIndexOf(code, statement, out var problem);
        if (problem is not null) return problem;

        var scanFrom = 0;
        if (after is not null)
        {
            var anchor = OnlyIndexOf(code, after, out var anchorProblem);
            if (anchorProblem is not null) return anchorProblem;
            if (anchor >= at)
                return $"`{statement}` does not come after `{after}` in the method, so it cannot be what follows it.";
            var anchorDepth = BraceDepth(code, anchor);
            if (anchorDepth != 1)
                return $"the anchor `{after}` sits at brace depth {anchorDepth}, not the method's own level (1), so "
                     + "what follows it is not the method's own path.";
            scanFrom = anchor + after.Length;
        }

        var depth = BraceDepth(code, at);
        if (depth != 1)
            return $"`{statement}` sits at brace depth {depth}, not the method's own level (1). An if, else, loop, "
                 + "switch or catch around it makes it conditional, which is what this refuses. If the block around "
                 + "it always runs (a try, using, lock or a bare block), move the statement out to the method's own "
                 + "level: this reading does not follow blocks.";

        var lineStart = code.LastIndexOf('\n', at) + 1;
        if (bare[lineStart..at].Trim().Length > 0)
            return $"`{statement}` is not first on its line: `{code[lineStart..at].Trim()}` sits in front of it there "
                 + "and may own it (a braceless if or else). Give the statement a line of its own.";

        var bareLines = bare[..lineStart].Split('\n');
        var codeLines = code[..lineStart].Split('\n');
        var p = Array.FindLastIndex(bareLines, l => l.Trim().Length > 0);
        var previous = p >= 0 ? codeLines[p].Trim() : string.Empty;
        if (!previous.EndsWith(';') && !previous.EndsWith('{') && !previous.EndsWith('}'))
            return $"the code line before `{statement}` is `{previous}`, which does not end in `;`, `{{` or `}}`: a "
                 + "braceless if, else, while, for, foreach, using or lock there owns the statement, or an expression "
                 + "runs on into it.";

        var jump = Regex.Match(bare[scanFrom..at], @"\b(return|goto|break|continue|throw)\b");
        if (jump.Success)
            return $"`{jump.Value}` comes before `{statement}`{(after is null ? "" : $", after `{after}`")}, so a path "
                 + "can leave or jump past it. If that one only ends a lambda, a local function, a switch or a loop "
                 + "it skips nothing — this reading cannot tell those apart, so it refuses rather than guess. Move "
                 + "the lambda below the statement, or pin from an anchor that follows the way out.";
        return null;
    }

    /// <summary>Assert <paramref name="statement"/> runs every time the method does (or,
    /// with <paramref name="after"/>, every time that anchor has run), with the reason as
    /// the failure message.</summary>
    public static void AssertRunsUnconditionally(string methodBody, string statement, string? after = null)
    {
        var why = WhyNotUnconditional(methodBody, statement, after);
        Assert.True(why is null, why);
    }

    /// <summary>The one index of <paramref name="what"/>, or -1 with
    /// <paramref name="problem"/> saying whether it is missing or repeated.</summary>
    private static int OnlyIndexOf(string code, string what, out string? problem)
    {
        var at = code.IndexOf(what, StringComparison.Ordinal);
        if (at < 0)
        {
            problem = $"`{what}` is not in the method's code (a copy inside a comment is not code).";
            return -1;
        }
        if (code.IndexOf(what, at + 1, StringComparison.Ordinal) >= 0)
        {
            problem = $"`{what}` appears more than once in the method, so this cannot say which copy runs. Pin a "
                    + "longer, unique statement.";
            return -1;
        }
        problem = null;
        return at;
    }

    /// <summary>The code with the inside of every string and character literal blanked and
    /// everything else, line breaks included, where it was — so a <c>return</c> in a
    /// message, or a markdown heading's <c>#</c> in a verbatim string, is not read as
    /// code. Same length as the input.</summary>
    private static string WithoutLiterals(string code)
    {
        var chars = code.ToCharArray();
        var i = 0;
        while (i < code.Length)
        {
            if (code[i] is '"' or '\'')
            {
                var end = LiteralEnd(code, i);
                for (var k = i + 1; k < end - 1 && k < chars.Length; k++)
                    if (chars[k] != '\n') chars[k] = ' ';
                i = end;
                continue;
            }
            i++;
        }
        return new string(chars);
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
