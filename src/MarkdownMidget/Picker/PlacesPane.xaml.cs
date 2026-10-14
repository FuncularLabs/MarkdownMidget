using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MarkdownMidget.Picker;

/// <summary>One folder in the places pane; Tip is its tooltip.</summary>
internal sealed record PlaceEntry(string Path, string Label, string? Tip = null);

/// <summary>One section of the places pane. Id is what picker-views.json keeps it collapsed by.</summary>
internal sealed record PlaceSection(string Id, string Header, IReadOnlyList<PlaceEntry> Entries);

/// <summary>The built-in picker's left pane: Places (Home first), Quick access, Recent folders and Drives, each a card
/// whose larger header collapses it by click, Enter, Space, Left and Right, or automation's ExpandCollapse. The sections
/// are the top-level items of one TreeView, so Up and Down walk the whole pane. A collapsed section is kept in
/// picker-views.json, each change written as itself, so a window's stale copy never erases another's.</summary>
public partial class PlacesPane : UserControl
{
    private PickerViewStore? _store;

    public PlacesPane() => InitializeComponent();

    /// <summary>Shows a chosen folder; false when it couldn't, and the entry is then let go, so a second click tries again.</summary>
    internal Func<string, bool> Open { get; set; } = _ => true;

    /// <summary>A folder's subfolders, read when its node is first expanded.</summary>
    internal Func<string, IEnumerable<string>> Subfolders { get; set; } = _ => [];

    /// <summary>The sections in order, the empty ones left out. Places, Recent folders and Drives list only full paths that
    /// <paramref name="exists"/> says are there. Quick access is unchecked, its full paths in tooltips: a pinned folder that
    /// has gone fails when clicked, as any other, and a network one isn't touched before that. Home's tooltip is its path.</summary>
    internal static IReadOnlyList<PlaceSection> Sections(Func<Environment.SpecialFolder, string> folder, Func<string, bool> exists,
        IEnumerable<string> pinned, IEnumerable<string> recent, IEnumerable<string> drives)
    {
        var home = folder(Environment.SpecialFolder.UserProfile);
        List<PlaceEntry> Entries(IEnumerable<string> paths, bool check = true, bool tips = false) =>
        [
            .. paths.Where(p => !check || Path.IsPathFullyQualified(p) && exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => new PlaceEntry(p, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : p, tips ? p : null)),
        ];
        var places = Entries([home, folder(Environment.SpecialFolder.Desktop), folder(Environment.SpecialFolder.MyDocuments), Path.Combine(home, "Downloads")]);
        return new PlaceSection[]
        {
            new("Places", "Places", [.. places.Select(e => e.Path == home ? e with { Label = "Home", Tip = home } : e)]),
            new("QuickAccess", "Quick access", Entries(pinned, check: false, tips: true)),
            new("Recent", "Recent folders", Entries(recent.Take(5))),
            new("Drives", "Drives", Entries(drives)),
        }.Where(s => s.Entries.Count > 0).ToList();
    }

    /// <summary>Fills the pane, each section collapsed if <paramref name="collapsed"/> names it; a section
    /// opened or shut is then written to <paramref name="store"/>.</summary>
    internal void Show(IReadOnlyList<PlaceSection> sections, IEnumerable<string> collapsed, PickerViewStore store)
    {
        _store = store;
        var shut = collapsed.ToHashSet(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            // Expanded before it joins the tree, so building it writes nothing.
            var item = new TreeViewItem { Header = section.Header, Tag = section, IsExpanded = !shut.Contains(section.Id), Style = (Style)Resources["PlacesSection"] };
            foreach (var entry in section.Entries) item.Items.Add(Node(entry.Path, entry.Label, entry.Tip));
            Tree.Items.Add(item);
        }
    }

    /// <summary>A folder node with a placeholder child, so the arrow shows without walking the disk up front.</summary>
    private static TreeViewItem Node(string path, string label, string? tip = null)
    {
        var node = new TreeViewItem { Header = label, Tag = path, ToolTip = tip };
        node.Items.Add("…");   // placeholder: replaced on first expand
        return node;
    }

    private void Tree_ExpandedOrCollapsed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { Tag: PlaceSection section } item)
        {
            var (id, shut) = (section.Id, !item.IsExpanded);
            _ = _store?.UpdateLater(f => PickerViews.SetCollapsed(f, id, shut));
            return;
        }
        if (e.OriginalSource is not TreeViewItem { Tag: string path, IsExpanded: true } node || node.Items.Count != 1 || node.Items[0] is not string) return;
        node.Items.Clear();
        foreach (var dir in Subfolders(path))
            node.Items.Add(Node(dir, Path.GetFileName(dir)));
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A place not there now (deleted, or on an offline share: telling them apart would wait twice)
        // says so in Open; then the node is let go, so a second click tries again. Letting go selects nothing.
        if (e.NewValue is not TreeViewItem { Tag: string path } node || Open(path)) return;
        Dispatcher.InvokeAsync(() => node.IsSelected = false);
    }

    /// <summary>Enter and Space toggle a section, and Left shuts an open one and Right opens a shut one, as the tree's own
    /// keys would: all of a header's keys in one place. Every other key, and these on an entry or with Ctrl, Shift or
    /// Alt, are the tree's, except numpad *: the tree's would open out the selected item's whole subtree, reading every
    /// folder under it, every drive's included, on the UI thread.</summary>
    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Multiply) { e.Handled = true; return; }
        if (e.OriginalSource is not TreeViewItem { Tag: PlaceSection } item || e.KeyboardDevice.Modifiers != ModifierKeys.None) return;
        bool? open = e.Key switch
        {
            Key.Enter or Key.Space => !item.IsExpanded,
            Key.Left when item.IsExpanded => false,
            Key.Right when !item.IsExpanded => true,
            _ => null,
        };
        if (open is not { } value) return;
        item.IsExpanded = value;
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).TemplatedParent is not TreeViewItem item) return;
        e.Handled = true;               // or the tree's own double-click would toggle it back
        if (e.ClickCount % 2 == 0) return;   // a double-click's second half: one toggle, one write; fast clicks count on, odd ones toggle
        item.IsExpanded = !item.IsExpanded;
        item.Focus();   // the keyboard carries on from the header clicked
    }
}
