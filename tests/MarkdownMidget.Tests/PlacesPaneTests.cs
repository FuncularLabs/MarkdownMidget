using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownMidget.Chrome;
using MarkdownMidget.Picker;
using MarkdownMidget.Source;
using Xunit;
using static System.Environment;

namespace MarkdownMidget.Tests;

/// <summary>The built-in picker's left pane: sections in order, Home first; headers that collapse by mouse, keys and automation,
/// kept in picker-views.json; the cards. Off screen, from stand-in paths, never the real profile; picker-views.json in a temp folder.</summary>
[Collection("WpfSta")]
public sealed class PlacesPaneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mdm-places-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static string Folder(SpecialFolder folder) => folder switch { SpecialFolder.UserProfile => @"C:\Users\Pat",
        SpecialFolder.Desktop => @"C:\Users\Pat\Desktop", SpecialFolder.MyDocuments => @"C:\Users\Pat\Documents", _ => "" };

    /// <summary>A pane of <paramref name="sections"/> (by default all four) in the light palette, with a new store, as a new process has.</summary>
    private void OnPane(Action<PlacesPane, PickerViewStore> body, IReadOnlyList<PlaceSection>? sections = null,
                        double height = 500, Action<Window>? setUp = null) => ChromePaletteTests.RunSta(() =>
    {
        Window? window = null;
        try
        {
            var (pane, store) = (new PlacesPane(), new PickerViewStore(_dir));
            window = OffscreenWindow.Create(pane, 240, height);
            window.Resources.MergedDictionaries.Add(new ChromeLightPalette());
            setUp?.Invoke(window);
            pane.Show(sections ?? PlacesPane.Sections(Folder, _ => true, [@"\\server\share\Pinned"], [@"D:\Work"], [@"C:\", @"D:\"]),
                store.Load().Collapsed, store);
            window.Show();
            pane.UpdateLayout();
            body(pane, store);
        }
        finally { window?.Close(); }
    });

    private static TreeViewItem Section(PlacesPane pane, string id) => pane.Tree.Items.Cast<TreeViewItem>().Single(s => ((PlaceSection)s.Tag).Id == id);
    private static TreeViewItem Entry(TreeViewItem section, int index) => (TreeViewItem)section.Items[index];
    private static T Part<T>(TreeViewItem item, string name) where T : class => (T)item.Template.FindName(name, item);
    private static TextBlock Label(TreeViewItem item) =>   // a section's header text, or an entry's
        PickerRowsTests.Find<TextBlock>(Part<DependencyObject>(item, "PART_Header"), t => t.Text == (string)item.Header)!;
    private static bool Click(TreeViewItem section, int clicks = 1) => LeftDown(Part<UIElement>(section, "PART_Header"), clicks);
    private static IExpandCollapseProvider Automation(PlacesPane pane, int section) => (IExpandCollapseProvider)
        UIElementAutomationPeer.CreatePeerForElement(pane.Tree).GetChildren()[section].GetPattern(PatternInterface.ExpandCollapse);
    private static void Written(PickerViewStore store) => store.UpdateLater(_ => { }).Wait();   // after the pane's queued writes

    /// <summary>A left press as Windows delivers it: MouseDown, which WPF raises again as MouseLeftButtonDown along its route.</summary>
    private static bool LeftDown(UIElement target, int clicks)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent };
        typeof(MouseButtonEventArgs).GetProperty(nameof(args.ClickCount))!.SetValue(args, clicks);   // its setter is internal
        target.RaiseEvent(args);
        return args.Handled;
    }

    /// <summary>A key pressed on <paramref name="item"/>, as when it has the focus: PreviewKeyDown, and then KeyDown if
    /// <paramref name="down"/> and nothing took it. True when the pane took it.</summary>
    private static bool Press(TreeViewItem item, Key key, KeyboardDevice? keyboard = null, bool down = false)
    {
        var args = new KeyEventArgs(keyboard ?? Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        item.RaiseEvent(args);
        var taken = args.Handled;
        if (down && !taken) { args.RoutedEvent = Keyboard.KeyDownEvent; item.RaiseEvent(args); }
        return taken;
    }

    /// <summary>A keyboard with Ctrl held, for the key events a test raises; the real keyboard is never read.</summary>
    private sealed class CtrlHeld() : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key == Key.LeftCtrl ? KeyStates.Down : KeyStates.None;
    }

    [Fact]
    public void PlacesStartsWithHomeAndTheSectionsComeInOrder()
    {
        var sections = PlacesPane.Sections(Folder, _ => true, [@"\\server\share\Pinned", @"\\SERVER\share\pinned"],
            [@"D:\R1", @"D:\R2", @"D:\R3", @"D:\R4", @"D:\R5", @"D:\R6"], [@"C:\"]);
        Assert.Equal(["Places", "Quick access", "Recent folders", "Drives"], sections.Select(s => s.Header));
        Assert.Equal(["Places", "QuickAccess", "Recent", "Drives"], sections.Select(s => s.Id));
        Assert.Equal(new (string, string, string?)[] { (@"C:\Users\Pat", "Home", @"C:\Users\Pat"), (@"C:\Users\Pat\Desktop", "Desktop", null),
            (@"C:\Users\Pat\Documents", "Documents", null), (@"C:\Users\Pat\Downloads", "Downloads", null) }, sections[0].Entries.Select(e => (e.Path, e.Label, e.Tip)));
        Assert.Equal(("Pinned", @"\\server\share\Pinned"), (Assert.Single(sections[1].Entries).Label, sections[1].Entries[0].Tip));
        Assert.Equal([@"D:\R1", @"D:\R2", @"D:\R3", @"D:\R4", @"D:\R5"], sections[2].Entries.Select(e => e.Path));
        Assert.Equal((@"C:\", @"C:\"), (sections[3].Entries[0].Path, sections[3].Entries[0].Label));
    }

    [Fact]
    public void ASectionWithNothingInItIsLeftOutAndSoIsAPlaceThatIsNotThere()
    {
        var sections = PlacesPane.Sections(f => f == SpecialFolder.Desktop ? "" : Folder(f), p => p is not (@"C:\Users\Pat\Downloads" or @"D:\Gone"), [], [@"D:\Gone"], [@"C:\"]);
        Assert.Equal(["Places", "Drives"], sections.Select(s => s.Id));
        Assert.Equal(["Home", "Documents"], sections[0].Entries.Select(e => e.Label));
        Assert.Empty(PlacesPane.Sections(_ => "", _ => true, [], [], []));   // no profile: no Home, and no Downloads in the working folder
        OnPane((pane, _) => Assert.Equal(["Places", "Drives"], pane.Tree.Items.Cast<TreeViewItem>().Select(s => (string)s.Header)), sections);
    }

    [Fact]
    public void HeadersAreLargerAndSemiboldAndTheirEntriesSitIndentedWithSpaceBetweenSections() => OnPane((pane, _) =>
    {
        var (places, quick) = (Section(pane, "Places"), Section(pane, "QuickAccess"));
        var (head, home) = (Label(places), Label(Entry(places, 0)));
        Assert.True(head.FontSize >= home.FontSize + 2, $"header {head.FontSize}px, entry {home.FontSize}px");
        Assert.Equal((FontWeights.SemiBold, FontWeights.Normal), (head.FontWeight, home.FontWeight));
        Assert.Equal(((SolidColorBrush)home.Foreground).Color, ((SolidColorBrush)head.Foreground).Color);   // not dimmed
        Assert.Equal(("Home", @"C:\Users\Pat"), (home.Text, Entry(places, 0).ToolTip));
        var indent = home.TranslatePoint(default, pane).X - head.TranslatePoint(default, pane).X;
        Assert.True(indent >= 10, $"Home's text is {indent}px right of the header's");
        var gap = quick.TranslatePoint(default, pane).Y - places.TranslatePoint(new Point(0, places.ActualHeight), pane).Y;
        Assert.True(gap >= 6, $"{gap}px between two sections");
    });

    [Fact]
    public void AHeaderTogglesOnAClickOrOnEnterOrSpaceAndItsChevronTurns() => OnPane((pane, _) =>
    {
        var quick = Section(pane, "QuickAccess");
        var (chevron, pinned, open) = (Part<TextBlock>(quick, "Chevron"), Entry(quick, 0), Part<TextBlock>(quick, "Chevron").Text);
        Click(quick);
        Assert.Equal((false, false, true), (quick.IsExpanded, pinned.IsVisible, chevron.Text != open));
        Click(quick);
        Assert.Equal((true, true, open), (quick.IsExpanded, pinned.IsVisible, chevron.Text));
        foreach (var key in new[] { Key.Enter, Key.Space })
        {
            Assert.True(Press(quick, key) && !quick.IsExpanded, $"{key} shuts");
            Assert.True(Press(quick, key) && quick.IsExpanded, $"{key} opens");
            Assert.False(Press(pinned, key));   // on an entry the key is the picker's, as before
        }
    });

    [Fact]
    public void LeftCollapsesAndRightExpandsAHeaderAndUpAndDownAreLeftToTheTree() => OnPane((pane, _) =>
    {
        var recent = Section(pane, "Recent");
        Assert.False(Press(recent, Key.Right));   // open already: the tree's own Right moves into it
        Assert.True(Press(recent, Key.Left) && !recent.IsExpanded);
        Assert.False(Press(recent, Key.Left));
        Assert.True(Press(recent, Key.Right) && recent.IsExpanded);
        Assert.False(Press(recent, Key.Down) || Press(recent, Key.Up));
        Assert.True(recent.IsExpanded);
    });

    [Fact]
    public void WithCtrlHeldAHeadersKeysAreLeftToTheTree() => OnPane((pane, _) =>
    {
        var (places, ctrl) = (Section(pane, "Places"), new CtrlHeld());
        Assert.False(Press(places, Key.Enter, ctrl) || Press(places, Key.Space, ctrl) || Press(places, Key.Left, ctrl));
        Assert.True(places.IsExpanded);
    });

    [Fact]
    public void ADoubleClickOnAHeaderTogglesItOnceAndTheTreeNeverSeesIt() => OnPane((pane, store) =>
    {
        var drives = Section(pane, "Drives");
        Assert.True(Click(drives, 1) && Click(drives, 2));   // taken, so the tree's own double-click doesn't toggle it back
        Written(store);
        Assert.False(drives.IsExpanded);
        Assert.Equal(["Drives"], store.Load().Collapsed);
    });

    [Fact]
    public void FastClicksOnAHeaderToggleItOnEveryOddCountAndWriteOnlyThen() => OnPane((pane, store) =>
    {
        var (drives, toggles) = (Section(pane, "Drives"), 0);
        drives.Expanded += (_, _) => toggles++;   // each open or shut is the one write the pane makes
        drives.Collapsed += (_, _) => toggles++;
        // One fast series: WPF counts on (3, 4, ...) while the clicks stay within the double-click time.
        foreach (var (clicks, open, toggled) in new[] { (1, false, 1), (2, false, 1), (3, true, 2), (4, true, 2) })
        {
            Assert.True(Click(drives, clicks));
            Written(store);
            Assert.Equal((open, toggled, open ? 0 : 1), (drives.IsExpanded, toggles, store.Load().Collapsed.Count));
        }
    });

    [Fact]
    public void AClickOrDoubleClickOnACardsBlankSpaceDoesNothing() => OnPane((pane, store) =>
    {
        var (places, home) = (Section(pane, "Places"), Entry(Section(pane, "Places"), 0));
        home.IsSelected = true;
        var (card, row) = (places.TranslatePoint(default, pane), home.TranslatePoint(new Point(0, home.ActualHeight / 2), pane).Y);
        // Right of Home's label, in the gutter left of it, and in the card's bottom padding, hit as the mouse hits.
        foreach (var point in new Point[] { new(card.X + places.ActualWidth - 6, row), new(card.X + 8, row), new(card.X + 40, card.Y + places.ActualHeight - 2) })
            foreach (var clicks in new[] { 1, 2 })
                LeftDown(Assert.IsAssignableFrom<UIElement>(pane.InputHitTest(point)), clicks);
        Written(store);
        Assert.Equal((true, true, false), (places.IsExpanded, home.IsSelected, places.IsSelected));
        Assert.Empty(store.Load().Collapsed);
    });

    [Fact]
    public void NumpadStarOpensNothingOutUnderAHeaderOrAnEntry() => OnPane((pane, _) =>
    {
        var asked = new List<string>();
        pane.Subfolders = path => { asked.Add(path); return path.Length < 60 ? [path + @"\Deeper"] : []; };   // a deep stand-in
        var taken = new List<bool>();
        foreach (var item in new[] { Section(pane, "Places"), Entry(Section(pane, "Places"), 2) })
        {
            item.IsSelected = true;   // as focus leaves it: the tree's own star opens out the selected item
            taken.Add(Press(item, Key.Multiply, down: true));
        }
        Assert.Empty(asked);   // WPF's star would read every folder under them on the UI thread
        Assert.Equal([true, true], taken);
    });

    [Fact]
    public void ASelectedHeaderIsHighlightedAsTheKeyboardsPlace() => OnPane((pane, _) =>
    {
        var places = Section(pane, "Places");
        Color Head() => ((SolidColorBrush)Part<Border>(places, "PART_Header").Background).Color;
        Color Edge() => ((SolidColorBrush)Part<Border>(places, "PART_Header").BorderBrush).Color;
        Assert.Equal((Colors.Transparent, Colors.Transparent), (Head(), Edge()));
        places.IsSelected = true;   // the tree without the keyboard focus: a fill as pale as the strip, so an edge too
        Assert.Equal((SystemColors.InactiveSelectionHighlightBrush.Color, SystemColors.ControlDarkColor), (Head(), Edge()));
        var active = typeof(System.Windows.Controls.Primitives.Selector).GetField("IsSelectionActivePropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        pane.Tree.SetValue((DependencyPropertyKey)active.GetValue(null)!, true);   // as the tree has while it holds the focus
        Assert.Equal((SystemColors.HighlightColor, SystemColors.HighlightTextColor, Colors.Transparent),
            (Head(), ((SolidColorBrush)Label(places).Foreground).Color, Edge()));
    });

    [Fact]
    public void AutomationReadsEachHeadersNameAndWhetherItIsExpanded() => OnPane((pane, _) =>
    {
        var items = UIElementAutomationPeer.CreatePeerForElement(pane.Tree).GetChildren();
        Assert.Equal(["Places", "Quick access", "Recent folders", "Drives"], items.Select(p => p.GetName()));
        var quick = Automation(pane, 1);
        Assert.Equal(ExpandCollapseState.Expanded, quick.ExpandCollapseState);
        quick.Collapse();
        Assert.Equal((ExpandCollapseState.Collapsed, false), (quick.ExpandCollapseState, Section(pane, "QuickAccess").IsExpanded));
        quick.Expand();
        Assert.True(Section(pane, "QuickAccess").IsExpanded);
    });

    [Fact]
    public void CollapsedSectionsAreKeptAfterARestartAndAStaleWindowsSavesDoNotEraseThem()
    {
        OnPane((pane, store) =>
        {
            Click(Section(pane, "QuickAccess"));
            Automation(pane, 3).Collapse();   // Drives, the way Narrator would
            Written(store);
        });
        OnPane((pane, store) =>   // a new store: the next picker, or one after a restart
        {
            Assert.Equal([true, false, true, false], pane.Tree.Items.Cast<TreeViewItem>().Select(s => s.IsExpanded));
            new PickerViewStore(_dir).Update(f => PickerViews.SetCollapsed(f, "Recent", true));   // another window, after this one opened
            _ = store.UpdateLater(f => PickerViews.Apply(f, @"C:\Alpha", new PickerView(PickerSort.Default, [250, 130, 110, 70]), DateTime.UtcNow));
            Press(Section(pane, "Drives"), Key.Right);
            Written(store);
        });
        var file = new PickerViewStore(_dir).Load();
        Assert.Equal(["QuickAccess", "Recent"], file.Collapsed);
        Assert.NotNull(file.Default);   // the view was saved too
    }

    [Fact]
    public void TheCollapsedSectionsRideInVersion1AndTheVersionGuardIsUnchanged()
    {
        Directory.CreateDirectory(_dir);
        var (path, store) = (Path.Combine(_dir, PickerViewStore.FileName), new PickerViewStore(_dir, _ => { }));
        File.WriteAllText(path, """{"Version":1,"Default":null,"Folders":[]}""");   // as 1.0.1 and 1.0.2 write it
        Assert.Empty(store.Load().Collapsed);
        Assert.True(store.Update(f => PickerViews.SetCollapsed(f, "Recent", true)) && PickerViews.Ours(File.ReadAllText(path)));
        Assert.Equal(["Recent"], store.Load().Collapsed);
        const string newer = """{"Version":2,"Collapsed":["Drives"]}""";
        File.WriteAllText(path, newer);
        Assert.Empty(store.Load().Collapsed);
        Assert.False(store.Update(f => PickerViews.SetCollapsed(f, "Recent", false)));
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.Equal(["Drives"], PickerViews.Parse("""{"Version":1,"Collapsed":[null,"Drives","Drives"," "]}""").Collapsed);
        Assert.Equal(["Drives", "Recent"], PickerViews.Parse("""{"Version":1,"Collapsed":[1,"Drives",{"a":[2]},["x"],"Recent"]}""").Collapsed);
    }

    [Theory]
    [InlineData("\"Drives\"")]
    [InlineData("[1]")]
    [InlineData("{\"Drives\":true}")]
    public void ACollapsedListOfTheWrongShapeReadsAsNoneAndTheViewsSurviveTheNextWrite(string collapsed)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, PickerViewStore.FileName), $$"""
            {"Version":1,"Default":{"Column":"Size","Widths":[250,130,110,70]},
             "Folders":[{"Folder":"C:\\Alpha","Column":"Type","Widths":[250,130,110,70]}],"Collapsed":{{collapsed}}}
            """);
        var store = new PickerViewStore(_dir, _ => { });
        Assert.Empty(store.Load().Collapsed);
        Assert.True(store.Update(f => PickerViews.Apply(f, @"C:\Beta", new PickerView(new PickerSort(PickerColumn.Name, true), [250, 130, 110, 70]), DateTime.UtcNow)));
        var file = store.Load();
        Assert.Equal("Size", file.Default?.Column);
        Assert.Equal([@"C:\Alpha", @"C:\Beta"], file.Folders.Select(f => f.Folder));
    }

    [Fact]
    public void TheCardBrushesAreInBothPalettesAndReadAgainstTheirSurfaces() => ChromePaletteTests.RunSta(() =>
    {
        // Floors under the ratios measured, light then dark: header text on its strip 17.86, 10.12; the strip on the pane
        // 1.18, 1.29; the edge on the pane 1.92, 2.19; a selected header on the strip 3.83, 2.18, and an inactive one's edge
        // 2.22, 1.83; a selected entry on the card's body 4.50, 2.54, and an inactive one 1.14, 1.36.
        foreach (var (palette, text, strip) in new (ResourceDictionary, double, double)[] { (new ChromeLightPalette(), 17.5, 1.15), (new ChromeDarkPalette(), 10, 1.25) })
        {
            void AtLeast(double floor, string fore, string back)
            {
                var ratio = SourcePalette.ContrastRatio(((SolidColorBrush)palette[fore]).Color, ((SolidColorBrush)palette[back]).Color);
                Assert.True(ratio >= floor, $"{palette.GetType().Name}: {fore} on {back} is {ratio:0.00}, under {floor}");
            }
            var (head, body, pane) = (ChromeKeys.PickerSectionHeader, ChromeKeys.PickerSectionBackground, "Chrome.Window.Background");
            AtLeast(text, "Chrome.Text", head); AtLeast(strip, head, pane); AtLeast(1.8, ChromeKeys.PickerSectionBorder, pane);
            AtLeast(2, "Chrome.Selection.Background", head); AtLeast(1.8, "Chrome.Control.Dark", head);   // the inactive one's edge
            AtLeast(2.5, "Chrome.Selection.Background", body); AtLeast(1.1, "Chrome.Selection.Inactive.Background", body);
        }
    });

    [Fact]
    public void TheCardsDrawWithThePaletteAndWithSystemColoursInHighContrast()
    {
        static Border Card(PlacesPane pane, string part = "Card") => Part<Border>(Section(pane, "Places"), part);
        static (Color, Color, Color) Colours(PlacesPane pane) => (((SolidColorBrush)Card(pane).Background).Color,
            ((SolidColorBrush)Card(pane).BorderBrush).Color, ((SolidColorBrush)Card(pane, "Strip").Background).Color);
        static Color Light(string key) => ((SolidColorBrush)new ChromeLightPalette()[key]).Color;
        OnPane((pane, _) =>
        {
            Assert.Equal((Light(ChromeKeys.PickerSectionBackground), Light(ChromeKeys.PickerSectionBorder), Light(ChromeKeys.PickerSectionHeader)), Colours(pane));
            var (card, strip) = (Card(pane), Card(pane, "Strip"));
            Assert.True(card.CornerRadius.TopLeft > 0 && card.BorderThickness.Left == 1);
            // The header's strip: the card's full width inside its edge, the header's height, rounded at the top only.
            Assert.Equal(card.ActualWidth - 2, strip.ActualWidth, 1);
            Assert.Equal(Card(pane, "PART_Header").ActualHeight, strip.ActualHeight, 1);
            Assert.True(strip.CornerRadius.TopLeft > 0 && strip.CornerRadius.TopRight > 0 && strip.CornerRadius.BottomLeft == 0);
        });
        OnPane((pane, _) => Assert.Equal((SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.ControlColor), Colours(pane)),
            setUp: window => window.Resources[SystemParameters.HighContrastKey] = true);
    }

    [Fact]
    public void ChoosingAnEntryOpensItAndOneThatCannotBeOpenedIsLetGo() => OnPane((pane, _) =>
    {
        var (opened, works) = (new List<string>(), true);
        pane.Open = path => { opened.Add(path); return works; };
        Entry(Section(pane, "Places"), 1).IsSelected = true;
        works = false;
        var gone = Entry(Section(pane, "QuickAccess"), 0);
        gone.IsSelected = true;
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Assert.False(gone.IsSelected);   // let go, so a second click tries again
        Section(pane, "Places").IsSelected = true;   // a header opens nothing
        Assert.Equal([@"C:\Users\Pat\Desktop", @"\\server\share\Pinned"], opened);
    });

    [Fact]
    public void AFolderEntryStillOpensOutToItsSubfoldersAndReadsThemOnce() => OnPane((pane, store) =>
    {
        var asked = new List<string>();
        pane.Subfolders = path => { asked.Add(path); return [path + @"\One", path + @"\Two"]; };
        var documents = Entry(Section(pane, "Places"), 2);
        foreach (var open in new[] { true, false, true }) documents.IsExpanded = open;
        Assert.Equal([@"C:\Users\Pat\Documents"], asked);
        Assert.Equal(["One", "Two"], documents.Items.Cast<TreeViewItem>().Select(n => (string)n.Header));
        Written(store);
        Assert.Empty(store.Load().Collapsed);   // a folder's arrow is not a section's
    });

    [Fact]
    public void AShortPaneScrollsItsSections() =>
        OnPane((pane, _) => Assert.True(PickerRowsTests.Find<ScrollViewer>(pane.Tree)!.ScrollableHeight > 0), height: 150);
}
