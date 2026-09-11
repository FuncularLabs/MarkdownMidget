using System;
using System.IO;
using System.Linq;
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
}
