using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MarkdownMidget.Chrome;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The chrome palettes, proved by drawing. Representative controls are built in memory and
/// rendered with RenderTargetBitmap on an STA thread (no window, nothing shown), once with
/// nothing but Aero2 and once per chrome state, and the pixels compared exactly.
///
/// "Faithful" controls are the ones whose dark template is Aero2's with only brushes swapped;
/// with the light palette those templates must draw Aero2 pixel for pixel, which is what
/// holds the light values to Aero2's and the templates to Aero2's shapes. The file picker's
/// list rows and headers and the toolbar's combo box have dark looks of their own, so they
/// are held only to "light is unchanged" and "dark is dark".
/// </summary>
public class ChromeRenderTests
{
    /// <summary>
    /// Skipped, with the reason shown, unless WPF is drawing with Aero2: every comparison here
    /// is against Aero2's own pixels and sizes. Windows reports "Aero" for the standard theme;
    /// high contrast ("AeroLite") and the classic look (visual styles off) load other themes.
    /// </summary>
    internal sealed class Aero2FactAttribute : FactAttribute
    {
        public Aero2FactAttribute()
        {
            if (!string.Equals(SystemParameters.UxThemeName, "Aero", StringComparison.OrdinalIgnoreCase))
                Skip = $"Needs the standard Windows theme, which WPF draws with Aero2; this session's theme is \"{SystemParameters.UxThemeName}\".";
        }
    }

    private sealed record Shot(int Width, int Height, byte[] Pixels);

    private sealed record Sample(string Name, bool Faithful, Func<FrameworkElement> Make);

    private static MenuItem Item(string header, params object[] children)
    {
        var item = new MenuItem { Header = header };
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    private static IReadOnlyList<Sample> Samples() =>
    [
        new("menu bar", true, () => new Menu
        {
            Items = { Item("_File", Item("_New")), Item("_Edit", Item("_Undo")), new MenuItem { Header = "_Help", IsEnabled = false, Items = { Item("x") } } },
        }),
        new("menu items", true, () => new StackPanel
        {
            Width = 240,
            Children =
            {
                new MenuItem { Header = "_Bold", InputGestureText = "Ctrl+B", IsCheckable = true, IsChecked = true },
                new MenuItem { Header = "Disabled", IsEnabled = false, IsCheckable = true, IsChecked = true },
                Item("Code _Block", Item("C#")),
                new MenuItem { Header = "Disabled header", IsEnabled = false, Items = { Item("x") } },
            },
        }),
        new("context menu", true, () => new ContextMenu
        {
            Items = { Item("Cu_t"), new Separator(), new MenuItem { Header = "_Paste", IsEnabled = false }, new MenuItem { Header = "Checked", IsChecked = true } },
        }),
        new("toolbar", true, () => new ToolBarTray
        {
            IsLocked = true,
            ToolBars =
            {
                new ToolBar
                {
                    Items =
                    {
                        new Button { Content = "New" }, new ToggleButton { Content = "B", IsChecked = true }, new Separator(),
                        new TextBlock { Text = "Style:" }, new Button { Content = "U", IsEnabled = false },
                    },
                },
            },
        }),
        new("toolbar with overflow", true, () => new ToolBarTray
        {
            IsLocked = true, Width = 90,
            ToolBars = { new ToolBar { Items = { new Button { Content = "One" }, new Button { Content = "Two" }, new Button { Content = "Three" }, new Button { Content = "Four" } } } },
        }),
        // Toolbars sit in a locked tray, as the app's one tray is: the dark toolbar has no grip.
        new("toolbar combo box", false, () => new ToolBarTray
        {
            IsLocked = true,
            ToolBars = { new ToolBar { Items = { new ComboBox { Width = 140, Items = { "Paragraph", "Heading 1" }, SelectedIndex = 0 } } } },
        }),
        new("status bar", true, () => new StatusBar
        {
            Width = 300,
            Items = { new StatusBarItem { Content = "WYSIWYG" }, new Separator(), new StatusBarItem { Content = "Untitled" } },
        }),
        new("tooltip", true, () => new ToolTip { Content = "Save (Ctrl+S)" }),
        new("buttons", true, () => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { new Button { Content = "OK", Width = 80 }, new Button { Content = "Cancel", Padding = new Thickness(12, 3, 12, 3) }, new Button { Content = "Off", IsEnabled = false } },
        }),
        new("text boxes", true, () => new StackPanel
        {
            Width = 200,
            Children = { new TextBox { Text = "find me", Padding = new Thickness(3) }, new PasswordBox { Password = "secret" }, new TextBox { Text = "read only", IsEnabled = false } },
        }),
        new("check boxes", true, () => new StackPanel
        {
            Children =
            {
                new CheckBox { Content = "Match _case", IsChecked = true }, new CheckBox { Content = "Whole word" },
                new CheckBox { Content = "Mixed", IsThreeState = true, IsChecked = null }, new CheckBox { Content = "Disabled", IsChecked = true, IsEnabled = false },
            },
        }),
        new("radio buttons", true, () => new StackPanel
        {
            Children =
            {
                new RadioButton { Content = "_Normal", GroupName = "a", IsChecked = true }, new RadioButton { Content = "Extended", GroupName = "a" },
                new RadioButton { Content = "Disabled", GroupName = "b", IsChecked = true, IsEnabled = false },
            },
        }),
        new("group box", true, () => new GroupBox { Header = "Search mode", Width = 220, Height = 80, Content = new TextBlock { Text = "inside" } }),
        new("combo boxes", true, () => new StackPanel
        {
            Width = 220,
            Children = { new ComboBox { Items = { "Markdown (*.md)" }, SelectedIndex = 0 }, new ComboBox { Items = { "Off" }, SelectedIndex = 0, IsEnabled = false } },
        }),
        new("progress bar", true, () => new ProgressBar { Width = 200, Height = 8, Value = 40 }),
        new("scroll bars", true, () => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new ScrollBar { Orientation = Orientation.Vertical, Height = 120, Maximum = 100, ViewportSize = 30, Value = 20 },
                new ScrollBar { Orientation = Orientation.Horizontal, Width = 200, Maximum = 100, ViewportSize = 30, Value = 60, VerticalAlignment = VerticalAlignment.Top },
            },
        }),
        new("labels, link, separator", true, () => new StackPanel
        {
            Width = 220,
            Children =
            {
                new Label { Content = "_Find what:" }, new TextBlock { Inlines = { new Run("See the "), new Hyperlink(new Run("MIT License")) } },
                new Separator(), new Label { Content = "Disabled", IsEnabled = false },
            },
        }),
        new("tree view", true, () => new TreeView
        {
            Width = 200,
            Items =
            {
                new TreeViewItem { Header = "This PC", IsExpanded = true, Items = { new TreeViewItem { Header = "Documents", IsSelected = true }, new TreeViewItem { Header = "Music", Items = { "x" } } } },
            },
        }),
        // The source view: AvalonEdit's own ScrollViewer, so its scroll bars and corner are WPF's.
        // The text area's colours belong to the document theme; a dark one is set here.
        new("source view scroll bars", true, () => new Source.SourceEditor
        {
            Width = 220, Height = 90, FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x27, 0x2E)), Foreground = Brushes.Gainsboro,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            Text = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i} of a document long enough to scroll sideways")),
        }),
        new("list view", false, () =>
        {
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Name", Width = 120 });
            view.Columns.Add(new GridViewColumn { Header = "Size", Width = 60 });
            return new ListView { View = view, Width = 220, Items = { "notes.md", "todo.md" }, SelectedIndex = 1 };
        }),
    ];

    // ContextMenu and ToolTip refuse a parent, so they are drawn as roots with their own resources.
    private static FrameworkElement Host(FrameworkElement control, Action<ResourceDictionary> resources)
    {
        if (control is ContextMenu or ToolTip)
        {
            resources(control.Resources);
            return control;
        }
        var host = new Border { Child = control, Padding = new Thickness(4) };
        host.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);
        host.SetResourceReference(TextElement.ForegroundProperty, SystemColors.WindowTextBrushKey);
        resources(host.Resources);
        return host;
    }

    private static Shot Draw(FrameworkElement root)
    {
        root.Measure(new Size(600, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        root.UpdateLayout();
        var (w, h) = ((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight));
        Assert.True(w > 0 && h > 0, "nothing was laid out");
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(pixels, w * 4, 0);
        return new Shot(w, h, pixels);
    }

    private static Shot DrawSample(Sample sample, Action<ResourceDictionary> resources) => Draw(Host(sample.Make(), resources));

    private static string? Difference(Shot expected, Shot actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height)
            return $"size {actual.Width}x{actual.Height}, expected {expected.Width}x{expected.Height}";
        int count = 0, first = -1;
        for (var i = 0; i < expected.Pixels.Length; i += 4)
            if (expected.Pixels[i] != actual.Pixels[i] || expected.Pixels[i + 1] != actual.Pixels[i + 1]
                || expected.Pixels[i + 2] != actual.Pixels[i + 2] || expected.Pixels[i + 3] != actual.Pixels[i + 3])
            {
                count++;
                if (first < 0) first = i / 4;
            }
        return count == 0 ? null
            : $"{count} of {expected.Width * expected.Height} pixels differ, first at ({first % expected.Width},{first / expected.Width})";
    }

    private static void Aero2(ResourceDictionary resources) { }
    private static void Light(ResourceDictionary resources) => ChromePalette.Apply(resources, dark: false, highContrast: false);
    private static void Dark(ResourceDictionary resources) => ChromePalette.Apply(resources, dark: true, highContrast: false);

    /// <summary>The dark mode dictionary built on the LIGHT palette, with the SystemColors keys
    /// set from this machine's own colours: what the dark templates draw when handed Aero2's
    /// colours.</summary>
    private static void DarkTemplatesLightPalette(ResourceDictionary resources)
    {
        var mode = new ChromeDarkMode(new ChromeLightPalette());
        foreach (var (systemKey, _) in ChromePalette.SystemColorTokens)
        {
            var name = typeof(SystemColors).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Single(p => p.PropertyType == typeof(ResourceKey) && Equals(p.GetValue(null), systemKey)).Name;
            mode[systemKey] = typeof(SystemColors).GetProperty(name[..^"Key".Length])!.GetValue(null)!;
        }
        resources.MergedDictionaries.Add(mode);
    }

    private static void ForEachSample(Func<Sample, bool> include, Action<Sample, List<string>> check)
    {
        ChromePaletteTests.RunSta(() =>
        {
            var failures = new List<string>();
            var count = 0;
            foreach (var sample in Samples().Where(include))
            {
                count++;
                check(sample, failures);
            }
            Assert.True(count > 0, "no samples ran");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        });
    }

    [Aero2Fact]
    public void TheLightPaletteLeavesEveryControlDrawnExactlyAsAero2()
    {
        ForEachSample(_ => true, (sample, failures) =>
        {
            if (Difference(DrawSample(sample, Aero2), DrawSample(sample, Light)) is { } diff) failures.Add($"{sample.Name}: {diff}");
        });
    }

    [Aero2Fact]
    public void TheDarkTemplatesWithTheLightPaletteDrawAero2PixelForPixel()
    {
        ForEachSample(s => s.Faithful, (sample, failures) =>
        {
            if (Difference(DrawSample(sample, Aero2), DrawSample(sample, DarkTemplatesLightPalette)) is { } diff) failures.Add($"{sample.Name}: {diff}");
        });
    }

    [Aero2Fact]
    public void SwitchingToDarkAndBackRestoresAero2OnTheSameControls()
    {
        ForEachSample(_ => true, (sample, failures) =>
        {
            var aero2 = DrawSample(sample, Aero2);
            ResourceDictionary? resources = null;
            var root = Host(sample.Make(), r => { resources = r; Dark(r); });
            var dark = Draw(root);
            if (Difference(aero2, dark) is null) failures.Add($"{sample.Name}: dark drew exactly like Aero2");
            Light(resources!);
            if (Difference(aero2, Draw(root)) is { } diff) failures.Add($"{sample.Name} after dark then light: {diff}");
        });
    }

    [Aero2Fact]
    public void DarkModeLeavesNoControlLight()
    {
        ForEachSample(_ => true, (sample, failures) =>
        {
            var shot = DrawSample(sample, Dark);
            // Transparent pixels (shadow margins) count as the dark window behind them.
            double sum = 0; int bright = 0, n = shot.Width * shot.Height;
            for (var i = 0; i < shot.Pixels.Length; i += 4)
            {
                var a = shot.Pixels[i + 3] / 255.0;
                double Channel(int offset) => Linear(shot.Pixels[i + offset] / 255.0 + (1 - a) * (0x20 / 255.0));
                var luminance = 0.2126 * Channel(2) + 0.7152 * Channel(1) + 0.0722 * Channel(0);
                sum += luminance;
                if (luminance > 0.45) bright++;
            }
            if (sum / n > 0.15 || bright > n * 0.2)
                failures.Add($"{sample.Name}: mean luminance {sum / n:0.000}, {100.0 * bright / n:0}% bright pixels");
        });

        static double Linear(double s) => s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    [Aero2Fact]
    public void DarkModeReachesThePopupsThatAreNotDrawnUntilOpened()
    {
        ChromePaletteTests.RunSta(() =>
        {
            var palette = new ChromeDarkPalette();
            Color Token(string key) => ((SolidColorBrush)palette[key]).Color;

            var header = Item("_File", Item("_New"));
            var submenu = Item("_Recent", Item("a.md"));
            header.Items.Add(submenu);
            var combo = new ComboBox { Items = { "a" } };
            var toolbar = new ToolBar { Width = 60, Items = { new Button { Content = "One" }, new Button { Content = "Two" } } };
            Draw(Host(new StackPanel { Children = { new Menu { Items = { header } }, combo, toolbar } }, Dark));
            submenu.ApplyTemplate();

            foreach (var (part, owner, key) in new (string, Control, string)[]
                     {
                         ("SubMenuBorder", header, "Chrome.Menu.Popup.Background"), ("SubMenuBorder", submenu, "Chrome.Menu.Popup.Background"),
                         ("DropDownBorder", combo, "Chrome.Window.Background"), ("ToolBarSubMenuBorder", toolbar, "Chrome.ToolBar.Background"),
                     })
            {
                var border = Assert.IsType<Border>(owner.Template.FindName(part, owner));
                Assert.True(Token(key) == ((SolidColorBrush)border.Background).Color, $"{owner.GetType().Name} {part} is not {key}");
            }

            // The file list's rows: Aero2's GridView style would colour them navy.
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Name", Width = 120 });
            var files = new ListView { View = view, Items = { "notes.md" } };
            Draw(Host(files, Dark));
            Assert.Equal(Token("Chrome.Text"), ((SolidColorBrush)files.Foreground).Color);

            // A dialog button: the dark template, not Aero2's light blue hover and pressed fills.
            var button = new Button { Content = "OK" };
            var buttonHost = Host(button, Dark);
            Draw(buttonHost);
            Assert.Same(buttonHost.FindResource("Chrome.ButtonTemplate"), button.Template);
        });
    }

    [Aero2Fact]
    public void TheToolbarPicturesAreTheOnesItShowedBeforeInLightAndLightInkInDark()
    {
        ChromePaletteTests.RunSta(() =>
        {
            foreach (var (key, file) in new[] { (ChromeKeys.IconNumberedList, "numbered-list-64.png"), (ChromeKeys.IconSpellCheck, "spellcheck-64.png") })
            {
                Image Picture() => new() { Width = 16, Height = 16, Stretch = Stretch.Fill };
                var before = Picture();
                before.Source = BitmapFrame.Create(new Uri($"pack://application:,,,/MarkdownMidget;component/Assets/{file}"));
                RenderOptions.SetBitmapScalingMode(before, BitmapScalingMode.HighQuality);
                var light = Picture();
                light.SetResourceReference(Image.SourceProperty, key);
                RenderOptions.SetBitmapScalingMode(light, BitmapScalingMode.HighQuality);
                var dark = Picture();
                dark.SetResourceReference(Image.SourceProperty, key);

                Assert.Null(Difference(Draw(Host(before, Aero2)), Draw(Host(light, Light))));
                // On the dark window the original ink all but vanishes; the dark palette's copy is light.
                var original = Picture();
                original.Source = BitmapFrame.Create(new Uri($"pack://application:,,,/MarkdownMidget;component/Assets/{file}"));
                Assert.True(Brightest(Draw(Host(original, Dark))) < 0.4, $"{file} was expected to be dark ink");
                Assert.True(Brightest(Draw(Host(dark, Dark))) > 0.6, $"{key} in dark mode is not light ink");
            }
        });

        static double Brightest(Shot shot)
        {
            double max = 0;
            for (var i = 0; i < shot.Pixels.Length; i += 4)
                max = Math.Max(max, (0.2126 * shot.Pixels[i + 2] + 0.7152 * shot.Pixels[i + 1] + 0.0722 * shot.Pixels[i]) / 255.0);
            return max;
        }
    }

    [Aero2Fact]
    public void ThePickerNavButtonsFollowASwitchMadeWhileTheyAreOpen()
    {
        ChromePaletteTests.RunSta(() =>
        {
            // The picker's nav button style as it was before the chrome work: sizes and the fade only.
            var before = new Style(typeof(ButtonBase));
            foreach (var (property, value) in new (DependencyProperty, object)[]
                     {
                         (Control.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")), (Control.FontSizeProperty, 14.0),
                         (FrameworkElement.WidthProperty, 28.0), (FrameworkElement.HeightProperty, 26.0),
                         (Control.PaddingProperty, new Thickness(0)), (FrameworkElement.MarginProperty, new Thickness(0, 0, 3, 0)),
                     })
                before.Setters.Add(new Setter(property, value));
            before.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.35) } });
            // One enabled button and one disabled (Back with no history).
            FrameworkElement Pair(Action<Button> style)
            {
                var (enabled, disabled) = (new Button { Content = "" }, new Button { Content = "", IsEnabled = false });
                style(enabled);
                style(disabled);
                return new StackPanel { Orientation = Orientation.Horizontal, Children = { enabled, disabled } };
            }
            var expected = Draw(Host(Pair(b => b.Style = before), Aero2));

            ResourceDictionary? resources = null;
            var buttons = new List<Button>();
            var root = Host(Pair(b => { b.SetResourceReference(FrameworkElement.StyleProperty, ChromeKeys.PickerNavButton); buttons.Add(b); }),
                            r => { resources = r; Light(r); });
            Assert.Null(Difference(expected, Draw(root)));   // light: as the picker drew before

            Dark(resources!);   // Windows switches while the picker is open
            Draw(root);
            var face = ((SolidColorBrush)new ChromeDarkPalette()["Chrome.Button.Background"]).Color;
            var template = root.FindResource("Chrome.ButtonTemplate");   // the dark button template, so hover and pressed are dark too
            foreach (var b in buttons)
            {
                Assert.Equal(face, Assert.IsType<SolidColorBrush>(b.Background).Color);
                Assert.Same(template, b.Template);
            }
            Assert.Equal((1.0, 0.35), (buttons[0].Opacity, buttons[1].Opacity));

            Light(resources!);
            Assert.Null(Difference(expected, Draw(root)));
        });
    }
}
