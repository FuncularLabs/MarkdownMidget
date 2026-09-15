using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Which box Ctrl+H (Edit ▸ Replace…) puts the cursor in: Replace with, as in other editors, unless the
/// document is read-only. Then the dialog greys Replace and the host refuses one, so the cursor goes to
/// Find what, where Ctrl+F puts it. The keypress and the focus itself are WPF: test plan FND-01..03.
/// </summary>
public class FindDialogFocusTests
{
    [Theory]
    [InlineData(false, false, false)]   // Ctrl+F: Find what
    [InlineData(false, true, false)]    // Ctrl+F, read-only: Find what
    [InlineData(true, false, true)]     // Ctrl+H: Replace with
    [InlineData(true, true, false)]     // Ctrl+H, read-only: Find what, as there is nothing to replace
    public void CtrlHFocusesReplaceWithUnlessReadOnly(bool replaceAsked, bool readOnly, bool expected)
        => Assert.Equal(expected, FindDialog.FocusesReplaceBox(replaceAsked, readOnly));

    // Replace and Replace All are greyed, with this tooltip, unless the decision is null. With no document
    // open they are greyed as in a read-only window. The buttons: FND-03.
    [Theory]
    [InlineData(false, false, null)]                            // an editable document: Replace works
    [InlineData(true, false, "The document is read-only.")]
    [InlineData(false, true, "No document is open.")]           // "No document open"
    [InlineData(true, true, "No document is open.")]            // e.g. Help closed with Ctrl+W
    public void ReplaceIsGreyedWhenReadOnlyOrNoDocument(bool readOnly, bool noDocument, string? expectedTip)
        => Assert.Equal(expectedTip, FindDialog.ReplaceBlockedTip(readOnly, noDocument));
}
