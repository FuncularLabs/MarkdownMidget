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
}
