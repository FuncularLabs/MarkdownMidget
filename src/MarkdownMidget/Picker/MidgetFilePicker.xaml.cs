using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace MarkdownMidget.Picker;

/// <summary>
/// The built-in file picker: a pure-WPF Open/Save dialog that loads no shell
/// extensions, because navigation here is nothing but System.IO.
///
/// Modelled on Avalonia's ManagedFileChooser (MIT) — the one maintained
/// precedent for a framework shipping a managed dialog as the fallback when the
/// native picker can't be trusted. Deliberately ABSENT: per-file shell icons,
/// thumbnails, preview pane and shell context menus. Those are exactly the
/// third-party code paths that crash the native dialog, so leaving them out is
/// the entire point rather than a shortcut; folder/file glyphs come from the
/// extension instead.
///
/// The logic worth testing (filter parsing and matching, typed-path resolution,
/// extension and retyping rules, sorting, type-ahead) lives in
/// <see cref="FilePickerModel"/>, and the Type column's names in
/// <see cref="FileTypeNames"/>; this class is the shell around them and the
/// only part that touches the disk or the registry. The address bar is a plain editable path
/// box — clickable breadcrumb segments were designed but not built.
/// </summary>
public partial class MidgetFilePicker : Window
{
    /// <summary>One row of the list.</summary>
    private sealed class Entry
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
        public required bool IsDirectory { get; init; }
        public string Display => IsDirectory ? "📁  " + Name : "📄  " + Name;
        public DateTime? ModifiedAt { get; init; }
        public string Modified => ModifiedAt?.ToLocalTime().ToString("g") ?? "";   // UTC, so the clock going back can't reorder
        public long Bytes { get; init; } = -1;   // a folder, or a size that couldn't be read
        public string Size => Bytes >= 0 ? FilePickerModel.FormatSize(Bytes) : "";
        public string Type { get; init; } = "";
        public PickerSortKey Key => new(IsDirectory, Name, ModifiedAt, Bytes, Type);
    }

    private readonly FilePickerRequest _request;
    private readonly IReadOnlyList<FilterGroup> _filters;
    /// <summary>The Type column's names for this picker, from HKEY_CLASSES_ROOT, read only.</summary>
    private readonly FileTypeNames _types = new((subKey, valueName) =>
    {
        using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(subKey);
        return key?.GetValue(valueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
    });
    private readonly GridViewColumn[] _sortColumns;   // in PickerColumn order
    private readonly double[] _builtInWidths;         // the XAML's, for a folder with no view remembered
    private PickerSort _sort = PickerSort.Default;     // the shown folder's, from ShowView
    /// <summary>Remembered views (#12, item 4), one store for every picker in this process,
    /// so their writes queue in order; and this picker's copy, changed as it saves.</summary>
    private static readonly PickerViewStore s_views = new(PickerViewStore.DefaultDirectory);
    private PickerViewsFile _views = s_views.Load();
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _currentDirectory = "";
    private bool _navigating;          // suppress feedback loops while we retarget the UI
    private string _typeAhead = "";
    private DateTime _typeAheadAt = DateTime.MinValue;

    /// <summary>The chosen path once the dialog returns true.</summary>
    public string? SelectedPath { get; private set; }

    internal MidgetFilePicker(FilePickerRequest request)
    {
        InitializeComponent();
        _request = request;
        _filters = FilePickerModel.ParseFilter(request.Filter);

        Title = request.Title ?? (request.Save ? "Save As" : "Open");
        AcceptButton.Content = request.Save ? "_Save" : "_Open";
        NewFolderButton.Visibility = request.Save ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Text = request.FileName ?? "";
        PickerNote.Text = FilePickerModel.NoteText(FilePickerService.UseBuiltIn);

        foreach (var group in _filters) FilterCombo.Items.Add(group);
        if (FilterCombo.Items.Count > 0)
        {
            var index = Math.Clamp(request.FilterIndex - 1, 0, FilterCombo.Items.Count - 1);
            _navigating = true;                 // don't re-list before the first Navigate
            FilterCombo.SelectedIndex = index;
            _navigating = false;
        }
        FilterCombo.IsEnabled = FilterCombo.Items.Count > 1;
        _sortColumns = [NameColumn, ModifiedColumn, TypeColumn, SizeColumn];
        _builtInWidths = [.. _sortColumns.Select(c => c.Width)];
        ShowSortHeaders();
        PickerRows.OnColumnResized(FileList, RememberView);

        BuildPlaces();
        Loaded += (_, _) =>
        {
            // Off the UI thread, once per process. Directory.Exists can't be the probe: it says no to a folder it may not read.
            _ = s_views.CleanOnce(folder => PickerViews.Gone(folder, root => new DriveInfo(root).DriveType, root => new DriveInfo(root).IsReady,
                path => { try { return File.GetAttributes(path); } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; } }));
            OpenFirstListableDirectory();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private FilterGroup? SelectedFilter => FilterCombo.SelectedItem as FilterGroup;

    /// <summary>
    /// Land on the first folder that actually LISTS. Existence is not enough:
    /// a folder can exist and refuse enumeration (another user's profile, a
    /// share that errors), and leaving the dialog with no current directory is
    /// worse than any of these candidates — a save typed into that state would
    /// resolve against the process working directory and write somewhere the
    /// user never chose.
    /// </summary>
    private void OpenFirstListableDirectory()
    {
        foreach (var candidate in StartCandidates())
            if (!string.IsNullOrEmpty(candidate) && Navigate(candidate, addToHistory: true, quiet: true))
                return;

        // Nothing at all could be listed. Say so plainly and take the accept
        // buttons away rather than leaving a dialog that looks usable.
        MessageBox.Show(this,
            "No folder could be opened — not the one asked for, and not Documents, " +
            "your user folder or any drive. Something is wrong with this machine's " +
            "file system or permissions.",
            "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
        AcceptButton.IsEnabled = false;
        NewFolderButton.IsEnabled = false;
    }

    private IEnumerable<string> StartCandidates()
    {
        yield return _request.InitialDirectory ?? "";
        foreach (var recent in _request.RecentFolders) yield return recent;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        foreach (var drive in DriveInfo.GetDrives())
        {
            string? root = null;
            try { if (drive.IsReady) root = drive.RootDirectory.FullName; } catch { }
            if (root is not null) yield return root;
        }
    }

    // ===== places tree =====

    private void BuildPlaces()
    {
        // Quick access is shown unchecked, its full paths in tooltips: a pinned folder that has
        // gone fails when clicked, as any other, and a network one isn't touched before that.
        void AddSection(string header, IEnumerable<string> paths, bool quickAccess = false)
        {
            var any = false;
            foreach (var path in paths.Where(p => quickAccess || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!any)
                {
                    PlacesTree.Items.Add(new TreeViewItem
                    {
                        Header = header,
                        IsEnabled = false,
                        FontWeight = FontWeights.SemiBold,
                        Focusable = false,
                    });
                    any = true;
                }
                var node = MakeNode(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path);
                if (quickAccess) node.ToolTip = path;
                PlacesTree.Items.Add(node);
            }
        }

        AddSection("Places", new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        });
        AddSection("Quick access", PinnedFolders(), quickAccess: true);
        AddSection("Recent folders", _request.RecentFolders.Take(5));
        AddSection("Drives", DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.RootDirectory.FullName));
    }

    private static Task<IReadOnlyList<string>>? s_pinnedRead;   // pickers open on the UI thread, one at a time

    /// <summary>Explorer's pinned folders (#12, item 5), given half a second: a redirected AppData can
    /// sit on a slow share, and the picker must open regardless. The read stops itself at that
    /// deadline, and while one is still running a new picker waits on it rather than start another.</summary>
    private static IReadOnlyList<string> PinnedFolders()
    {
        if (s_pinnedRead is not { IsCompleted: false })
        {
            var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            s_pinnedRead = Task.Run(() => { using (deadline) return QuickAccess.PinnedFolders(QuickAccess.JumpListPath, deadline.Token); });
        }
        return s_pinnedRead.Wait(TimeSpan.FromMilliseconds(500)) ? s_pinnedRead.Result : [];
    }

    /// <summary>A tree node with a placeholder child, so the arrow shows without
    /// walking the whole disk up front. Real children arrive on expand.</summary>
    private static TreeViewItem MakeNode(string path, string label)
    {
        var node = new TreeViewItem { Header = label, Tag = path };
        node.Items.Add("…");   // placeholder: replaced on first expand
        return node;
    }

    private void PlacesTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem node || node.Tag is not string path) return;
        if (node.Items.Count != 1 || node.Items[0] is not string) return;   // already populated
        node.Items.Clear();
        foreach (var dir in SafeDirectories(path))
            node.Items.Add(MakeNode(dir, Path.GetFileName(dir)));
    }

    private void PlacesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A place not there now (deleted, or on an offline share: telling them apart would wait twice)
        // says so; then the node is let go, so a second click tries again.
        if (_navigating || e.NewValue is not TreeViewItem { Tag: string path } node || Navigate(path, addToHistory: true, sayIfMissing: true)) return;
        Dispatcher.InvokeAsync(() => { _navigating = true; node.IsSelected = false; _navigating = false; });
    }

    // ===== listing =====

    private IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path)
                .Where(Visible)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return []; }   // denied or gone: an empty branch beats an exception
    }

    private bool Visible(string path)
    {
        if (HiddenCheck.IsChecked == true) return true;
        try
        {
            var attrs = File.GetAttributes(path);
            return !attrs.HasFlag(FileAttributes.Hidden) && !attrs.HasFlag(FileAttributes.System);
        }
        catch { return false; }
    }

    /// <summary>Show <paramref name="path"/>. Returns false when it could not be
    /// listed, in which case NOTHING moves: a folder that exists but refuses to
    /// enumerate (another user's profile, a share that errors) must not become
    /// the current directory behind a view still showing the old one - a later
    /// Save would then land somewhere the user never saw. <paramref name="quiet"/>
    /// suppresses the error box while walking candidate start folders, where a
    /// failure is expected and the next candidate is the answer.</summary>
    private bool Navigate(string path, bool addToHistory, bool quiet = false, bool sayIfMissing = false)
    {
        if (!Directory.Exists(path)) { if (sayIfMissing) MessageBox.Show(this, "That folder doesn't exist.", "Markdown Midget"); return false; }
        var target = Path.GetFullPath(path);

        var entries = new List<Entry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(target).Where(Visible))
            {
                entries.Add(new Entry
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    IsDirectory = true,
                    ModifiedAt = SafeWriteTime(dir),
                    Type = FileTypeNames.Folder,
                });
            }
            var filter = SelectedFilter;
            foreach (var file in Directory.EnumerateFiles(target).Where(Visible))
            {
                var name = Path.GetFileName(file);
                if (filter is not null && !FilePickerModel.MatchesFilter(name, filter)) continue;
                long length = -1;
                try { length = new FileInfo(file).Length; } catch { /* unreadable: no size */ }
                entries.Add(new Entry
                {
                    Name = name,
                    FullPath = file,
                    IsDirectory = false,
                    ModifiedAt = SafeWriteTime(file),
                    Bytes = length,
                    Type = _types.Of(name, isDirectory: false),
                });
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
                MessageBox.Show(this, $"Couldn't list that folder:\n{ex.Message}",
                    "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var view = PickerViews.Resolve(_views, target) ?? new PickerView(PickerSort.Default, _builtInWidths);
        entries.Sort((a, b) => FilePickerModel.CompareEntries(a.Key, b.Key, view.Sort));

        _currentDirectory = target;   // committed only now, with a view to match
        ShowView(view);
        var now = DateTime.UtcNow;
        if (PickerViews.Touch(_views, target, now)) _ = s_views.UpdateLater(f => PickerViews.Touch(f, target, now));
        // A successful listing clears the "nothing could be opened" verdict. That
        // state is environmental (a downed share holding Documents, no ready
        // drive), so leaving the buttons dead after the user navigated somewhere
        // real would be a transient outage written as a permanent mark.
        AcceptButton.IsEnabled = true;
        NewFolderButton.IsEnabled = true;
        _navigating = true;
        FileList.ItemsSource = entries;
        AddressBox.Text = _currentDirectory;
        _navigating = false;

        if (addToHistory)
        {
            // A new branch discards the forward history, exactly like a browser.
            if (_historyIndex < _history.Count - 1)
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
            if (_history.Count == 0 || !string.Equals(_history[^1], _currentDirectory, StringComparison.OrdinalIgnoreCase))
                _history.Add(_currentDirectory);
            _historyIndex = _history.Count - 1;
        }
        UpdateNavButtons();
        return true;
    }

    private static DateTime? SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return null; }
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = _currentDirectory.Length > 0 && Directory.GetParent(_currentDirectory) is not null;
    }

    // ===== navigation commands =====

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0) return;
        // Move the cursor only if the step lands: a deleted folder in the history
        // would otherwise consume the Back press and leave the index pointing at
        // somewhere we are not.
        if (Navigate(_history[_historyIndex - 1], addToHistory: false)) _historyIndex--;
        UpdateNavButtons();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0 || _historyIndex >= _history.Count - 1) return;
        if (Navigate(_history[_historyIndex + 1], addToHistory: false)) _historyIndex++;
        UpdateNavButtons();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        // GetParent("") throws, and that is reachable from the Alt+Up accelerator
        // when no folder could be opened at all.
        if (_currentDirectory.Length == 0) return;
        if (Directory.GetParent(_currentDirectory) is { } parent) Navigate(parent.FullName, addToHistory: true);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Navigate(_currentDirectory, addToHistory: false);

    private void Hidden_Click(object sender, RoutedEventArgs e) => Navigate(_currentDirectory, addToHistory: false);

    private void FilterCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating || _currentDirectory.Length == 0) return;
        // Save mode only, like Windows' own dialog: in Open mode the box holds a
        // selection, and rewriting it would fight the user.
        if (_request.Save)
            NameBox.Text = FilePickerModel.RetypeForFilter(NameBox.Text.Trim(), SelectedFilter);
        Navigate(_currentDirectory, addToHistory: false);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var resolved = FilePickerModel.ResolveTypedPath(AddressBox.Text, _currentDirectory);
        if (resolved is not null && Directory.Exists(resolved))
        {
            Navigate(resolved, addToHistory: true);
            FileList.Focus();
        }
        else if (resolved is not null && File.Exists(resolved))
        {
            Choose(resolved);
        }
        else
        {
            MessageBox.Show(this, "That folder doesn't exist.", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Information);
            AddressBox.Text = _currentDirectory;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Explorer's navigation keys, on the window so they work from any pane.
        if (e.Key == Key.F5) { Refresh_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            switch (e.SystemKey)
            {
                case Key.Left: Back_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.Right: Forward_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.Up: Up_Click(this, new RoutedEventArgs()); e.Handled = true; return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    // ===== list interaction =====

    /// <summary>A header click sorts by that column, or turns it round. The list itself is
    /// re-sorted, not a view over it, so type-ahead, the arrow keys, Enter and double-click
    /// all go by the order on screen.</summary>
    private void FileList_HeaderClick(object sender, RoutedEventArgs e)
    {
        var column = Array.IndexOf(_sortColumns, (e.OriginalSource as GridViewColumnHeader)?.Column);
        if (column < 0) return;   // the blank header past the last column
        _sort = _sort.Click((PickerColumn)column);
        ShowSortHeaders();
        RememberView();
        if (FileList.ItemsSource is not List<Entry> entries) return;
        _navigating = true;       // the same row stays selected, and the name box keeps what was typed
        PickerRows.Resort(FileList, entries, (a, b) => FilePickerModel.CompareEntries(a.Key, b.Key, _sort));
        _navigating = false;
    }

    private void ShowSortHeaders()
    {
        for (var i = 0; i < _sortColumns.Length; i++)
            _sortColumns[i].Header = FilePickerModel.HeaderText((PickerColumn)i, _sort);
    }

    private void ShowView(PickerView view)
    {
        _sort = view.Sort;
        for (var i = 0; i < _sortColumns.Length; i++) _sortColumns[i].Width = view.Widths[i];
        ShowSortHeaders();
    }

    /// <summary>Saves the list's look for the folder on show (PickerViews.Apply says whether as
    /// the default or the folder's own): in this picker's copy now, in the file in the background.
    /// The file decides whether there was a default yet, and the copy then becomes the file as written.</summary>
    private void RememberView()
    {
        if (_currentDirectory.Length == 0) return;
        var (folder, now) = (_currentDirectory, DateTime.UtcNow);
        var view = new PickerView(_sort, [.. _sortColumns.Select(c => c.ActualWidth)]);
        PickerViews.Apply(_views, folder, view, now);
        _ = s_views.UpdateLater(f => PickerViews.Apply(f, folder, view, now), written => Dispatcher.InvokeAsync(() => _views = written));
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        // A selected FILE fills the name box; a selected folder must not, or
        // Save would offer to write a file named after a directory.
        if (FileList.SelectedItem is Entry { IsDirectory: false } entry) NameBox.Text = entry.Name;
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PickerRows.DoubleClickedItem(FileList, e.ChangedButton, e.OriginalSource) is Entry entry) Activate(entry);
    }

    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FileList.SelectedItem is Entry entry)
        {
            e.Handled = true;
            Activate(entry);
        }
        else if (e.Key == Key.Back)
        {
            e.Handled = true;
            Up_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Type-ahead: letters jump to the next entry starting with what was
    /// typed, and repeating the same prefix walks the matches.</summary>
    private void FileList_TextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        var now = DateTime.UtcNow;
        _typeAhead = now - _typeAheadAt > TimeSpan.FromSeconds(1) ? e.Text : _typeAhead + e.Text;
        _typeAheadAt = now;

        if (FileList.ItemsSource is not IEnumerable<Entry> source) return;
        var names = source.Select(x => x.Name).ToList();
        var index = FilePickerModel.FindByPrefix(names, _typeAhead, FileList.SelectedIndex);
        if (index < 0) return;
        FileList.SelectedIndex = index;
        FileList.ScrollIntoView(FileList.SelectedItem);
        e.Handled = true;
    }

    private void Activate(Entry entry)
    {
        if (entry.IsDirectory) Navigate(entry.FullPath, addToHistory: true);
        else Choose(entry.FullPath);
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Accept_Click(this, new RoutedEventArgs());
    }

    // ===== accept =====

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        // With no folder open, a relative name would resolve against the PROCESS
        // working directory - writing somewhere the user never saw, which is the
        // whole harm the deferred-commit fix exists to prevent.
        if (_currentDirectory.Length == 0) return;
        var typed = NameBox.Text.Trim();
        if (typed.Length == 0)
        {
            // Nothing typed, but a folder is highlighted: treat Open as "enter it",
            // which is what double-click would have done.
            if (FileList.SelectedItem is Entry { IsDirectory: true } dir) Navigate(dir.FullPath, addToHistory: true);
            return;
        }

        var resolved = FilePickerModel.ResolveTypedPath(typed, _currentDirectory);
        if (resolved is null)
        {
            MessageBox.Show(this, "That name can't be used as a file name.", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // A typed folder navigates rather than "opening" it as a document.
        if (Directory.Exists(resolved))
        {
            Navigate(resolved, addToHistory: true);
            NameBox.Clear();
            return;
        }

        resolved = FilePickerModel.EnsureExtension(resolved, SelectedFilter, _request.DefaultExt);
        Choose(resolved);
    }

    private void Choose(string path)
    {
        // A folder is never an answer - including the case that only becomes a
        // folder after the extension was applied ("notes" beside a directory
        // called "notes.md"). Enter it instead, which is what the user meant.
        if (Directory.Exists(path))
        {
            Navigate(path, addToHistory: true);
            NameBox.Clear();
            return;
        }
        if (_request.Save)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "That folder doesn't exist.", "Markdown Midget",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (File.Exists(path) && MessageBox.Show(this,
                    $"{Path.GetFileName(path)} already exists.\nReplace it?", "Markdown Midget",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        else if (_request.CheckFileExists && !File.Exists(path))
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)} wasn't found in this folder.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPath = path;
        DialogResult = true;
        Close();
    }

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDirectory.Length == 0) return;   // same reason as Accept_Click
        var dlg = InputDialog.Single(this, "New Folder", "Folder name", "New folder");
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value1.Trim();
        if (name.Length == 0) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "That name can't be used as a folder name.", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var created = Path.Combine(_currentDirectory, name);
            Directory.CreateDirectory(created);
            Navigate(created, addToHistory: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't create the folder:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
