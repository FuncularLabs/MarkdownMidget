using System.IO;
using System.Linq;
using System.Text.Json;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The one picture ceiling (<see cref="PictureLimit"/>): the constant every route
/// applies, where its boundary sits, and the one notice every route shows when a
/// picture is past it. The routes themselves are tested where they live — the drop
/// in DropRoutingTests and DropFilesTests, Insert ▸ Picture in PickedPictureTests,
/// the formatted view's paste in editor-src/test/picture-paste*.test.mjs — and what
/// is tested here is that they share one ceiling and one way of saying so.
/// </summary>
public class PictureLimitTests
{
    private const long Megabyte = 1024 * 1024;

    // The first bytes of a PNG, which is all the drop's routing reads.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    [Fact]
    public void TheDropCeilingIsTheSharedCeiling()
    {
        // One ceiling, not two that happen to agree today: change the shared one and
        // the drop's changes with it. The drop's own boundary sits exactly there.
        Assert.Equal(PictureLimit.MaxBytes, DropRouting.MaxPictureBytes);
        Assert.Equal(DropKind.Picture, DropRouting.Classify("at.png", Png, PictureLimit.MaxBytes).Kind);
        Assert.Equal(DropKind.TooLarge, DropRouting.Classify("over.png", Png, PictureLimit.MaxBytes + 1).Kind);
        // And giving it a shared home did not move it: the drop still refuses
        // exactly what it refused before.
        Assert.Equal(64L * Megabyte, PictureLimit.MaxBytes);
    }

    [Fact]
    public void ExactlyTheCeilingGoesInAndOneByteMoreDoesNot()
    {
        Assert.False(PictureLimit.IsTooLarge(PictureLimit.MaxBytes));
        Assert.True(PictureLimit.IsTooLarge(PictureLimit.MaxBytes + 1));
        Assert.False(PictureLimit.IsTooLarge(0));
        // -1 is "the size was not stated" (a drop message that gave none): no
        // ceiling applies to it, rather than a picture being refused over a field
        // that went missing.
        Assert.False(PictureLimit.IsTooLarge(-1));
        // The ceiling is a parameter only so a test need not allocate 64 MB; the
        // boundary is the same wherever it sits.
        Assert.False(PictureLimit.IsTooLarge(64, ceiling: 64));
        Assert.True(PictureLimit.IsTooLarge(65, ceiling: 64));
    }

    [Fact]
    public void TheNoticeNamesTheLimitComputedFromTheCeiling()
    {
        Assert.Equal($"Too large to insert (over {PictureLimit.MaxBytes / Megabyte} MB): huge.png", PictureLimit.Notice("huge.png"));
        // Computed, never written down: the same call with another ceiling states
        // that one. A notice that said "64 MB" as a literal passes the line above
        // and fails these.
        Assert.Equal("Too large to insert (over 32 MB): huge.png", PictureLimit.Notice("huge.png", ceiling: 32 * Megabyte));
        Assert.Equal("Too large to insert (over 128 MB): a.png, b.png", PictureLimit.Notice("a.png, b.png", ceiling: 128 * Megabyte));
    }

    [Fact]
    public void APictureWithNoNameIsCalledThePastedPicture()
    {
        // A picture from the clipboard has no file name to put in the notice. The
        // wording is otherwise the same, so a paste and a drop refused for the same
        // reason read the same.
        Assert.Equal($"Too large to insert (over {PictureLimit.MaxBytes / Megabyte} MB): pasted picture", PictureLimit.Notice(null));
        Assert.Equal("Too large to insert (over 32 MB): pasted picture", PictureLimit.Notice(null, ceiling: 32 * Megabyte));
    }

    [Fact]
    public void TheDropRefusesWithTheSharedNotice()
    {
        // The drop's status line for a picture past the ceiling IS the shared
        // notice, so Insert ▸ Picture, a paste and a drop cannot drift into three
        // wordings of one refusal.
        var one = DropRouting.Plan([new DroppedFile("huge.png", Png, PictureLimit.MaxBytes + 1)], DropTarget.Editable, oneDocument: false);
        Assert.Equal(PictureLimit.Notice("huge.png"), one.Notice());
        var two = DropRouting.Plan(
            [new DroppedFile("a.png", Png, PictureLimit.MaxBytes + 1), new DroppedFile("b.png", Png, PictureLimit.MaxBytes + 1)],
            DropTarget.Editable, oneDocument: false);
        Assert.Equal(PictureLimit.Notice("a.png, b.png"), two.Notice());
    }

    [Fact]
    public void TheEditorIsHandedTheCeilingAsJson()
    {
        // A paste into the formatted view is Chromium's own, so the editor applies
        // the ceiling to it there — the host's ceiling, handed over in MDM.create's
        // options, never a second 64 MB written into the editor.
        using var options = JsonDocument.Parse(PictureLimit.EditorOptionsJson());
        var only = Assert.Single(options.RootElement.EnumerateObject());
        Assert.Equal("maxPictureBytes", only.Name);
        Assert.Equal(PictureLimit.MaxBytes, only.Value.GetInt64());
    }

    [Fact]
    public void TheEditorReadsTheCeilingAndReportsARefusalUnderTheNamesTheHostUses()
    {
        // A wiring pin, read from the sources, for the one seam no test runs end to
        // end: host and editor are two languages talking through a WebView. If the
        // option's name drifts on one side, the editor's guard gets no ceiling and
        // silently refuses nothing; if the message type drifts, a refused paste is
        // cancelled with no word in the status bar. Each side's behaviour is tested
        // on that side (picture-paste*.test.mjs; the notice above); this proves only
        // that the names meet.
        using var options = JsonDocument.Parse(PictureLimit.EditorOptionsJson());
        var option = Assert.Single(options.RootElement.EnumerateObject()).Name;

        var guard = Source("editor-src", "src", "picture-paste.js");
        Assert.Contains($"options.{option}", guard, StringComparison.Ordinal);
        Assert.Contains($"type: '{PictureLimit.RefusedMessageType}'", guard, StringComparison.Ordinal);

        var main = Source("editor-src", "src", "main.js");
        Assert.Contains("ceilingFrom(options)", main, StringComparison.Ordinal);
        Assert.Contains("postToHost(refusalMessage(size))", main, StringComparison.Ordinal);

        var window = Source("src", "MarkdownMidget", "MainWindow.xaml.cs");
        Assert.Contains("PictureLimit.EditorOptionsJson()", window, StringComparison.Ordinal);
        Assert.Contains("case PictureLimit.RefusedMessageType:", window, StringComparison.Ordinal);
    }

    private static string Source(params string[] path) =>
        File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(path)));

    /// <summary>The repository, found from the test assembly (as MenuPathsInDocsTests
    /// finds it).</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles("MarkdownMidget.sln*").Any()
                || (dir.EnumerateDirectories("src").Any() && dir.EnumerateFiles("HELP.md").Any()))
                return dir.FullName;
        throw new InvalidOperationException(
            $"No MarkdownMidget.sln[x] (or src/ beside HELP.md) above {AppContext.BaseDirectory}");
    }
}
