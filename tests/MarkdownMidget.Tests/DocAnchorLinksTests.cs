using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Every in-repo anchor link the docs write must land on a heading that is really
/// there. `](HELP.md#known-limits)` and `](#markdown-conventions)` are the shape:
/// a same-file jump, or a jump into another markdown file in this repository.
///
/// Why this exists: an anchor is prose with a URL's syntax. Rename or move a
/// heading and every link into it silently becomes a link to the top of the page —
/// no build break, no test failure, and a reader who clicks "see Known limits"
/// lands on the title and concludes the section was cut. The 1.0 docs lean on these
/// cross-references (README points at HELP's Markdown conventions and Known limits;
/// Help's Known limits points back into four of its own sections), so the plan's
/// L1/L2 promises are only as good as the links that carry them.
///
/// Slugs follow GitHub's rule for markdown headings: lowercase, every character
/// that is not a letter, digit, hyphen or underscore dropped, spaces turned into
/// hyphens, and a repeated heading numbered `-1`, `-2` after the first.
///
/// What it does NOT check: links that leave the repository (http/https), links to a
/// file with no `#` in them, and anything inside a fenced code block, which is a
/// sample and not a claim. An anchor written inside a single-backtick code span on
/// a prose line is still read as a link — no doc here does that, and pretending to
/// parse inline code would cost more than it protects.
/// </summary>
public class DocAnchorLinksTests
{
    /// <summary>The files whose links are claims about today's docs. ROADMAP is in
    /// because its two decision links are navigated exactly as often as Help's.</summary>
    private static readonly string[] LinkingDocNames = ["HELP.md", "README.md", "CHANGELOG.md", "ROADMAP.md"];

    public static TheoryData<string> LinkingDocs
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in LinkingDocNames) data.Add(name);
            return data;
        }
    }

    // ===== the repository =====

    /// <summary>
    /// The repository, found from the test assembly rather than the working
    /// directory, for the reason MenuPathsInDocsTests gives: `dotnet test` runs from
    /// the repo root in CI and from wherever the developer happens to be locally.
    /// </summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("MarkdownMidget.sln*").Any()
                || (dir.EnumerateDirectories("src").Any() && dir.EnumerateFiles("HELP.md").Any()))
                return dir.FullName;
        throw new InvalidOperationException(
            $"No MarkdownMidget.sln[x] (or src/ beside HELP.md) above {AppContext.BaseDirectory}");
    }

    // ===== slugs =====

    /// <summary>
    /// GitHub's anchor for one heading's text. The markup characters a heading
    /// carries — `#`, `**`, backticks, an em dash — are all "not a letter, digit,
    /// hyphen or underscore", so dropping that class handles them all at once; the
    /// SPACES around a dropped character survive as hyphens, which is why
    /// "Markdown Midget — Help" is `markdown-midget--help` and not `...-help`.
    /// </summary>
    private static string Slug(string headingText)
    {
        var sb = new StringBuilder(headingText.Length);
        foreach (var c in headingText.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
            else if (c == ' ') sb.Append('-');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Every anchor one file offers. Fenced blocks are skipped: README's publish
    /// sample contains a `# -> src/...` shell comment, which is not a heading.
    /// </summary>
    private static HashSet<string> AnchorsOf(IEnumerable<string> lines)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in WithoutFencedBlocks(lines))
        {
            if (Regex.Match(line, @"^(#{1,6})\s+(.*)$") is not { Success: true } h) continue;
            // A closing run of hashes is decoration, not part of the name.
            var slug = Slug(Regex.Replace(h.Groups[2].Value, @"\s*#+\s*$", ""));
            if (slug.Length == 0) continue;
            var count = seen.TryGetValue(slug, out var n) ? n : 0;
            seen[slug] = count + 1;
            anchors.Add(count == 0 ? slug : $"{slug}-{count}");
        }
        return anchors;
    }

    /// <summary>The lines outside ``` / ~~~ fences, blanked rather than removed so
    /// line numbers keep counting.</summary>
    private static IEnumerable<string> WithoutFencedBlocks(IEnumerable<string> lines)
    {
        string? fence = null;
        foreach (var line in lines)
        {
            var open = Regex.Match(line, @"^\s{0,3}(`{3,}|~{3,})");
            if (fence is null)
            {
                if (open.Success) { fence = open.Groups[1].Value[..1]; yield return ""; continue; }
                yield return line;
            }
            else
            {
                if (open.Success && open.Groups[1].Value.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                yield return "";
            }
        }
    }

    // ===== the scan =====

    private readonly record struct AnchorLink(int Line, string Target, string Anchor, string Raw);

    /// <summary>
    /// Every `](target#anchor)` in one file. `target` is empty for a same-file jump.
    /// An off-machine link (`https://…#fragment`) is somebody else's page and not
    /// this test's business.
    /// </summary>
    private static List<AnchorLink> LinksIn(IReadOnlyList<string> lines)
    {
        var links = new List<AnchorLink>();
        var scannable = WithoutFencedBlocks(lines).ToList();
        for (var i = 0; i < scannable.Count; i++)
            foreach (Match m in Regex.Matches(scannable[i], @"\]\(([^)#\s]*)#([^)\s]+)\)"))
            {
                var target = m.Groups[1].Value;
                if (Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.\-]*:") || target.StartsWith("//", StringComparison.Ordinal))
                    continue;   // http:, https:, mailto:, //host/… — off this repo
                links.Add(new AnchorLink(i + 1, target, m.Groups[2].Value, m.Value));
            }
        return links;
    }

    /// <summary>
    /// Every link in <paramref name="file"/> whose anchor names no heading in its
    /// target. <paramref name="read"/> returns the target file's lines, or null when
    /// there is no such file — which is itself a finding, not a pass.
    /// </summary>
    private static List<string> Violations(string file, IReadOnlyList<string> lines,
                                           Func<string, IReadOnlyList<string>?> read)
    {
        var found = new List<string>();
        foreach (var link in LinksIn(lines))
        {
            var targetName = link.Target.Length == 0 ? file : link.Target;
            if (read(targetName) is not { } targetLines)
            {
                found.Add($"{file}({link.Line}): {link.Raw} — there is no {targetName} to link into.");
                continue;
            }
            var anchors = AnchorsOf(targetLines);
            if (anchors.Contains(link.Anchor)) continue;
            var near = anchors.Where(a => a.StartsWith(link.Anchor[..Math.Min(6, link.Anchor.Length)], StringComparison.Ordinal))
                              .OrderBy(a => a, StringComparer.Ordinal).Take(3).ToArray();
            found.Add($"{file}({link.Line}): {link.Raw} — {targetName} has no heading whose anchor is " +
                      $"\"#{link.Anchor}\"." + (near.Length > 0 ? $" Nearest: {string.Join(", ", near.Select(a => "#" + a))}" : ""));
        }
        return found;
    }

    private static Func<string, IReadOnlyList<string>?> FromRepo(string root) => name =>
    {
        // Relative to the repo root, because every linking file scanned here sits at
        // it — which is also what makes a `docs/plans/release-1.0.md#…` target work.
        var path = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? File.ReadAllLines(path)
            : null;
    };

    // ===== the pin =====

    [Theory]
    [MemberData(nameof(LinkingDocs))]
    public void EveryAnchorLinkLandsOnAHeadingThatExists(string docName)
    {
        var root = RepoRoot();
        var lines = File.ReadAllLines(Path.Combine(root, docName));

        var violations = Violations(docName, lines, FromRepo(root));

        Assert.True(violations.Count == 0,
            $"{violations.Count} anchor link(s) point at nothing:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheScanActuallyFindsTheLinksItIsCheckingAndCanFailOnOne()
    {
        // Two vacuity guards in one. First: the theory above passes trivially if the
        // regex stops matching, so the real files must yield real links.
        var root = RepoRoot();
        var counted = LinkingDocNames
            .Sum(name => LinksIn(File.ReadAllLines(Path.Combine(root, name))).Count);
        Assert.True(counted >= 8, $"only {counted} anchor link(s) found across the docs — the scan has stopped seeing them");

        // Second: a bad anchor is reported. Both spellings, because they fail
        // differently — a heading that was renamed, and a file that isn't there.
        IReadOnlyList<string>? Read(string name) => name == "HELP.md" ? new[] { "# Title", "## Known limits" } : null;

        var renamed = Assert.Single(Violations(
            "README.md", ["see [limits](HELP.md#known-limitations) for the list."], Read));
        Assert.Contains("HELP.md has no heading whose anchor is \"#known-limitations\"", renamed);
        Assert.Contains("#known-limits", renamed);   // and the nearest one it does have

        var missingFile = Assert.Single(Violations(
            "README.md", ["see [gone](docs/NOPE.md#anything)."], Read));
        Assert.Contains("there is no docs/NOPE.md to link into", missingFile);

        // The same link spelled right is not a finding, in either direction.
        Assert.Empty(Violations("README.md", ["see [limits](HELP.md#known-limits)."], Read));
        Assert.Empty(Violations("HELP.md", ["and [above](#known-limits)."], Read));
    }

    [Theory]
    [InlineData("Known limits", "known-limits")]
    [InlineData("Markdown Midget — Help", "markdown-midget--help")]          // em dash goes; its two spaces stay
    [InlineData("Secure Markdown (encrypted documents)", "secure-markdown-encrypted-documents")]
    [InlineData("Distribution (single-file builds)", "distribution-single-file-builds")]
    [InlineData("Formatting marks (¶)", "formatting-marks-")]                // trailing hyphen is GitHub's too
    [InlineData("Host ↔ editor bridge (`window.MDM`)", "host--editor-bridge-windowmdm")]
    [InlineData("Decided 2026-08-13 — how a portable sibling learns it is superseded",
                "decided-2026-08-13--how-a-portable-sibling-learns-it-is-superseded")]
    public void ASlugIsWhatGitHubWouldMake(string heading, string slug)
        => Assert.Equal(slug, Slug(heading));

    [Fact]
    public void ARepeatedHeadingGetsGitHubsNumberedAnchors()
        // Every doc here has a "Distribution", and CHANGELOG repeats section names
        // per release; the second one is #x-1, not a second #x.
        => Assert.Equal(
            new[] { "fixed", "fixed-1", "fixed-2" },
            AnchorsOf(["### Fixed", "### Fixed", "### Fixed"]).OrderBy(a => a, StringComparer.Ordinal).ToArray());

    [Fact]
    public void AHashInsideAFencedBlockIsNotAHeading()
        // README's publish sample has a `# -> src/...` shell comment in a fence, and
        // a link written in a sample is a sample too.
        => Assert.Empty(Violations("README.md",
            ["```bash", "# Not a heading", "see [x](#not-a-heading)", "```"], _ => null));
}
