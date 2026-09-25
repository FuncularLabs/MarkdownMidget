using System;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// For a test that needs real keyboard focus. WPF gives an element keyboard focus only
/// while its thread holds Win32 focus, and only a window activated on that thread can
/// hold it. Windows can turn that activation into the foreground, even for a window far
/// off screen, and the user's typing then goes to the test's window. So such a test is
/// skipped, with this reason shown, unless the run is on CI (CI=true) or the person
/// running it has said a test may take the focus (MDM_TESTS_MAY_TAKE_FOCUS=1).
///
/// A test that needs only layout or a PresentationSource does not need this: it uses
/// <see cref="OffscreenWindow"/>, which is never activated.
/// </summary>
internal sealed class TakesFocusFactAttribute : FactAttribute
{
    public TakesFocusFactAttribute()
    {
        if (!MayTakeFocus)
            Skip = "Takes real keyboard focus, which activates a window, and Windows can turn that into the foreground "
                + "and take the keyboard from whoever is using this desktop. Runs on CI, or with MDM_TESTS_MAY_TAKE_FOCUS=1.";
    }

    private static bool MayTakeFocus =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("MDM_TESTS_MAY_TAKE_FOCUS") == "1";
}
