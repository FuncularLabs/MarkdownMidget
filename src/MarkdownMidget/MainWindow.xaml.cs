using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace MarkdownMidget;

/// <summary>
/// Main window. Hosts the Milkdown WYSIWYG surface in a WebView2 and provides a
/// WordPad-style menu/toolbar plus a toggleable raw-markdown source view.
/// </summary>
public partial class MainWindow : Window
{
    private const string VirtualHost = "markdownmidget.invalid";
    private const string AppVersion = "v0.1-alpha1";

    // Segoe Fluent Icons glyphs for the source/WYSIWYG toggle.
    private static readonly string GlyphSource = char.ConvertFromUtf32(0xE943); // braces {} = markdown source
    private static readonly string GlyphRich = char.ConvertFromUtf32(0xE8A5);   // document = formatted view

    private string? _currentPath;
    private bool _dirty;
    private bool _editorReady;
    private bool _sourceMode;
    private bool _syncingStyle;
    private bool _showMarks;

    // Dirty tracking by content comparison: the document is "unchanged" whenever it
    // matches the last opened/saved markdown — so undoing back to that state clears
    // the modified flag, and undo past the Open state is impossible (history flushed).
    private string _cleanMarkdown = string.Empty;
    private bool _suppressDirty;
    private string? _pendingOpenPath;
    private bool _startReadOnly;
    private bool _isHelpWindow;
    private bool _readOnly;
    private readonly DispatcherTimer _dirtyTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow()
    {
        InitializeComponent();
        RegisterShortcuts();
        SourceToggle.Content = GlyphSource; // start in WYSIWYG; button offers source view
        SourceBox.SpellCheck.IsEnabled = true; // match the editor's default
        _dirtyTimer.Tick += async (_, _) => { _dirtyTimer.Stop(); await UpdateDirtyAsync(); };

        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (arg is "--readonly" or "-r" or "/readonly") _startReadOnly = true;
            else if (arg is "--help-window") { _isHelpWindow = true; _startReadOnly = true; }
            else if (File.Exists(arg)) _pendingOpenPath ??= arg;
        }

        if (_isHelpWindow) MenuViewHelp.IsEnabled = false; // no help-of-help

        Loaded += async (_, _) => await InitializeEditorAsync();
        Closing += MainWindow_Closing;
        UpdateTitle();
    }

    // ===== WebView2 / editor bootstrap =====

    private async Task InitializeEditorAsync()
    {
        var wwwroot = ExtractEmbeddedEditor();

        // Keep the user-data folder out of Program Files / the app directory.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkdownMidget", "WebView2");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);

        var core = Web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;

        // Lock down the host shell: it is a local app, not a browser.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Let dropped files bubble to the window's drop handler instead of the WebView.
        Web.AllowExternalDrop = false;

        // Per-launch nonce defeats WebView2's disk cache so a rebuilt editor bundle
        // is always loaded fresh (the bundle refs inside index.html are also hashed).
        core.Navigate($"https://{VirtualHost}/index.html?n={Guid.NewGuid():N}");
    }

    /// <summary>
    /// Writes the embedded editor bundle to a local folder and returns its path.
    /// Embedding (rather than shipping a loose wwwroot) lets a self-contained
    /// publish stay a single file; WebView2 still needs the assets on disk to map.
    /// </summary>
    private static string ExtractEmbeddedEditor()
    {
        const string prefix = "wwwroot/";
        var asm = Assembly.GetExecutingAssembly();
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarkdownMidget", "editor");
        Directory.CreateDirectory(target);

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            var dest = Path.Combine(target, name[prefix.Length..]);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var file = File.Create(dest);
            stream.CopyTo(file);
        }

        return target;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
        }
        catch
        {
            return;
        }

        switch (type)
        {
            case "loaded":
                // Bridge is wired; hand the editor its initial (empty) document.
                _ = RunEditorAsync($"window.MDM.create({JsLiteral(string.Empty)})");
                break;
            case "ready":
                _editorReady = true;
                if (_pendingOpenPath is { } p)
                {
                    _pendingOpenPath = null;
                    _ = OpenPathAsync(p);
                }
                else
                {
                    _ = SetCleanBaselineAsync();
                }
                if (_startReadOnly) SetReadOnly(true);
                break;
            case "change":
                if (!_sourceMode)
                    ScheduleDirtyCheck();
                break;
            case "selection":
                // Reflect the block type at the cursor in the Style dropdown.
                if (!_sourceMode)
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    if (d.RootElement.TryGetProperty("style", out var s))
                        SyncStyleCombo(s.GetString() ?? "paragraph");
                }
                break;
            case "history":
                if (!_sourceMode)
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    SetUndoRedoEnabled(
                        d.RootElement.TryGetProperty("canUndo", out var cu) && cu.GetBoolean(),
                        d.RootElement.TryGetProperty("canRedo", out var cr) && cr.GetBoolean());
                }
                break;
        }
    }

    /// <summary>Runs JS in the editor and returns its (JSON-decoded string) result.</summary>
    private async Task<string?> RunEditorAsync(string script)
    {
        if (Web.CoreWebView2 is null) return null;
        var raw = await Web.CoreWebView2.ExecuteScriptAsync(script);
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        try { return JsonSerializer.Deserialize<string>(raw); }
        catch { return raw; }
    }

    /// <summary>Applies a formatting/style command to whichever surface is active.</summary>
    private void EditorCommand(string name)
    {
        if (_readOnly) return;
        if (_sourceMode)
        {
            SourceFormat.Apply(SourceBox, name);
            return;
        }
        if (!_editorReady) return;
        _ = RunEditorAsync($"window.MDM.cmd({JsLiteral(name)})");
        RefocusEditor();
    }

    /// <summary>Inserts a markdown fragment (link/image) into the active surface.</summary>
    private void InsertMarkdownFragment(string md)
    {
        if (_readOnly || string.IsNullOrEmpty(md)) return;
        if (_sourceMode)
        {
            SourceBox.SelectedText = md;
            SourceBox.Focus();
        }
        else if (_editorReady)
        {
            _ = RunEditorAsync($"window.MDM.insertMarkdown({JsLiteral(md)})");
        }
        RefocusEditor();
    }

    private void InsertCodeBlock(string language)
    {
        if (_readOnly) return;
        if (_sourceMode)
        {
            SourceFormat.InsertCodeBlock(SourceBox, language);
            return;
        }
        if (!_editorReady) return;
        _ = RunEditorAsync($"window.MDM.cmd({JsLiteral("codeblock")}, {JsLiteral(language)})");
        RefocusEditor();
    }

    /// <summary>
    /// Returns focus to the active editing surface after a toolbar/menu action so the
    /// caret and selection stay put and the user can keep typing immediately.
    /// </summary>
    private void RefocusEditor()
    {
        if (_sourceMode) SourceBox.Focus();
        else Web.Focus(); // MDM.cmd/insertMarkdown already restores the DOM caret in JS
    }

    private static string JsLiteral(string value) => JsonSerializer.Serialize(value);

    private async Task<string> GetDocumentMarkdownAsync()
    {
        if (_sourceMode) return SourceBox.Text;
        if (!_editorReady) return string.Empty;
        return await RunEditorAsync("window.MDM.getMarkdown()") ?? string.Empty;
    }

    private async Task SetDocumentMarkdownAsync(string markdown)
    {
        SourceBox.Text = markdown;
        if (_editorReady)
            await RunEditorAsync($"window.MDM.setMarkdown({JsLiteral(markdown)})");
    }

    // ===== Source / WYSIWYG toggle =====

    private async void ToggleSource_Click(object sender, RoutedEventArgs e)
    {
        await SetSourceModeAsync(!_sourceMode);
    }

    private async Task SetSourceModeAsync(bool on)
    {
        if (on == _sourceMode) return;

        if (on)
        {
            // Entering source: pull the latest markdown out of the editor.
            SourceBox.Text = await GetDocumentMarkdownAsync();
            Web.Visibility = Visibility.Collapsed;
            SourceBox.Visibility = Visibility.Visible;
            SourceBox.Focus();
        }
        else
        {
            // Leaving source: push edits back into the WYSIWYG editor.
            await SetDocumentMarkdownAsync(SourceBox.Text);
            SourceBox.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
        }

        _sourceMode = on;
        SourceToggle.IsChecked = on;
        MenuViewSource.IsChecked = on;
        StatusMode.Text = on ? "Markdown source" : "WYSIWYG";

        // The button shows the view it switches TO: in source mode show the
        // document glyph (-> formatted); in WYSIWYG show braces (-> source).
        SourceToggle.Content = on ? GlyphRich : GlyphSource;
        SourceToggle.ToolTip = on
            ? "Switch to formatted view (Ctrl+E)"
            : "Edit markdown source (Ctrl+E)";

        if (on) SetUndoRedoEnabled(true, true); // the source TextBox manages its own undo
        RefocusEditor();
        _ = UpdateDirtyAsync();
    }

    // ===== File operations =====

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var name = _currentPath is null ? "Untitled" : Path.GetFileName(_currentPath);
        var result = MessageBox.Show(
            $"Save changes to {name}?", "Markdown Midget",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result switch
        {
            MessageBoxResult.Yes => await SaveAsync(false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        await LoadDocumentAsync(string.Empty, null);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        var dlg = new OpenFileDialog
        {
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ".md",
        };
        if (dlg.ShowDialog(this) != true) return;
        await OpenPathAsync(dlg.FileName);
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path);
            await LoadDocumentAsync(text, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the file:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Loads markdown into the editor and resets the clean baseline + history.</summary>
    private async Task LoadDocumentAsync(string markdown, string? path)
    {
        _suppressDirty = true;
        await SetDocumentMarkdownAsync(markdown); // setMarkdown flushes undo history
        _currentPath = path;
        _suppressDirty = false;
        await SetCleanBaselineAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task<bool> SaveAsync(bool forcePrompt)
    {
        // Plain Save is disabled in read-only mode (it would overwrite the same file);
        // Save As (forcePrompt) still works so the content can be kept elsewhere.
        if (_readOnly && !forcePrompt) return false;

        var path = _currentPath;
        if (forcePrompt || path is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".md",
                FileName = _currentPath is null ? "Untitled.md" : Path.GetFileName(_currentPath),
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }

        var markdown = await GetDocumentMarkdownAsync();
        try
        {
            await File.WriteAllTextAsync(path, markdown);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save the file:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _currentPath = path;
        _cleanMarkdown = markdown; // new clean baseline; undo history is left intact
        _dirty = false;
        UpdateTitle();
        return true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            e.Cancel = true; // Defer; re-close after the async prompt resolves.
            if (await ConfirmDiscardAsync())
            {
                _dirty = false;
                Close();
            }
        }
    }

    // ===== Edit menu (native shortcuts also work in each surface) =====

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        if (_sourceMode) SourceBox.Undo();
        else if (_editorReady) _ = RunEditorAsync("window.MDM.undo()");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        if (_sourceMode) SourceBox.Redo();
        else if (_editorReady) _ = RunEditorAsync("window.MDM.redo()");
    }

    private void Cut_Click(object sender, RoutedEventArgs e) => EditOrEditor("cut", "document.execCommand('cut')");
    private void Copy_Click(object sender, RoutedEventArgs e) => EditOrEditor("copy", "document.execCommand('copy')");
    private void Paste_Click(object sender, RoutedEventArgs e) => EditOrEditor("paste", null);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => EditOrEditor("selectall", "document.execCommand('selectAll')");

    /// <summary>In source mode operate on the TextBox; in WYSIWYG defer to the editor.</summary>
    private void EditOrEditor(string textBoxAction, string? editorScript)
    {
        if (_readOnly && textBoxAction is "cut" or "paste") return;
        if (_sourceMode)
        {
            switch (textBoxAction)
            {
                case "undo": SourceBox.Undo(); break;
                case "redo": SourceBox.Redo(); break;
                case "cut": SourceBox.Cut(); break;
                case "copy": SourceBox.Copy(); break;
                case "paste": SourceBox.Paste(); break;
                case "selectall": SourceBox.SelectAll(); break;
            }
            return;
        }
        if (editorScript is not null && _editorReady)
            _ = RunEditorAsync(editorScript);
    }

    // ===== Format commands =====

    private void Bold_Click(object sender, RoutedEventArgs e) => EditorCommand("bold");
    private void Italic_Click(object sender, RoutedEventArgs e) => EditorCommand("italic");
    private void Underline_Click(object sender, RoutedEventArgs e) => EditorCommand("underline");
    private void Strike_Click(object sender, RoutedEventArgs e) => EditorCommand("strike");
    private void Code_Click(object sender, RoutedEventArgs e) => EditorCommand("code");
    private void Bullet_Click(object sender, RoutedEventArgs e) => EditorCommand("bullet");
    private void Ordered_Click(object sender, RoutedEventArgs e) => EditorCommand("ordered");
    private void Quote_Click(object sender, RoutedEventArgs e) => EditorCommand("quote");
    private void Hr_Click(object sender, RoutedEventArgs e) => EditorCommand("hr");

    private void StyleCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingStyle) return;
        if (StyleCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            ApplyStyle(tag);
    }

    /// <summary>Applies a block style; "codeblock:&lt;lang&gt;" converts to a code block.</summary>
    private void ApplyStyle(string tag)
    {
        const string codePrefix = "codeblock:";
        if (tag.StartsWith(codePrefix, StringComparison.Ordinal))
            InsertCodeBlock(tag[codePrefix.Length..]);
        else
            EditorCommand(tag);
    }

    // ===== Style menu / focus =====

    private void Style_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
        {
            EditorCommand(tag);
            SyncStyleCombo(tag);
        }
    }

    private void SyncStyleCombo(string tag)
    {
        foreach (var obj in StyleCombo.Items)
            if (obj is ComboBoxItem ci && (string?)ci.Tag == tag)
            {
                _syncingStyle = true;
                StyleCombo.SelectedItem = ci;
                _syncingStyle = false;
                return;
            }
    }

    private void FocusStyle_Click(object sender, RoutedEventArgs e)
    {
        StyleCombo.Focus();
        StyleCombo.IsDropDownOpen = true;
    }

    // ===== Insert: link / picture / code block =====

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        var selected = _sourceMode ? SourceBox.SelectedText : string.Empty;
        var dlg = new InputDialog("Insert Link", "Text", selected, "URL", "https://") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var url = dlg.Value2.Trim();
        if (url.Length == 0) return;
        var text = dlg.Value1.Trim();
        if (text.Length == 0) text = url;
        InsertMarkdownFragment($"[{text}]({url})");
    }

    private async void Picture_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Insert Picture",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var alt = Path.GetFileNameWithoutExtension(dlg.FileName);
        string uri;
        try
        {
            // Embed the image as a base64 data URI so it renders inside the
            // sandboxed WebView (which can't load local file: paths) and travels
            // with the markdown. This bloats the document by design.
            var bytes = await File.ReadAllBytesAsync(dlg.FileName);
            uri = $"data:{MimeForImage(dlg.FileName)};base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read the image:\n{ex.Message}", "Insert Picture",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        InsertMarkdownFragment($"![{alt}]({uri})");
    }

    private static string MimeForImage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private void CodeBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
            InsertCodeBlock(item.Tag as string ?? string.Empty);
    }

    private void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _sourceMode || !_editorReady) return;
        var dlg = new TableDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _ = RunEditorAsync(
            $"window.MDM.insertTable({dlg.Rows}, {dlg.Columns}, {(dlg.HeaderRow ? "true" : "false")})");
        RefocusEditor();
    }

    /// <summary>Opens the code-block language menu beneath the hybrid code button.</summary>
    private void CodeBlockMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.ContextMenu is ContextMenu cm)
        {
            cm.PlacementTarget = b;
            cm.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            cm.IsOpen = true;
        }
    }

    // ===== Misc UI =====

    private void ToggleMarks_Click(object sender, RoutedEventArgs e)
    {
        _showMarks = MarksToggle.IsChecked == true;
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.showMarks({(_showMarks ? "true" : "false")})");
        RefocusEditor();
    }

    private void SpellCheck_Click(object sender, RoutedEventArgs e)
    {
        var on = MenuSpellCheck.IsChecked;
        SourceBox.SpellCheck.IsEnabled = on;
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.setSpellcheck({(on ? "true" : "false")})");
        RefocusEditor();
    }

    // ===== Drag & drop: open in this instance if idle, else launch a new one =====

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // Open the first file here only if this window holds an untitled, unmodified
        // document; otherwise (a file is open, or there are unsaved edits) keep it and
        // open everything in fresh instances.
        var openHere = _currentPath is null && !_dirty;
        for (var i = 0; i < files.Length; i++)
        {
            if (i == 0 && openHere) _ = OpenPathAsync(files[0]);
            else OpenInNewInstance(files[i]);
        }
        Activate();
    }

    private static void OpenInNewInstance(string path, bool readOnly = false, bool helpWindow = false)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var flags = helpWindow ? " --help-window" : (readOnly ? " --readonly" : "");
        try { Process.Start(new ProcessStartInfo(exe, $"\"{path}\"{flags}") { UseShellExecute = false }); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open a new window:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== Read-only mode =====

    private void ReadOnly_Click(object sender, RoutedEventArgs e) => SetReadOnly(MenuReadOnly.IsChecked);

    private void SetReadOnly(bool on)
    {
        _readOnly = on;
        MenuReadOnly.IsChecked = on;
        SourceBox.IsReadOnly = on;
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.setEditable({(on ? "false" : "true")})");

        // Gray out everything that modifies the open file; Save As / Open / New and
        // the view toggles stay usable (Save would overwrite the same file, so it is
        // disabled — Save As to a new file is the read-only escape hatch).
        FormatToolBar.IsEnabled = FormatMenu.IsEnabled = StyleMenu.IsEnabled = InsertMenu.IsEnabled = !on;
        SaveBtn.IsEnabled = SaveMenu.IsEnabled = !on;
        SetUndoRedoEnabled(_canUndo, _canRedo); // re-gate undo/redo for read-only

        StatusMode.Text = on ? (_sourceMode ? "Markdown source (read-only)" : "WYSIWYG (read-only)")
                             : (_sourceMode ? "Markdown source" : "WYSIWYG");
        UpdateTitle();
    }

    // ===== Help (opens this guide read-only in a new instance) =====

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkdownMidget", "HELP.md");

            // Always restore the canonical help text from the embedded copy, then mark
            // the file read-only so it's harder to overwrite by accident.
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream("HELP.md"))
            using (var file = File.Create(path))
                stream!.CopyTo(file);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            OpenInNewInstance(path, helpWindow: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open Help:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "Markdown Midget\nA WYSIWYG markdown editor.\n\nMarkdown is the native format.",
            "About Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===== Dirty tracking (content vs. last opened/saved markdown) =====

    private void ScheduleDirtyCheck()
    {
        _dirtyTimer.Stop();
        _dirtyTimer.Start();
    }

    private void Source_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_sourceMode) _ = UpdateDirtyAsync();
    }

    private async Task UpdateDirtyAsync()
    {
        if (_suppressDirty) return;
        var current = await GetDocumentMarkdownAsync();
        var dirty = !string.Equals(current, _cleanMarkdown, StringComparison.Ordinal);
        if (dirty != _dirty)
        {
            _dirty = dirty;
            UpdateTitle();
        }
    }

    /// <summary>Marks the current content as the clean baseline (after open/save/new).</summary>
    private async Task SetCleanBaselineAsync()
    {
        _cleanMarkdown = await GetDocumentMarkdownAsync();
        _dirty = false;
        UpdateTitle();
    }

    private bool _canUndo;
    private bool _canRedo;

    private void SetUndoRedoEnabled(bool canUndo, bool canRedo)
    {
        _canUndo = canUndo;
        _canRedo = canRedo;
        UndoBtn.IsEnabled = UndoMenu.IsEnabled = canUndo && !_readOnly;
        RedoBtn.IsEnabled = RedoMenu.IsEnabled = canRedo && !_readOnly;
    }

    private void UpdateTitle()
    {
        var name = _currentPath is null ? "Untitled" : Path.GetFileName(_currentPath);
        var readOnly = _readOnly ? "          [Read Only]" : "";
        Title = $"{(_dirty ? "*" : "")}{name}{readOnly} — Markdown Midget {AppVersion}";
        StatusFile.Text = name;
    }

    private void RegisterShortcuts()
    {
        void Bind(Key key, ModifierKeys mods, ExecutedRoutedEventHandler handler)
        {
            var cmd = new RoutedCommand();
            cmd.InputGestures.Add(new KeyGesture(key, mods));
            CommandBindings.Add(new CommandBinding(cmd, handler));
        }

        Bind(Key.N, ModifierKeys.Control, (_, _) => New_Click(this, new RoutedEventArgs()));
        Bind(Key.O, ModifierKeys.Control, (_, _) => Open_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control, (_, _) => Save_Click(this, new RoutedEventArgs()));
        Bind(Key.S, ModifierKeys.Control | ModifierKeys.Shift, (_, _) => SaveAs_Click(this, new RoutedEventArgs()));
        Bind(Key.E, ModifierKeys.Control, (_, _) => ToggleSource_Click(this, new RoutedEventArgs()));
        Bind(Key.K, ModifierKeys.Control, (_, _) => Link_Click(this, new RoutedEventArgs()));
        Bind(Key.H, ModifierKeys.Control | ModifierKeys.Shift, (_, _) => FocusStyle_Click(this, new RoutedEventArgs()));

        // Ctrl+0..Ctrl+5 apply paragraph styles (also work via the editor keymap in WYSIWYG).
        var styleKeys = new[] { (Key.D0, "paragraph"), (Key.D1, "h1"), (Key.D2, "h2"),
                                (Key.D3, "h3"), (Key.D4, "h4"), (Key.D5, "h5") };
        foreach (var (key, tag) in styleKeys)
        {
            var t = tag;
            Bind(key, ModifierKeys.Control, (_, _) => { EditorCommand(t); SyncStyleCombo(t); });
        }
    }
}
