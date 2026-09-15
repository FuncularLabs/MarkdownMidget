using System;

namespace MarkdownMidget;

/// <summary>The busy lightbox of a switch from the source view (<c>SetSourceModeAsync</c>), known by its view generation; the window holds the live one's, 0 for none.</summary>
internal static class SwitchBusy
{
    /// <summary>How long a switch runs before its lightbox shows: a small document's is over by then and never flashes it.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);

    /// <summary>What the lightbox says, and so how an ending switch knows the overlay is still its own and not an open's put up since.</summary>
    public const string Text = "Switching to the formatted view…";

    /// <summary>Whether switch <paramref name="busy"/> is the one the window's <paramref name="owner"/> names: a later switch's number, or none, is not.</summary>
    public static bool Owns(long owner, long busy) => busy != 0 && owner == busy;

    /// <summary>Whether switch <paramref name="busy"/>'s lightbox shows once <see cref="Delay"/> is up: still its switch, still installing
    /// with the source view showing (the formatted view's WebView2 would cover it), and no open already holding the overlay.</summary>
    public static bool ShowsNow(long owner, long busy, bool sourceView, bool overlayShowing) => Owns(owner, busy) && sourceView && !overlayShowing;
}
