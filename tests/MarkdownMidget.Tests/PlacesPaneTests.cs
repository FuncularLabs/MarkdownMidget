using System.IO;
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
    private static void Click(TreeViewItem section) => Part<UIElement>(section, "PART_Header").RaiseEvent(
        new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
    private static IExpandCollapseProvider Automation(PlacesPane pane, int section) => (IExpandCollapseProvider)
        UIElementAutomationPeer.CreatePeerForElement(pane.Tree).GetChildren()[section].GetPattern(PatternInterface.ExpandCollapse);
    private static void Written(PickerViewStore store) => store.UpdateLater(_ => { }).Wait();   // after the pane's queued writes

    private static bool Press(TreeViewItem item, Key key)   // as when it has the focus; true when the pane took the key
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(item), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        item.RaiseEvent(args);
        return args.Handled;
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
        File.WriteAllText(path, """{"Version":1,"Default":null,"Folders":[]}""");   // as 1.0.1 writes it
        Assert.Empty(store.Load().Collapsed);
        Assert.True(store.Update(f => PickerViews.SetCollapsed(f, "Recent", true)) && PickerViews.Ours(File.ReadAllText(path)));
        Assert.Equal(["Recent"], store.Load().Collapsed);
        const string newer = """{"Version":2,"Collapsed":["Drives"]}""";
        File.WriteAllText(path, newer);
        Assert.Empty(store.Load().Collapsed);
        Assert.False(store.Update(f => PickerViews.SetCollapsed(f, "Recent", false)));
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.Equal(["Drives"], PickerViews.Parse("""{"Version":1,"Collapsed":[null,"Drives","Drives"," "]}""").Collapsed);
    }

    [Fact]
    public void TheCardBrushesAreInBothPalettesAndReadAgainstTheirSurfaces() => ChromePaletteTests.RunSta(() =>
    {
        // Floors under the ratios measured: header text on the card, the card's edge and its shading on the pane.
        foreach (var (palette, text, edge, shade) in new (ResourceDictionary, double, double, double)[]
                 { (new ChromeLightPalette(), 19.5, 1.6, 1.05), (new ChromeDarkPalette(), 11.5, 1.8, 1.1) })
        {
            Color C(string key) => ((SolidColorBrush)palette[key]).Color;
            var (card, window) = (C(ChromeKeys.PickerSectionBackground), C("Chrome.Window.Background"));
            Assert.InRange(SourcePalette.ContrastRatio(C("Chrome.Text"), card), text, 21);
            Assert.InRange(SourcePalette.ContrastRatio(C(ChromeKeys.PickerSectionBorder), window), edge, 3);
            Assert.InRange(SourcePalette.ContrastRatio(card, window), shade, 1.3);
        }
    });

    [Fact]
    public void TheCardsDrawWithThePaletteAndWithSystemColoursInHighContrast()
    {
        static Border Card(PlacesPane pane) => Part<Border>(Section(pane, "Places"), "Card");
        static (Color, Color) Colours(Border card) => (((SolidColorBrush)card.Background).Color, ((SolidColorBrush)card.BorderBrush).Color);
        static Color Light(string key) => ((SolidColorBrush)new ChromeLightPalette()[key]).Color;
        OnPane((pane, _) =>
        {
            Assert.Equal((Light(ChromeKeys.PickerSectionBackground), Light(ChromeKeys.PickerSectionBorder)), Colours(Card(pane)));
            Assert.True(Card(pane).CornerRadius.TopLeft > 0 && Card(pane).BorderThickness.Left == 1);
        });
        OnPane((pane, _) => Assert.Equal((SystemColors.WindowColor, SystemColors.WindowTextColor), Colours(Card(pane))),
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
