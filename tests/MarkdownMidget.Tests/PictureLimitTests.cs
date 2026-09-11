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
}
