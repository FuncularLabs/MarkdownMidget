using System.Text;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// "Did the file really change?" (issue #4, release-1.0 stage 0.12 R6). A window
/// keeps two baselines: the editor's own serialisation of the last opened/saved
/// state (dirty tracking) and the file as it was last read from or written to
/// disk. They differ whenever the editor normalises - a setext heading comes back
/// ATX - so a tool that rewrites the file with identical bytes must be judged
/// against the disk baseline, not the editor's.
/// </summary>
public class ExternalChangeTests
{
    // On disk: a setext heading. In the editor: the same heading, serialised ATX.
    private const string OnDisk = "Title\r\n=====\r\n\r\nbody\r\n";
    private const string Normalised = "# Title\n\nbody\n";

    private static string Folded(string raw) => DocumentText.Detect(Encoding.UTF8.GetBytes(raw)).Text;

    [Fact]
    public void IdenticalBytesAreNotAChange()
    {
        var diskBaseline = Folded(OnDisk);
        Assert.NotEqual(Normalised, diskBaseline);   // the pair the bug needs: normalised != raw

        var rewritten = Folded(OnDisk);              // a tool touched the file; same bytes

        Assert.False(ExternalChange.IsRealChange(rewritten, Normalised, diskBaseline));
    }

    [Fact]
    public void LineEndingOnlyRewriteIsNotAChange()
    {
        var diskBaseline = Folded(OnDisk);                         // loaded CRLF
        var rewritten = Folded(OnDisk.Replace("\r\n", "\n"));      // dos2unix ran on it

        Assert.False(ExternalChange.IsRealChange(rewritten, Normalised, diskBaseline));
    }

    [Fact]
    public void BomOnlyRewriteIsNotAChange()
    {
        var diskBaseline = Folded(OnDisk);
        var withMark = DocumentText.Detect([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(OnDisk)]).Text;

        Assert.False(ExternalChange.IsRealChange(withMark, Normalised, diskBaseline));
    }

    [Fact]
    public void ContentChangeIsAChange()
    {
        var diskBaseline = Folded(OnDisk);
        var edited = Folded(OnDisk + "more\r\n");

        Assert.True(ExternalChange.IsRealChange(edited, Normalised, diskBaseline));
    }

    [Fact]
    public void DiskMatchingTheEditorsOwnSerialisationIsNotAChange()
    {
        // The check that existed before #4, kept: a file now holding exactly what
        // the editor considers clean has nothing to reload.
        var diskBaseline = Folded(OnDisk);

        Assert.False(ExternalChange.IsRealChange(Normalised, Normalised, diskBaseline));
    }
}
