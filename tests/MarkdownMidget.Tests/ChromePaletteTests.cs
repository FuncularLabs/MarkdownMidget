using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using MarkdownMidget.Chrome;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The two chrome palettes and the switch between them, from the loaded dictionaries (no
/// windows): one key set, dark text that is legible on the surface it is drawn on, and an
/// Apply that leaves exactly one palette merged whatever order it is called in.
/// </summary>
public class ChromePaletteTests
{
    internal static void RunSta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "STA body timed out");
        if (error is not null) throw new Exception("STA body failed", error);
    }

    [Fact]
    public void BothPalettesDefineTheSameKeysWithTheSameKindOfValue()
    {
        RunSta(() =>
        {
            var light = new ChromeLightPalette();
            var dark = new ChromeDarkPalette();
            var lightKeys = light.Keys.Cast<object>().ToHashSet();
            var darkKeys = dark.Keys.Cast<object>().ToHashSet();

            Assert.True(lightKeys.Count >= 90, $"only {lightKeys.Count} light keys: the palette did not load");
            Assert.Empty(lightKeys.Except(darkKeys));
            Assert.Empty(darkKeys.Except(lightKeys));
            foreach (var key in lightKeys)
            {
                var (l, d) = (light[key], dark[key]);
                var kind = l is Brush ? typeof(Brush) : l is Style ? typeof(Style) : l.GetType();
                Assert.True(kind.IsInstanceOfType(d), $"{key}: light is {l.GetType().Name}, dark is {d.GetType().Name}");
            }
        });
    }

    [Fact]
    public void EveryKeyTheWindowsUseIsInBothPalettes()
    {
        RunSta(() =>
        {
            var light = new ChromeLightPalette();
            var dark = new ChromeDarkPalette();
            var used = typeof(ChromeKeys).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => (string)f.GetRawConstantValue()!).ToList();
            Assert.NotEmpty(used);
            foreach (var key in used)
                Assert.True(light.Contains(key) && dark.Contains(key), $"ChromeKeys names {key}, which a palette lacks");
            foreach (var (_, token) in ChromePalette.SystemColorTokens)
                Assert.True(light.Contains(token) && dark.Contains(token), $"SystemColorTokens names {token}, which a palette lacks");
        });
    }

    /// <summary>Foreground brush, the surface it is drawn on in dark mode, the ratio it must clear.
    /// 4.5 for text; 3 for disabled text, glyphs and the 20px arrows.</summary>
    private static readonly (string Text, string Surface, double Minimum)[] DarkPairs =
    [
        ("Chrome.Text", "Chrome.Window.Background", 4.5),
        ("Chrome.Text", "Chrome.Control.Background", 4.5),
        ("Chrome.Text", "Chrome.ToolBar.Background", 4.5),
        ("Chrome.Text", "Chrome.StatusBar.Background", 4.5),
        ("Chrome.Text", "Chrome.ContextMenu.Background", 4.5),
        ("Chrome.Text", "Chrome.Menu.Highlight.Background", 4.5),
        ("Chrome.Text", "Chrome.Selection.Inactive.Background", 4.5),
        ("Chrome.Text", "Chrome.Button.Background", 4.5),
        ("Chrome.Text", "Chrome.Button.Hover.Background", 4.5),
        ("Chrome.Text", "Chrome.Button.Pressed.Background", 4.5),
        ("Chrome.Text", "Chrome.Button.Checked.Background", 4.5),
        ("Chrome.Text", "Chrome.ComboBox.Background", 4.5),
        ("Chrome.Text", "Chrome.ComboBox.Hover.Background", 4.5),
        ("Chrome.Text", "Chrome.ComboBox.Pressed.Background", 4.5),
        ("Chrome.Text", "Chrome.Find.Status.Background", 4.5),
        ("Chrome.Text.Disabled", "Chrome.Window.Background", 3),
        ("Chrome.Text.Disabled", "Chrome.Control.Background", 3),
        ("Chrome.Text.Disabled", "Chrome.ToolBar.Background", 3),
        ("Chrome.Menu.Text", "Chrome.Menu.Background", 4.5),
        ("Chrome.Menu.Text", "Chrome.Menu.Popup.Background", 4.5),
        ("Chrome.Menu.Text", "Chrome.Menu.Highlight.Background", 4.5),
        ("Chrome.Menu.Text", "Chrome.ContextMenu.Background", 4.5),
        ("Chrome.Menu.Text.Disabled", "Chrome.Menu.Popup.Background", 3),
        ("Chrome.Menu.Text.Disabled", "Chrome.ContextMenu.Background", 3),
        ("Chrome.Selection.Text", "Chrome.Selection.Background", 4.5),
        ("Chrome.Link", "Chrome.Window.Background", 4.5),
        ("Chrome.ToolTip.Text", "Chrome.ToolTip.Background", 4.5),
        ("Chrome.Button.Disabled.Text", "Chrome.Button.Disabled.Background", 3),
        ("Chrome.Check.Glyph", "Chrome.Check.Background", 4.5),
        ("Chrome.Check.Glyph", "Chrome.Check.Hover.Background", 4.5),
        ("Chrome.Check.Glyph", "Chrome.Check.Pressed.Background", 4.5),
        ("Chrome.Check.Disabled.Glyph", "Chrome.Check.Disabled.Background", 3),
        ("Chrome.ScrollBar.Glyph", "Chrome.ScrollBar.Background", 3),
        ("Chrome.ScrollBar.Glyph.Hover", "Chrome.ScrollBar.Button.Hover", 3),
        ("Chrome.ScrollBar.Glyph.Pressed", "Chrome.ScrollBar.Button.Pressed", 3),
        ("Chrome.ScrollBar.Glyph.Disabled", "Chrome.ScrollBar.Background", 3),
        ("Chrome.ComboBox.Arrow", "Chrome.ComboBox.Background", 3),
        ("Chrome.ComboBox.Arrow.Active", "Chrome.ComboBox.Hover.Background", 3),
        ("Chrome.ComboBox.Arrow.Active", "Chrome.ComboBox.Pressed.Background", 3),
        ("Chrome.ComboBox.Arrow.Disabled", "Chrome.ComboBox.Disabled.Background", 3),
        ("Chrome.Text.Accent", "Chrome.Window.Background", 4.5),
        ("Chrome.Text.Accent", "Chrome.StatusBar.Background", 4.5),
        ("Chrome.Text.Secondary", "Chrome.Window.Background", 4.5),
        ("Chrome.Text.Secondary", "Chrome.StatusBar.Background", 4.5),
        ("Chrome.Dialog.PathText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.MutedText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.CodeText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.HintText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.FaintText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.WarningText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.ErrorText", "Chrome.Window.Background", 4.5),
        ("Chrome.Dialog.InvalidText", "Chrome.Window.Background", 4.5),
        ("Chrome.Chip.Text", "Chrome.Chip.Background", 4.5),
        ("Chrome.Chip.RemovedText", "Chrome.Chip.Background", 4.5),
        ("Chrome.Chip.RemovedText", "Chrome.Window.Background", 4.5),
        ("Chrome.Chip.CaptionText", "Chrome.Window.Background", 4.5),
        ("Chrome.Chip.ArrowText", "Chrome.Window.Background", 3),
    ];

    [Fact]
    public void DarkTextClearsItsContrastTargetOnEverySurfaceItIsDrawnOn()
    {
        RunSta(() =>
        {
            var dark = new ChromeDarkPalette();
            var failures = new List<string>();
            foreach (var (text, surface, minimum) in DarkPairs)
            {
                Assert.True(dark.Contains(text) && dark.Contains(surface), $"{text} on {surface}: not in the dark palette");
                var (fg, bg) = (((SolidColorBrush)dark[text]).Color, ((SolidColorBrush)dark[surface]).Color);
                Assert.True(fg.A == 255 && bg.A == 255, $"{text} on {surface}: a contrast pair must be opaque");
                var ratio = SourcePalette.ContrastRatio(fg, bg);
                if (ratio < minimum) failures.Add($"{text} {fg} on {surface} {bg}: {ratio:0.00} < {minimum}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));

            // The no-document splash keeps its own text colours; on the dark splash they must still read.
            var splash = ((SolidColorBrush)dark[ChromeKeys.SplashBackground]).Color;
            foreach (var (hex, minimum) in new[] { ("#FFE4E6EA", 3.0), ("#FFC7CBD0", 4.5), ("#FF9CC4E4", 4.5) })
                Assert.True(SourcePalette.ContrastRatio((Color)ColorConverter.ConvertFromString(hex), splash) >= minimum, $"splash text {hex}");
        });
    }

    [Fact]
    public void EveryTextBrushIsCoveredByAContrastPair()
    {
        // A new text colour added to the palette without a pair would otherwise never be checked.
        RunSta(() =>
        {
            static bool IsText(string key) => key.Split('.').Any(s => s == "Link" || s.EndsWith("Text") || s.EndsWith("Glyph") || s.EndsWith("Arrow"));
            var textKeys = new ChromeDarkPalette().Keys.OfType<string>().Where(IsText).ToList();
            Assert.True(textKeys.Count >= 25, $"only {textKeys.Count} text keys found");
            var paired = DarkPairs.Select(p => p.Text).ToHashSet();
            var unpaired = textKeys.Where(k => !paired.Contains(k)).ToList();
            Assert.True(unpaired.Count == 0, "no contrast pair for: " + string.Join(", ", unpaired));
        });
    }

    private static List<ResourceDictionary> Ours(ResourceDictionary resources) =>
        resources.MergedDictionaries.Where(d => d is ChromeLightPalette or ChromeDarkMode).ToList();

    [Fact]
    public void ApplyingTheSameModeTwiceChangesNothing()
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            Assert.True(ChromePalette.Apply(resources, dark: true, highContrast: false));
            var afterFirst = resources.MergedDictionaries.ToList();
            var changes = 0;
            ((System.Collections.Specialized.INotifyCollectionChanged)resources.MergedDictionaries).CollectionChanged += (_, _) => changes++;
            Assert.True(ChromePalette.Apply(resources, dark: true, highContrast: false));
            Assert.Equal(afterFirst, resources.MergedDictionaries.ToList());
            Assert.Equal(0, changes);

            Assert.False(ChromePalette.Apply(resources, dark: false, highContrast: false));
            var light = resources.MergedDictionaries.ToList();
            changes = 0;
            Assert.False(ChromePalette.Apply(resources, dark: false, highContrast: false));
            Assert.Equal(light, resources.MergedDictionaries.ToList());
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void DarkThenLightLeavesExactlyTheLightPaletteAndNoSystemColourOverrides()
    {
        RunSta(() =>
        {
            var before = new ResourceDictionary();
            var after = new ResourceDictionary();
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(before);
            resources.MergedDictionaries.Add(new ChromeLightPalette());   // as App.xaml starts
            resources.MergedDictionaries.Add(after);

            ChromePalette.Apply(resources, dark: true, highContrast: false);
            Assert.Equal(3, resources.MergedDictionaries.Count);
            var darkMode = Assert.IsType<ChromeDarkMode>(resources.MergedDictionaries[1]);
            Assert.Equal(((SolidColorBrush)darkMode["Chrome.Text"]).Color, ((SolidColorBrush)resources[SystemColors.ControlTextBrushKey]).Color);
            Assert.Equal(((SolidColorBrush)darkMode["Chrome.Window.Background"]).Color, ((SolidColorBrush)resources[SystemColors.WindowBrushKey]).Color);

            ChromePalette.Apply(resources, dark: false, highContrast: false);
            Assert.Equal(3, resources.MergedDictionaries.Count);
            Assert.Same(before, resources.MergedDictionaries[0]);
            Assert.IsType<ChromeLightPalette>(resources.MergedDictionaries[1]);
            Assert.Same(after, resources.MergedDictionaries[2]);
            Assert.Single(Ours(resources));
            foreach (var (systemKey, _) in ChromePalette.SystemColorTokens)
                Assert.False(resources.Contains(systemKey), $"{systemKey} is still overridden after switching to light");
        });
    }

    [Fact]
    public void StrayChromeDictionariesCollapseToTheOneWanted()
    {
        // A resource set that somehow holds several (two starts, a hand edit) ends with one.
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(new ChromeLightPalette());
            resources.MergedDictionaries.Add(new ChromeDarkMode());
            resources.MergedDictionaries.Add(new ChromeLightPalette());
            ChromePalette.Apply(resources, dark: true, highContrast: false);
            Assert.IsType<ChromeDarkMode>(Assert.Single(resources.MergedDictionaries));

            resources.MergedDictionaries.Add(new ChromeLightPalette());
            ChromePalette.Apply(resources, dark: false, highContrast: false);
            Assert.IsType<ChromeLightPalette>(Assert.Single(resources.MergedDictionaries));
        });
    }

    [Fact]
    public void HighContrastTakesTheLightPathEvenWhenDarkIsAsked()
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            Assert.False(ChromePalette.Apply(resources, dark: true, highContrast: true));
            Assert.IsType<ChromeLightPalette>(Assert.Single(resources.MergedDictionaries));
            Assert.False(resources.Contains(SystemColors.WindowTextBrushKey));
            // With no resources (no Application) the effective mode is still reported.
            Assert.True(ChromePalette.Apply(null, dark: true, highContrast: false));
            Assert.False(ChromePalette.Apply(null, dark: true, highContrast: true));
        });
    }
}
