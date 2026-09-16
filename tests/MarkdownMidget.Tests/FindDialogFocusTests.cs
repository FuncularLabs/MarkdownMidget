using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// Which box Ctrl+H (Edit ▸ Replace…) puts the cursor in: Replace with, as in other editors, unless the
/// document is read-only or not open (the dialog greys Replace and the host refuses one) or Find what is empty. Then the cursor goes to
/// Find what, where Ctrl+F puts it. The keypress and the focus itself are WPF: test plan FND-01..03.
/// </summary>
public class FindDialogFocusTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]   // Ctrl+F: Find what
    [InlineData(false, true, true, false, false)]     // Ctrl+F, read-only: Find what
    [InlineData(true, false, false, false, true)]     // Ctrl+H, Find what filled: Replace with
    [InlineData(true, true, false, false, false)]     // Ctrl+H, Find what empty: Find what, which has to be typed first
    [InlineData(true, false, true, false, false)]     // Ctrl+H, read-only: Find what, as there is nothing to replace
    [InlineData(true, false, false, true, false)]     // Ctrl+H, no document: the same
    public void CtrlHFocusesReplaceWithOnlyWhenFindWhatHasTextAndReplaceCanRun(bool replaceAsked, bool queryEmpty, bool readOnly, bool noDocument, bool expected)
        => Assert.Equal(expected, FindDialog.FocusesReplaceBox(replaceAsked,
            readOnly: FindDialog.ReplaceBlockedTip(readOnly, noDocument) is not null, queryEmpty));   // as SetReadOnly folds them

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
