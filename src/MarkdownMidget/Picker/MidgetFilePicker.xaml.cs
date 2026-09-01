using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
/// extension rules, sorting, type-ahead, breadcrumbs) lives in
/// <see cref="FilePickerModel"/>; this class is the shell around it and the
/// only part that touches the disk.
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
        public string Modified { get; init; } = "";
        public string Size { get; init; } = "";
    }

    private readonly FilePickerRequest _request;
    private readonly IReadOnlyList<FilterGroup> _filters;
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

        foreach (var group in _filters) FilterCombo.Items.Add(group);
        if (FilterCombo.Items.Count > 0)
        {
            var index = Math.Clamp(request.FilterIndex - 1, 0, FilterCombo.Items.Count - 1);
            _navigating = true;                 // don't re-list before the first Navigate
            FilterCombo.SelectedIndex = index;
            _navigating = false;
        }
        FilterCombo.IsEnabled = FilterCombo.Items.Count > 1;

        BuildPlaces();
        Loaded += (_, _) =>
        {
            Navigate(FirstDirectory(), addToHistory: true);
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private FilterGroup? SelectedFilter => FilterCombo.SelectedItem as FilterGroup;

    /// <summary>Where the dialog opens: the caller's folder, else the requested
    /// file's folder, else Documents — never a path that no longer exists.</summary>
    private string FirstDirectory()
    {
        if (!string.IsNullOrEmpty(_request.InitialDirectory) && Directory.Exists(_request.InitialDirectory))
            return _request.InitialDirectory;
        foreach (var recent in _request.RecentFolders)
            if (Directory.Exists(recent)) return recent;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(docs) ? docs : Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    }

    // ===== places tree =====

    private void BuildPlaces()
    {
        void AddSection(string header, IEnumerable<string> paths)
        {
            var any = false;
            foreach (var path in paths.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
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
                PlacesTree.Items.Add(MakeNode(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path));
            }
        }

        AddSection("Places", new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        });
        AddSection("Recent folders", _request.RecentFolders.Take(5));
        AddSection("Drives", DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => d.RootDirectory.FullName));
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
        if (_navigating) return;
        if (e.NewValue is TreeViewItem { Tag: string path }) Navigate(path, addToHistory: true);
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
    /// Save would then land somewhere the user never saw.</summary>
    private bool Navigate(string path, bool addToHistory)
    {
        if (!Directory.Exists(path)) return false;
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
                    Modified = SafeWriteTime(dir),
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
                    Modified = SafeWriteTime(file),
                    Size = length >= 0 ? FilePickerModel.FormatSize(length) : "",
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't list that folder:\n{ex.Message}",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        entries.Sort((a, b) => FilePickerModel.CompareEntries(a.IsDirectory, a.Name, b.IsDirectory, b.Name));

        _currentDirectory = target;   // committed only now, with a view to match
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

    private static string SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path).ToString("g"); } catch { return ""; }
    }

    private void UpdateNavButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = Directory.GetParent(_currentDirectory) is not null;
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

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        // A selected FILE fills the name box; a selected folder must not, or
        // Save would offer to write a file named after a directory.
        if (FileList.SelectedItem is Entry { IsDirectory: false } entry) NameBox.Text = entry.Name;
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is Entry entry) Activate(entry);
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
