using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>Copy Link's clipboard write, through a fake that stands in for the clipboard: no test here touches the real one.</summary>
public class CopyLinkTests
{
    [Fact]
    public void CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash()
    {
        string? written = null;
        Assert.Null(MainWindow.CopyLinkTo("mailto:a@b.example?subject=Hi%20there&body=x", s => written = s));
        Assert.Equal("mailto:a@b.example?subject=Hi%20there&body=x", written);
        Assert.NotNull(MainWindow.CopyLinkTo("x", _ => throw new System.Runtime.InteropServices.COMException("in use", unchecked((int)0x800401D0))));   // CLIPBRD_E_CANT_OPEN
    }
}
