using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Every "Top ▸ Item" chain the current docs write must name an item that really
/// sits under that menu.
///
/// Why this exists: "View ▸ Read Only" appeared in six places while the item lives
/// under Edit. Nothing caught it — a menu path is prose, and prose is not compiled.
/// It is the cheapest kind of documentation lie to write and the most annoying to
/// hit, because the reader goes looking in the wrong menu and concludes the feature
/// is missing.
///
/// What it checks: HELP.md and README.md in full, and CHANGELOG.md's [Unreleased]
/// section plus the newest dated one. OLDER CHANGELOG SECTIONS ARE EXEMPT ON
/// PURPOSE: they describe the menus as they were at that release ("Settings moved
/// to the Edit menu"), and rewriting history to match today's menu bar would make
/// them false rather than true.
///
/// How much of a chain it checks: the top menu and the item under it, always, and
/// each further level whose parent is a real submenu. Three deliberate tolerances:
/// a chain whose first segment is not a top-level menu is not a menu path at all
/// ("Right-click a picture ▸ Resize…", "Modified state, undo, and saving ▸ Markdown
/// conventions") and is skipped; a segment after a LEAF item is something inside the
/// dialog that item opens ("Edit ▸ Settings… ▸ Import words from Word's custom
/// dictionary"), which this test knows nothing about; and two submenus are filled at
/// runtime — View ▸ Theme from the theme files on disk and File ▸ Open Recent from
/// the user's history — so an unknown name under those two is allowed.
/// </summary>
public class MenuPathsInDocsTests
{
    private const char Chevron = '▸';

    /// <summary>What a `**` bold marker becomes while scanning: dropping it outright
    /// would run the item's name into the prose after it ("Read Only locks the
    /// document against…"), and keeping it would break the name match. One character
    /// that is neither, so a name can end at it.</summary>
    private const char Bold = '\u0002';

    // ===== the menu model =====

    private sealed class MenuNode
    {
        public MenuNode(string name) => Name = name;

        /// <summary>What the menu shows, access-key underscores already stripped.</summary>
        public string Name { get; }

        /// <summary>Set instead of a literal name when the app builds the header at
        /// runtime; matched against the documented spelling.</summary>
        public Regex? Pattern { get; init; }

        public List<MenuNode> Children { get; } = new();

        /// <summary>This submenu's contents are discovered at runtime (theme files,
        /// the recent-files list), so a name under it that the model doesn't know is
        /// not evidence of anything.</summary>
        public bool ChildrenDiscoveredAtRuntime { get; set; }
    }

    /// <summary>
    /// WPF spells an access key with a single underscore and a literal underscore
    /// with two, so "Save _As…" shows as "Save As…" and "a__b" as "a_b".
    /// </summary>
    private static string DisplayHeader(string header)
    {
        var sb = new StringBuilder(header.Length);
        for (var i = 0; i < header.Length; i++)
        {
            if (header[i] != '_') { sb.Append(header[i]); continue; }
            if (i + 1 < header.Length && header[i + 1] == '_') { sb.Append('_'); i++; }
        }
        return sb.ToString();
    }

    /// <summary>The menu bar out of MainWindow.xaml. The XAML is XML, so it is read
    /// as XML rather than with a regex: nesting is the whole point here, and a
    /// regex over angle brackets cannot see it.</summary>
    private static MenuNode MenuBarFromXaml(string xamlPath)
    {
        var menu = XDocument.Load(xamlPath).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Menu")
            ?? throw new InvalidOperationException($"No <Menu> element in {xamlPath}");
        var root = new MenuNode("(menu bar)");
        foreach (var item in menu.Elements().Where(e => e.Name.LocalName == "MenuItem"))
            if (NodeFrom(item) is { } node) root.Children.Add(node);
        return root;
    }

    /// <summary>Null for an item with no Header in the XAML — the app writes that
    /// one's text in code (Help ▸ Apply …), so it arrives from the list below.</summary>
    private static MenuNode? NodeFrom(XElement element)
    {
        if ((string?)element.Attribute("Header") is not { Length: > 0 } header) return null;
        var node = new MenuNode(DisplayHeader(header));
        foreach (var child in element.Elements().Where(e => e.Name.LocalName == "MenuItem"))
            if (NodeFrom(child) is { } sub) node.Children.Add(sub);
        return node;
    }

    /// <summary>
    /// The items the XAML doesn't have, because code adds them. Each one is checked
    /// into place through <see cref="Find"/>, so a rename on the XAML side fails
    /// this test loudly instead of quietly dropping the item.
    /// </summary>
    private static void AddItemsBuiltInCode(MenuNode root)
    {
        // MainWindow.Theme.cs builds the Theme submenu: one checkable item per theme
        // file, then these two. The theme names come from the CSS files present on
        // the machine (built-in and the user's own), so the list cannot be closed.
        var theme = Find(root, "View", "Theme");
        theme.Children.Add(new MenuNode("Same Theme for Both Views"));
        theme.Children.Add(new MenuNode("Open Themes Folder"));
        theme.ChildrenDiscoveredAtRuntime = true;

        // MainWindow.xaml.cs fills Open Recent with the user's files and appends this.
        var recent = Find(root, "File", "Open Recent");
        recent.Children.Add(new MenuNode("Clear Recent"));
        recent.ChildrenDiscoveredAtRuntime = true;

        // MainWindow.xaml.cs: MenuApplyUpdate.Header = $"Apply v{onDisk} _Update",
        // set when the Help menu opens and the installed copy is newer than this
        // process. The docs name it both with the version placeholder and without.
        Find(root, "Help").Children.Add(new MenuNode("Apply vX.Y.Z Update")
        {
            Pattern = new Regex(@"^Apply( v[0-9A-Za-z.+\-]+)? Update", RegexOptions.IgnoreCase),
        });
    }

    private static MenuNode Find(MenuNode root, params string[] path)
    {
        var node = root;
        foreach (var name in path)
            node = node.Children.FirstOrDefault(c => c.Name == name)
                ?? throw new InvalidOperationException(
                    $"MainWindow.xaml has no \"{name}\" under \"{node.Name}\" — this test's list of " +
                    "code-added menu items is out of date with the XAML.");
        return node;
    }

    // ===== reading the docs =====

    /// <summary>
    /// The repository, found from the test assembly rather than from the working
    /// directory: `dotnet test` runs from the repo root in CI and from wherever the
    /// developer happens to be locally, and bin/ sits a known number of levels down
    /// from neither. The solution file is MarkdownMidget.slnx, so the marker is
    /// matched with a wildcard.
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

    /// <summary>
    /// Which lines of CHANGELOG.md describe the menus as they are now: everything
    /// before the first version heading, the [Unreleased] section, and the newest
    /// dated section. Every older section is a historical account and exempt.
    /// </summary>
    private static IReadOnlyList<int> CurrentChangelogLines(IReadOnlyList<string> lines)
    {
        var scope = new List<int>();
        var dated = 0;
        var inScope = true;   // the preamble, until the first heading says otherwise
        for (var i = 0; i < lines.Count; i++)
        {
            if (Regex.Match(lines[i], @"^## \[(.+?)\]") is { Success: true } heading)
            {
                var version = heading.Groups[1].Value;
                inScope = version.Equals("Unreleased", StringComparison.OrdinalIgnoreCase) || ++dated == 1;
            }
            if (inScope) scope.Add(i);
        }
        return scope;
    }

    // ===== the scan =====

    /// <summary>
    /// One scannable string out of the lines in scope, with the source line of every
    /// character kept beside it. Lines are JOINED because a chain wraps: Help's
    /// "View ▸ Theme ▸ Same Theme for Both / Views" is one path over two lines, and
    /// a line-at-a-time scan would silently skip the deepest level of it. Each `**`
    /// becomes the <see cref="Bold"/> marker on the way through — every chain in
    /// these files is written inside a pair, and the closing one is where the item's
    /// name ends.
    ///
    /// Runs of whitespace collapse to one space, because the break can fall INSIDE
    /// an item's name: Help wraps "**File ▸ Windows / Integration ▸ Register as .md
    /// editor…**", and the continuation line is indented under its bullet, so the
    /// join would otherwise read "Windows   Integration" and match no item.
    /// </summary>
    private static (string Text, int[] Line) Flatten(IReadOnlyList<string> lines, IReadOnlyList<int> scope)
    {
        var sb = new StringBuilder();
        var map = new List<int>();
        void Append(char c, int line)
        {
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) return;
            sb.Append(c);
            map.Add(line);
        }
        foreach (var index in scope)
        {
            var line = lines[index];
            for (var c = 0; c < line.Length; c++)
            {
                var bold = line[c] == '*' && c + 1 < line.Length && line[c + 1] == '*';
                if (bold) c++;
                var ch = bold ? Bold : line[c];
                Append(char.IsWhiteSpace(ch) ? ' ' : ch, index + 1);
            }
            Append(' ', index + 1);   // the line break itself is a space
        }
        return (sb.ToString(), map.ToArray());
    }

    /// <summary>How many characters of <paramref name="text"/> at <paramref name="pos"/>
    /// this item's name accounts for, or -1. The documented name may leave off a
    /// trailing ellipsis ("Edit ▸ Settings" for "Settings…"), and whatever matches
    /// has to end at a word boundary so "Theme" doesn't swallow "Themes Folder".</summary>
    private static int MatchLength(MenuNode node, string text, int pos)
    {
        var best = -1;
        if (node.Pattern is not null)
        {
            var window = text.Substring(pos, Math.Min(120, text.Length - pos));
            if (node.Pattern.Match(window) is { Success: true, Index: 0 } m) best = m.Length;
        }
        else
        {
            foreach (var form in new[] { node.Name, node.Name.TrimEnd('…', '.', ' ') })
                if (form.Length > best && form.Length > 0
                    && string.CompareOrdinal(text, pos, form, 0, form.Length) == 0)
                    best = form.Length;
        }
        if (best < 0) return -1;
        var after = pos + best;
        return after >= text.Length || !char.IsLetterOrDigit(text[after]) ? best : -1;
    }

    /// <summary>Spaces, and the bold boundaries that sit between a chain and its
    /// markup ("**File** ▸ **New**" is the same path as "**File ▸ New**").</summary>
    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && text[pos] is ' ' or Bold) pos++;
        return pos;
    }

    /// <summary>What the doc appears to name where no item matched — enough of it to
    /// recognise in the failure message, stopped at the first punctuation that ends a
    /// menu name in prose.</summary>
    private static string Unmatched(string text, int pos)
    {
        var end = pos;
        while (end < text.Length && end - pos < 40)
        {
            var c = text[end];
            if (c is ',' or ';' or ':' or ')' or '(' or Chevron or Bold) break;
            // A full stop ends the sentence only when a space follows it: menu items
            // have one in the middle ("Register as .md editor…").
            if (c == '.' && (end + 1 >= text.Length || text[end + 1] == ' ')) break;
            end++;
        }
        return text[pos..end].Trim();
    }

    /// <summary>Every chain in one file that names something the menus don't have.</summary>
    private static List<string> Violations(string file, IReadOnlyList<string> lines,
                                           IReadOnlyList<int> scope, MenuNode menuBar)
    {
        var found = new List<string>();
        var (text, lineOf) = Flatten(lines, scope);
        foreach (var top in menuBar.Children)
        {
            foreach (Match start in Regex.Matches(text, Regex.Escape(top.Name) + @"[ \u0002]*" + Chevron))
            {
                // "…Insert ▸" inside a word ("Reinsert ▸") is not this menu.
                if (start.Index > 0 && char.IsLetterOrDigit(text[start.Index - 1])) continue;

                var node = top;
                var chain = top.Name;
                var pos = SkipSpaces(text, start.Index + start.Length);
                while (true)
                {
                    var match = node.Children
                        .Select(child => (child, length: MatchLength(child, text, pos)))
                        .Where(x => x.length > 0)
                        .OrderByDescending(x => x.length)
                        .FirstOrDefault();
                    if (match.child is null)
                    {
                        if (!node.ChildrenDiscoveredAtRuntime)
                        {
                            var named = Unmatched(text, pos);
                            found.Add($"{file}({lineOf[start.Index]}): \"{chain} {Chevron} {named}\" — " +
                                      $"the {node.Name} menu has no \"{named}\". It has: " +
                                      string.Join(", ", node.Children.Select(c => c.Name)));
                        }
                        break;
                    }

                    chain = $"{chain} {Chevron} {match.child.Name}";
                    pos = SkipSpaces(text, pos + match.length);
                    // A deeper level is only this test's business when the item it
                    // hangs off is a submenu; under a leaf it is a dialog's own
                    // control, which no menu can be asked about.
                    if (pos >= text.Length || text[pos] != Chevron) break;
                    if (match.child.Children.Count == 0 && !match.child.ChildrenDiscoveredAtRuntime) break;
                    node = match.child;
                    pos = SkipSpaces(text, pos + 1);
                }
            }
        }
        return found;
    }

    // ===== the pin =====

    private static MenuNode TheAppsMenus()
    {
        var root = MenuBarFromXaml(Path.Combine(RepoRoot(), "src", "MarkdownMidget", "MainWindow.xaml"));
        AddItemsBuiltInCode(root);
        return root;
    }

    [Theory]
    [InlineData("HELP.md")]
    [InlineData("README.md")]
    [InlineData("CHANGELOG.md")]
    public void EveryDocumentedMenuPathNamesAnItemThatExists(string docName)
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), docName));
        var scope = docName == "CHANGELOG.md"
            ? CurrentChangelogLines(lines)
            : Enumerable.Range(0, lines.Length).ToList();

        var violations = Violations(docName, lines, scope, TheAppsMenus());

        Assert.True(violations.Count == 0,
            $"{violations.Count} menu path(s) name an item that isn't there:{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void TheMenuBarIsTheSevenMenusTheAppShows()
    {
        // The scan only follows a chain whose first segment is one of these, so a
        // menu missing from the model would make every path under it unchecked
        // rather than failed — silence, which is what this test refuses.
        Assert.Equal(
            new[] { "File", "Edit", "Format", "Style", "Insert", "View", "Help" },
            TheAppsMenus().Children.Select(c => c.Name).ToArray());
    }

    [Theory]
    [InlineData("_New", "New")]
    [InlineData("Save _As…", "Save As…")]
    [InlineData("Export to P_DF…", "Export to PDF…")]
    [InlineData("Same_Theme__For_Both", "SameTheme_ForBoth")]
    public void AccessKeyUnderscoresGoAndDoubledOnesStay(string header, string shown)
        => Assert.Equal(shown, DisplayHeader(header));

    [Fact]
    public void AnItemUnderTheWrongMenuIsReportedWithItsFileLineAndChain()
    {
        // The mutation this whole file exists to kill: Read Only lives under Edit.
        var lines = new[] { "intro", "Locked with **View ▸ Read Only**, like this Help window." };

        var violation = Assert.Single(Violations("HELP.md", lines, [0, 1], TheAppsMenus()));

        Assert.Contains("HELP.md(2)", violation);
        Assert.Contains($"View {Chevron} Read Only", violation);
        Assert.Contains("the View menu has no \"Read Only\"", violation);
        Assert.Contains("Edit Markdown Source", violation);   // and here is what it does have
        // The same words under the menu that really has them are not a finding.
        Assert.Empty(Violations("HELP.md", ["**Edit ▸ Read Only** locks the document."], [0], TheAppsMenus()));
    }

    [Fact]
    public void AChainThatWrapsOntoTheNextLineIsStillFollowed()
    {
        // Help wraps exactly like this — mid-name, with the continuation indented
        // under its bullet — and the deepest level is the half that would go
        // unchecked if the scan worked a line at a time. The indent is the point of
        // the second line: without collapsing it, "Windows   Integration" matches no
        // item and the file's own text reads as a violation.
        var good = new[] { "- run **File ▸ Windows", "  Integration ▸ Register as .md editor…** once." };
        Assert.Empty(Violations("HELP.md", good, [0, 1], TheAppsMenus()));

        var bad = new[] { "- run **File ▸ Windows", "  Integration ▸ Register as .txt editor…** once." };
        Assert.Contains("Register as .txt editor",
            Assert.Single(Violations("HELP.md", bad, [0, 1], TheAppsMenus())));
    }

    [Fact]
    public void AChainThatDoesNotStartAtAMenuIsNotAMenuPath()
    {
        // Section paths and right-click paths use the same separator and are not
        // claims about the menu bar.
        string[] lines =
        [
            "**Right-click a picture ▸ Resize…** to scale it.",
            "listed in *Modified state, undo, and saving ▸ Markdown conventions*.",
        ];
        Assert.Empty(Violations("HELP.md", lines, [0, 1], TheAppsMenus()));
    }

    [Fact]
    public void AThirdSegmentUnderALeafItemIsADialogsBusinessNotAMenus()
    {
        // Settings… opens a dialog; "Import words from Word's custom dictionary" is a
        // button in it, and no menu model can confirm or deny it.
        Assert.Empty(Violations("HELP.md",
            ["**Edit ▸ Settings… ▸ Import words from Word's custom dictionary** copies them."],
            [0], TheAppsMenus()));
    }

    [Fact]
    public void ASubmenuFilledAtRuntimeAcceptsANameTheModelCannotKnow()
    {
        // A theme name comes from a CSS file on the machine. The two items the code
        // adds are still modelled, so the tolerance is scoped to this one submenu.
        Assert.Empty(Violations("HELP.md",
            ["**View ▸ Theme ▸ Dracula** and **View ▸ Theme ▸ Same Theme for Both Views**."],
            [0], TheAppsMenus()));
        Assert.Contains($"View {Chevron} Thyme",
            Assert.Single(Violations("HELP.md", ["**View ▸ Thyme ▸ Dracula**"], [0], TheAppsMenus())));
    }

    [Fact]
    public void OnlyTheUnreleasedAndNewestDatedChangelogSectionsAreScanned()
    {
        string[] lines =
        [
            "# Changelog",                     // 0  preamble - in scope
            "## [Unreleased]",                 // 1
            "- **Edit ▸ Read Only** claims the file.",   // 2  in scope, correct
            "## [0.10.0] - 2026-09-10",        // 3
            "- **View ▸ Spell Check** toggles.",         // 4  in scope, correct
            "## [0.9.0] - 2026-09-08",         // 5
            "- **View ▸ Settings…** moved to Edit.",     // 6  older: exempt, though wrong today
        ];
        var scope = CurrentChangelogLines(lines);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, scope);
        Assert.Empty(Violations("CHANGELOG.md", lines, scope, TheAppsMenus()));

        // The same line inside the newest section is a finding, so the exemption is
        // the section's age and not a hole in the scan.
        var scanned = CurrentChangelogLines(lines[..5]);
        Assert.Contains($"View {Chevron} Settings",
            Assert.Single(Violations("CHANGELOG.md", [.. lines[..5], "- **View ▸ Settings…** moved to Edit."],
                                     [.. scanned, 5], TheAppsMenus())));
    }
}
