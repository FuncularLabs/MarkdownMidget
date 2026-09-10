using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The rule behind View ▸ Theme once the source view may have its own theme. What
/// regresses here is subtle and user-visible: a selection landing on the wrong view,
/// or the menu ticking the document theme while the user is looking at a differently
/// themed source pane.
/// </summary>
public class ThemeLinkingTests
{
    [Theory]
    // linked: both views, whichever view is showing
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    // unlinked: only the view the user is in
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, true)]
    public void ASelectionTargetsTheRightViews(bool linked, bool sourceMode, bool expectDoc, bool expectSource)
    {
        var (doc, src) = ThemeLinking.TargetsFor(linked, sourceMode);
        Assert.Equal(expectDoc, doc);
        Assert.Equal(expectSource, src);
    }

    [Theory]
    // linked: always the document theme, even in the source view
    [InlineData(true, false, "doc")]
    [InlineData(true, true, "doc")]
    // unlinked: the active view's theme
    [InlineData(false, false, "doc")]
    [InlineData(false, true, "src")]
    public void TheMenuTicksTheActiveViewsTheme(bool linked, bool sourceMode, string expected)
        => Assert.Equal(expected, ThemeLinking.TickedKey(linked, sourceMode, "doc", "src"));

    [Fact]
    public void UnlinkedInTheSourceViewNeverTouchesTheDocument()
    {
        // The whole point of unlinking: setting the source view dark must not recolour
        // the document behind it.
        var (doc, _) = ThemeLinking.TargetsFor(linked: false, sourceMode: true);
        Assert.False(doc);
    }
}
