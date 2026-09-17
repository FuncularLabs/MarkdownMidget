using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;

namespace MarkdownMidget.Chrome;

/// <summary>ChromeLight.xaml: today's colours.</summary>
public partial class ChromeLightPalette : ResourceDictionary
{
    public ChromeLightPalette() => InitializeComponent();
}

/// <summary>ChromeDark.xaml: the same keys in dark greys.</summary>
public partial class ChromeDarkPalette : ResourceDictionary
{
    public ChromeDarkPalette() => InitializeComponent();
}

/// <summary>ChromeStyles.xaml: the styles and templates that draw with the palette.</summary>
public partial class ChromeDarkStyles : ResourceDictionary
{
    public ChromeDarkStyles() => InitializeComponent();
}

/// <summary>
/// Everything dark mode merges, as one dictionary so that switching is one change to the
/// application's resources rather than several: a palette, the styles, and the SystemColors
/// keys that Aero2's own templates read (window and text colours, selection, grey text),
/// pointed at the palette's brushes.
/// </summary>
internal sealed class ChromeDarkMode : ResourceDictionary
{
    public ChromeDarkMode() : this(new ChromeDarkPalette()) { }

    /// <summary>Any palette with the chrome keys; tests pass the light one to prove the
    /// templates are Aero2's.</summary>
    internal ChromeDarkMode(ResourceDictionary palette)
    {
        MergedDictionaries.Add(palette);
        MergedDictionaries.Add(new ChromeDarkStyles());
        foreach (var (systemKey, token) in ChromePalette.SystemColorTokens)
            this[systemKey] = palette[token];
    }
}

/// <summary>
/// The window chrome's light and dark palettes, and the one switch between them.
///
/// Light is exactly today's app: only <see cref="ChromeLightPalette"/> is merged, which holds
/// the colours the windows' XAML used to spell out and no colour-bearing style (its two styles
/// set sizes only: menu separators' spacing and the file picker's glyph buttons; see
/// ChromeLight.xaml). No template replaces Aero2's. Dark
/// merges <see cref="ChromeDarkMode"/>. High contrast always takes the light path, so the
/// system's colours stay in charge there.
/// </summary>
public static class ChromePalette
{
    /// <summary>
    /// Which SystemColors brush keys dark mode overrides, and with which palette brush.
    /// Aero2 draws window backgrounds, text, grey text, selections and separators through
    /// these keys, so overriding them darkens every control that reads them without a
    /// template of our own.
    /// </summary>
    internal static readonly IReadOnlyList<(ResourceKey SystemKey, string Token)> SystemColorTokens =
    [
        (SystemColors.WindowBrushKey, "Chrome.Window.Background"),
        (SystemColors.WindowFrameBrushKey, "Chrome.Window.Frame"),
        (SystemColors.WindowTextBrushKey, "Chrome.Text"),
        (SystemColors.ControlTextBrushKey, "Chrome.Text"),
        (SystemColors.MenuTextBrushKey, "Chrome.Text"),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, "Chrome.Text"),
        (SystemColors.GrayTextBrushKey, "Chrome.Text.Disabled"),
        (SystemColors.ControlBrushKey, "Chrome.Control.Background"),
        (SystemColors.ControlDarkBrushKey, "Chrome.Control.Dark"),
        (SystemColors.ControlDarkDarkBrushKey, "Chrome.Control.DarkDark"),
        (SystemColors.HighlightBrushKey, "Chrome.Selection.Background"),
        (SystemColors.HighlightTextBrushKey, "Chrome.Selection.Text"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "Chrome.Selection.Inactive.Background"),
        (SystemColors.HotTrackBrushKey, "Chrome.Link"),
    ];

    /// <summary>
    /// THE HOOK for the Windows appearance service: call it once at startup with the effective
    /// mode, before the first window opens, and again from its Changed event. Safe from any
    /// thread (it moves itself to the application's; when calls cross threads, the last one
    /// made wins) and cheap to repeat: applying the mode already in force changes nothing. Every open window follows through DynamicResource,
    /// and every window's title bar follows through <see cref="ChromeWindows"/>. With high
    /// contrast on, dark is ignored and the light path is taken.
    /// </summary>
    public static void Apply(bool dark) =>
        Apply(dark, Application.Current?.Dispatcher, () => Application.Current?.Resources, () => SystemParameters.HighContrast);

    /// <summary><see cref="Apply(bool)"/> with the application's dispatcher, resources and the
    /// high contrast reading passed in; a null dispatcher applies on the calling thread.</summary>
    internal static void Apply(bool dark, System.Windows.Threading.Dispatcher? dispatcher,
                               Func<ResourceDictionary?> resources, Func<bool> highContrast)
    {
        Volatile.Write(ref _latestDark, dark ? 1 : 0);
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // The queued call applies whatever was asked last when it runs, so a call that
            // waited here can never undo a later one.
            dispatcher.BeginInvoke(new Action(() => ApplyLatest(resources, highContrast)));
            return;
        }
        ApplyLatest(resources, highContrast);
    }

    private static int _latestDark;   // the mode of the most recent Apply call, from any thread

    private static void ApplyLatest(Func<ResourceDictionary?> resources, Func<bool> highContrast)
    {
        ChromeWindows.Register();
        ChromeWindows.SetDark(Apply(resources(), Volatile.Read(ref _latestDark) == 1, highContrast()));
    }

    /// <summary>
    /// Put exactly one chrome dictionary into <paramref name="resources"/>' merged
    /// dictionaries, where the previous one was (other merged dictionaries keep their
    /// places), and report whether dark was applied. Returns without touching anything when
    /// the wanted one is already the only one there.
    /// </summary>
    internal static bool Apply(ResourceDictionary? resources, bool dark, bool highContrast)
    {
        var effective = dark && !highContrast;
        if (resources is null) return effective;

        var merged = resources.MergedDictionaries;
        var ours = merged.Where(d => d is ChromeLightPalette or ChromeDarkMode).ToList();
        if (ours.Count == 1 && (effective ? ours[0] is ChromeDarkMode : ours[0] is ChromeLightPalette))
            return effective;

        // A fresh dictionary each switch: switches are rare, and a dictionary is never shared
        // between two resource owners or two threads.
        ResourceDictionary wanted = effective ? new ChromeDarkMode() : new ChromeLightPalette();
        if (ours.Count == 0)
        {
            merged.Add(wanted);
            return effective;
        }
        // Replace in place: one change, so one resource invalidation per open window.
        merged[merged.IndexOf(ours[0])] = wanted;
        foreach (var stale in ours.Skip(1))
            merged.Remove(stale);
        return effective;
    }
}
