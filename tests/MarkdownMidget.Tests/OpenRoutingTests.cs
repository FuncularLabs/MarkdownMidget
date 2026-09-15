using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Opening never replaces a document: in this window only on the "no document" screen, else one instance per file. No test starts a process.</summary>
public class OpenRoutingTests
{
    private const string A = @"C:\docs\a.md", B = @"C:\docs\b.md", ThisOne = @"c:\DOCS\A.md";

    [Theory]
    [InlineData(true, false, false, "", 0, true)]           // the "no document" screen
    [InlineData(false, true, false, "", 0, true)]           // a blank untitled document nobody has touched
    [InlineData(false, true, false, " \n", 0, true)]
    [InlineData(false, true, true, "", 0, false)]           // ... typed into
    [InlineData(false, true, false, "# notes", 0, false)]   // an untitled document with text
    [InlineData(false, true, false, "\0baseline not taken\0", 0, false)]   // a load's placeholder baseline
    [InlineData(false, false, false, "", 0, false)]         // an empty saved file
    [InlineData(true, false, false, "", 1, false)]          // F-1: an open into this window is still loading
    [InlineData(false, true, false, "", 1, false)]          // F-2: a window still starting looks blank but is counted until it lands
    public void A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way(bool closed, bool untitled, bool modified, string clean, int underWay, bool expected) =>
        Assert.Equal(expected, OpenRouting.HasNoDocument(closed, untitled, modified, clean, underWay));

    [Theory]
    [InlineData(true, null, 1)]                    // no document: the first file opens here
    [InlineData(false, null, 0)]                   // an untitled document with text
    [InlineData(false, @"C:\docs\other.md", 0)]    // a saved file
    [InlineData(false, ThisOne, 1)]                // this window's own document is not opened again
    public void The_first_file_opens_here_only_when_the_window_has_no_document(bool noDocument, string? current, int notLaunched)
    {
        var route = OpenRouting.Plan([A, B, B], noDocument, current);    // a file named twice starts once
        Assert.Equal((noDocument ? A : null, current == ThisOne), (route.Here, route.AlreadyHere));
        Assert.Equal(new[] { A, B }.Skip(notLaunched), route.Launch);
        Assert.Null(route.Notice());
    }

    [Fact]
    public void A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note()
    {
        var paths = Enumerable.Range(1, 12).Select(i => $@"C:\docs\{i}.md").ToList();
        var route = OpenRouting.Plan(paths, noDocument: true, currentPath: null);
        Assert.Equal(paths[0], route.Here);
        Assert.Equal(paths.Skip(1).Take(10), route.Launch);
        Assert.Contains("12.md", route.Notice());
        var started = new List<string>();
        Assert.Contains("a.md", OpenRouting.LaunchAll([A, B], p => { if (p == A) throw new System.ComponentModel.Win32Exception(2); started.Add(p); }));
        Assert.Equal(new[] { B }, started);
        Assert.Null(OpenRouting.LaunchAll([B], _ => { }));
    }

    [Fact]
    public void Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name()
    {
        byte[] text = "# x"u8.ToArray(), png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var plan = DropRouting.Plan([new("a.md", text), new("p.png", png), new("x.zip", text), new("dir", null), new("b.txt", text), new("c.md", text)], DropTarget.NoDocument, oneDocument: false);
        var (paths, notice) = OpenRouting.DocumentPaths(plan, [A, @"C:\p.png", @"C:\x.zip", @"C:\dir", @"C:\docs\b.txt", @"C:\docs\wrong.md"]);
        Assert.Equal(new[] { A, @"C:\docs\b.txt" }, paths);
        Assert.Contains("c.md", notice);
        Assert.Empty(OpenRouting.DocumentPaths(plan, null).Paths);    // no path: no unsaved copy either
        Assert.Empty(OpenRouting.DocumentPaths(plan, [A]).Paths);     // lists that don't line up are not trusted
    }
}
