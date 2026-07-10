using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
    // Read at runtime from AssemblyInformationalVersionAttribute so CI's tag-derived
    // -p:InformationalVersion=... flows through automatically — no manual const sync.
    private static readonly string AppVersion = "v" + (
        typeof(MainWindow).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0");
    private static readonly string ProductDesc = "Markdown Midget " + AppVersion;

    // Segoe Fluent Icons glyphs for the source/WYSIWYG toggle.
    private static readonly string GlyphSource = char.ConvertFromUtf32(0xE943); // braces {} = markdown source
    private static readonly string GlyphRich = char.ConvertFromUtf32(0xE8A5);   // document = formatted view

    private string? _currentPath;
    private string? _displayName; // title for dropped content that has no path
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

    private const int MaxRecent = 5;
    private readonly List<string> _recentFiles = new();
    private string _pageWidth = "landscape"; // portrait | landscape | full (persisted)
    private bool _startReadOnly;
    private bool _isHelpWindow;
    private bool _readOnly;
    private bool _closed;            // closed/no-document state — shows ClosedSplash
    private (int curW, int curH, int natW, int natH) _imgResize;
    private readonly DispatcherTimer _dirtyTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private FindDialog? _findDialog;
    private int _sourceFindCursor = -1; // index into _sourceFindMatches
    private System.Text.RegularExpressions.MatchCollection? _sourceFindMatches;
    private string _lastFindSource = "";
    private string _lastFindFlags = "";

    // External-change tracking for the currently-open file.
    private FileSystemWatcher? _watcher;
    private bool _suppressWatcher;       // set true around our own writes
    private bool _externalDialogOpen;    // re-entrancy guard
    private DateTime _lastWatcherFireUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        RegisterShortcuts();
        SourceToggle.Content = GlyphSource; // start in WYSIWYG; button offers source view
        _dirtyTimer.Tick += async (_, _) => { _dirtyTimer.Stop(); await UpdateDirtyAsync(); };

        LoadRecent();
        BuildRecentMenu();
        LoadSettings();

        // Apply the persisted spell-check state (the editor is told on ready).
        MenuSpellCheck.IsChecked = _spellCheck;
        MenuSkipCodeSpell.IsChecked = _skipCodeSpell;
        SourceBox.SpellCheck.IsEnabled = _spellCheck;

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--readonly" or "-r" or "/readonly") _startReadOnly = true;
            else if (arg is "--help-window") { _isHelpWindow = true; _startReadOnly = true; }
            else if (arg == "--finish-move" && i + 1 < args.Length)
            {
                // We were launched by a "move" install to delete the original download
                // once its process has exited. Consume the path so it isn't opened.
                RegistrationService.FinishMove(args[++i]);
            }
            else if (File.Exists(arg)) _pendingOpenPath ??= arg;
        }

        if (_isHelpWindow) MenuViewHelp.IsEnabled = false; // no help-of-help

        Loaded += async (_, _) => await InitializeEditorAsync();
        Closing += MainWindow_Closing;
        UpdateTitle();
    }

    // ===== WebView2 / editor bootstrap =====

    private static string WebViewBaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "WebView2");

    // Unique per launch. A crashed/force-killed instance can orphan WebView2 child
    // processes that keep its profile folder locked; a bare process id could later be
    // recycled onto that still-locked folder and hit ERR_ACCESS_DENIED again. A GUID
    // never collides, so this run's folder is always brand-new and unlocked.
    private static readonly string ProfileFolderName = Guid.NewGuid().ToString("N");

    private async Task InitializeEditorAsync()
    {
        var wwwroot = ExtractEmbeddedEditor();

        // Give each instance its OWN WebView2 profile folder. A shared folder gets
        // locked/corrupted by a crashed or force-killed instance whose WebView2 child
        // processes orphan and hold the lock, breaking the next launch with
        // ERR_ACCESS_DENIED. A fresh per-launch folder can't conflict; a bad one only
        // affects that run, and the next launch is always clean.
        var userData = Path.Combine(WebViewBaseDir, ProfileFolderName);
        CleanupOldWebViewProfiles(); // remove folders from prior runs (in-use ones skipped)

        try
        {
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // Creating the profile folder or the WebView2 environment failed
            // (denied/locked profile, missing runtime, disk full…). The nav backstop
            // can't fire because we never navigate — offer the same restart, which
            // comes up on a fresh folder.
            OfferEditorRestart(ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnEditorNavigationCompleted;

        // Serve the open document's images ourselves (see ApplyDocBaseAsync).
        core.AddWebResourceRequestedFilter($"https://{DocHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnDocResourceRequested;

        // Lock down the host shell: it is a local app, not a browser.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // The WebView covers the window centre, so let it accept drops; the editor
        // intercepts file drops and posts them to the host (see the 'fileDrop'
        // message). Drops on the toolbar/menu are still handled by Window_Drop.
        Web.AllowExternalDrop = true;

        Web.ZoomFactorChanged += OnZoomChanged;
        UpdateZoomIndicator();

        // Per-launch nonce defeats WebView2's disk cache so a rebuilt editor bundle
        // is always loaded fresh (the bundle refs inside index.html are also hashed).
        _editorNavPending = true;
        core.Navigate($"https://{VirtualHost}/index.html?n={Guid.NewGuid():N}");
    }

    private bool _editorNavPending;

    // Backstop: if the editor shell still fails to load, offer a restart. With the
    // per-process profile folder a restart alone yields a clean profile, so no
    // marker/manual delete is needed.
    private void OnEditorNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_editorNavPending) return;   // only the initial editor-shell navigation
        _editorNavPending = false;
        if (e.IsSuccess) return;
        OfferEditorRestart($"error: {e.WebErrorStatus}");
    }

    // Offer a one-click restart when the editor surface can't come up (nav failure or
    // a failed environment creation). A restarted process gets a fresh per-launch
    // profile folder, so nothing else needs cleaning.
    private void OfferEditorRestart(string reason)
    {
        var reset = MessageBox.Show(this,
            $"The editor surface couldn't load ({reason}).\n\n" +
            "Restart Markdown Midget with a fresh editor profile?\n\n" +
            "Your documents and settings are not affected.",
            "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (reset != MessageBoxResult.Yes) return;

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { /* if relaunch fails the user can start it manually */ }
        Application.Current.Shutdown();
    }

    // Remove WebView2 profile folders left by previous runs. Folders still in use
    // (another running instance, or an orphaned WebView2 child) are locked and
    // skipped — they'll be cleaned up by a later launch once released.
    private static void CleanupOldWebViewProfiles()
    {
        try
        {
            if (!Directory.Exists(WebViewBaseDir)) return;
            foreach (var dir in Directory.GetDirectories(WebViewBaseDir))
            {
                if (string.Equals(Path.GetFileName(dir), ProfileFolderName, StringComparison.Ordinal)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* in use — skip */ }
            }
        }
        catch { /* best-effort */ }
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
                _ = RunEditorAsync($"window.MDM.setPageWidth({JsLiteral(_pageWidth)})");
                if (!_spellCheck) _ = RunEditorAsync("window.MDM.setSpellcheck(false)");
                _ = RunEditorAsync($"window.MDM.setCodeSpellcheck({(_skipCodeSpell ? "true" : "false")})");
                UpdatePageWidthChecks();
                if (_pendingOpenPath is { } p)
                {
                    _pendingOpenPath = null;
                    _ = OpenPathAsync(p);
                }
                else
                {
                    // Default landing state is the "no document open" splash, so a
                    // brand-new session is purely a drop target / Open / New prompt.
                    _ = SetCleanBaselineAsync();
                    SetClosed(true);
                }
                if (_startReadOnly) SetReadOnly(true);
                break;
            case "change":
                if (!_sourceMode)
                    ScheduleDirtyCheck();
                // Edits invalidate the WYSIWYG find index — force a re-scan on next find.
                _lastFindSource = "";
                _lastFindFlags = "";
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
            case "contextmenu":
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    var menu = d.RootElement.TryGetProperty("menu", out var mv) ? mv.GetString() ?? "text" : "text";
                    var x = d.RootElement.TryGetProperty("x", out var vx) ? vx.GetDouble() : 0;
                    var y = d.RootElement.TryGetProperty("y", out var vy) ? vy.GetDouble() : 0;
                    if (menu == "image")
                    {
                        int Get(string k) => d.RootElement.TryGetProperty(k, out var v) ? v.GetInt32() : 0;
                        _imgResize = (Get("curW"), Get("curH"), Get("natW"), Get("natH"));
                    }
                    // Defer so showing the menu doesn't block the WebView2 message pump.
                    Dispatcher.BeginInvoke(() => ShowEditorContextMenu(menu, x, y));
                }
                break;
            case "fileDrop":
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    var name = d.RootElement.TryGetProperty("name", out var nv) ? nv.GetString() ?? "Dropped.md" : "Dropped.md";
                    var content = d.RootElement.TryGetProperty("content", out var cv) ? cv.GetString() ?? "" : "";
                    Dispatcher.BeginInvoke(() => HandleDroppedContent(name, content));
                }
                break;
        }
    }

    private void ShowImageResizeDialog(int curW, int curH, int natW, int natH)
    {
        var dlg = new ImageSizeDialog(curW, curH, natW, natH) { Owner = this };
        if (dlg.ShowDialog() == true)
            _ = RunEditorAsync($"window.MDM.setImageSize({dlg.NewWidth}, {dlg.NewHeight})");
        RefocusEditor();
    }

    // ===== Native editor context menus =====

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private void ShowEditorContextMenu(string menu, double x, double y)
    {
        // Structure menus aren't available in read-only — fall back to the text menu.
        var key = (!_readOnly && menu == "table") ? "TableContextMenu"
                : (!_readOnly && menu == "image") ? "ImageContextMenu"
                : "TextContextMenu";
        if (FindResource(key) is not ContextMenu cm) return;

        // The WebView2 child HWND holds OS keyboard focus; pull it up to this window
        // first so the menu popup becomes keyboard-navigable.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetFocus(hwnd);

        cm.PlacementTarget = Web;
        cm.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
        cm.HorizontalOffset = x;
        cm.VerticalOffset = y;
        cm.IsOpen = true;
    }

    /// <summary>
    /// The WebView2 (an HwndHost) keeps Win32 keyboard focus, so a menu opened over it
    /// isn't keyboard-navigable until we pull focus into it and highlight an item.
    /// </summary>
    private void ContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        cm.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            cm.Focus();
            if (cm.ItemContainerGenerator.ContainerFromIndex(0) is MenuItem first)
            {
                first.Focus();
                Keyboard.Focus(first);
            }
            else
            {
                cm.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        }));
    }

    private void TableCmd_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _sourceMode) return;
        if (sender is MenuItem { Tag: string name })
        {
            _ = RunEditorAsync($"window.MDM.tableCmd({JsLiteral(name)})");
            RefocusEditor();
        }
    }

    private void ImageResize_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _sourceMode) return;
        ShowImageResizeDialog(_imgResize.curW, _imgResize.curH, _imgResize.natW, _imgResize.natH);
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
        if (_closed) return; // no document to flip between views

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
        ShowBusy($"Opening {Path.GetFileName(path)}…");
        try
        {
            var text = await File.ReadAllTextAsync(path);
            await LoadDocumentAsync(text, path);
            AddRecent(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the file:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { HideBusy(); }
    }

    /// <summary>Loads markdown into the editor and resets the clean baseline + history.</summary>
    private async Task LoadDocumentAsync(string markdown, string? path)
    {
        _suppressDirty = true;
        await ApplyDocBaseAsync(path);            // resolve relative images before render
        await SetDocumentMarkdownAsync(markdown); // setMarkdown flushes undo history
        _currentPath = path;
        _displayName = null;
        _suppressDirty = false;
        await SetCleanBaselineAsync();
        SetClosed(false);
        StartWatching(path);
    }

    // Resolve relative image paths (e.g. docs/logo.png) against the open document's
    // folder — the way Markdown Monster and GitHub do — by pointing a <base href> at
    // a dedicated host we serve ourselves via WebResourceRequested (a second virtual-
    // host mapping won't serve cross-origin to the editor host). The markdown model
    // keeps the original relative paths; only browser URL resolution changes, so
    // saving is unaffected.
    private const string DocHost = "mdm-doc.invalid";
    private string? _docFolder;

    private async Task ApplyDocBaseAsync(string? path)
    {
        if (Web.CoreWebView2 is null) return;

        string? dir = null;
        if (!string.IsNullOrEmpty(path))
        {
            try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); } catch { dir = null; }
        }
        _docFolder = (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ? Path.GetFullPath(dir) : null;

        if (_editorReady)
            await RunEditorAsync(_docFolder is not null
                ? $"window.MDM.setDocBase({JsLiteral($"https://{DocHost}/")})"
                : "window.MDM.setDocBase(null)");
    }

    // Serve files from the current document's folder for requests to the doc host.
    // Restricted to the document's own folder subtree.
    private void OnDocResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var root = _docFolder;
        if (root is null) return;
        try
        {
            var rel = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                return; // outside the document folder — don't serve
            if (!File.Exists(full)) return;

            var ms = new MemoryStream(File.ReadAllBytes(full));
            var headers = $"Content-Type: {ContentTypeFor(Path.GetExtension(full))}\r\n" +
                          "Access-Control-Allow-Origin: *\r\nCache-Control: no-cache";
            e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", headers);
        }
        catch { /* fall through to a normal failure */ }
    }

    private static string ContentTypeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        _ => "application/octet-stream",
    };

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
        _suppressWatcher = true;
        try
        {
            await File.WriteAllTextAsync(path, markdown);
        }
        catch (Exception ex)
        {
            _suppressWatcher = false;
            MessageBox.Show($"Couldn't save the file:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        // Let any FS event from our own write settle, then re-enable watching.
        _ = Dispatcher.BeginInvoke(new Action(() => _suppressWatcher = false), DispatcherPriority.Background);
        var pathChanged = !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase);
        _currentPath = path;
        _cleanMarkdown = markdown; // new clean baseline; undo history is left intact
        _dirty = false;
        UpdateTitle();
        if (pathChanged) StartWatching(path);
        SetClosed(false);
        AddRecent(path);
        return true;
    }

    // ===== Close (no-document state) =====

    private async void Close_Click(object sender, RoutedEventArgs e) => await CloseCurrentAsync();

    private void SplashOpen_Click(object sender, RoutedEventArgs e) => Open_Click(this, e);
    private void SplashNew_Click(object sender, RoutedEventArgs e) => New_Click(this, e);

    private async Task CloseCurrentAsync()
    {
        if (_closed) return;
        if (!await ConfirmDiscardAsync()) return;
        StopWatching();
        _suppressDirty = true;
        await SetDocumentMarkdownAsync(string.Empty);
        _currentPath = null;
        _displayName = null;
        _cleanMarkdown = string.Empty;
        _dirty = false;
        _suppressDirty = false;
        UpdateTitle();
        SetClosed(true);
    }

    private void SetClosed(bool on)
    {
        _closed = on;
        ClosedSplash.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = on || _sourceMode ? Visibility.Collapsed : Visibility.Visible;
        SourceBox.Visibility = (!on && _sourceMode) ? Visibility.Visible : Visibility.Collapsed;
        // When closed, all document-modifying controls are pointless — gray them out.
        FormatToolBar.IsEnabled = !on && !_readOnly;
        FormatMenu.IsEnabled = !on && !_readOnly;
        StyleMenu.IsEnabled = !on && !_readOnly;
        InsertMenu.IsEnabled = !on && !_readOnly;
        SaveBtn.IsEnabled = !on && !_readOnly;
        SaveMenu.IsEnabled = !on && !_readOnly;
        if (on) { UndoBtn.IsEnabled = UndoMenu.IsEnabled = false; RedoBtn.IsEnabled = RedoMenu.IsEnabled = false; }
        StatusMode.Text = on ? "No document" : (_sourceMode ? "Markdown source" : "WYSIWYG");
    }

    // ===== External change detection (FileSystemWatcher + backup + prompt) =====

    private void StartWatching(string? path)
    {
        StopWatching();
        if (path is null) return;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnWatcherEvent;
            _watcher.Created += OnWatcherEvent;
            _watcher.Renamed += OnWatcherEvent;
        }
        catch
        {
            // A path on a transient share or special filesystem can't be watched;
            // accept that external-change detection is best-effort here.
            StopWatching();
        }
    }

    private void StopWatching()
    {
        if (_watcher is null) return;
        try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { /* ignore */ }
        _watcher = null;
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (_suppressWatcher || _externalDialogOpen) return;
        var now = DateTime.UtcNow;
        if ((now - _lastWatcherFireUtc).TotalMilliseconds < 400) return; // debounce duplicate events
        _lastWatcherFireUtc = now;
        Dispatcher.BeginInvoke(new Action(async () => await OnExternalChangeAsync(e.FullPath)));
    }

    private async Task OnExternalChangeAsync(string fullPath)
    {
        if (_externalDialogOpen || _currentPath is null) return;
        if (!string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(_currentPath), StringComparison.OrdinalIgnoreCase))
            return;

        // Wait briefly for the writer to finish flushing.
        await Task.Delay(120);

        string newContent;
        try { newContent = await File.ReadAllTextAsync(_currentPath); }
        catch { return; /* file locked; another event will re-fire */ }

        if (string.Equals(newContent, _cleanMarkdown, StringComparison.Ordinal)) return;

        // Save the current (possibly unsaved) in-memory version as a timestamped backup.
        var inMemory = await GetDocumentMarkdownAsync();
        string backupPath;
        try { backupPath = WriteTimestampedBackup(_currentPath, inMemory); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't write a backup of your current version:\n{ex.Message}\n\nThe disk version was NOT reloaded.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _externalDialogOpen = true;
        try
        {
            var dlg = new ExternalChangeDialog(Path.GetFileName(_currentPath), backupPath) { Owner = this };
            dlg.ShowDialog();
            switch (dlg.Choice)
            {
                case ExternalChangeChoice.Reload:
                    await LoadDocumentAsync(newContent, _currentPath);
                    break;
                case ExternalChangeChoice.SaveAs:
                    await HandleSaveAsAfterExternalChangeAsync(inMemory, newContent, backupPath);
                    break;
                case ExternalChangeChoice.Keep:
                default:
                    // Accept the disk content as the new baseline so dirty reflects "my
                    // edits differ from disk"; the next Save will overwrite the disk.
                    _cleanMarkdown = newContent;
                    _ = UpdateDirtyAsync();
                    break;
            }
        }
        finally { _externalDialogOpen = false; }
    }

    private static string WriteTimestampedBackup(string originalPath, string content)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, $"{name}.{stamp}{ext}.bak");
        // Highly unlikely collision (same second) — append milliseconds.
        if (File.Exists(path))
            path = Path.Combine(dir, $"{name}.{stamp}-{DateTime.Now.Millisecond:D3}{ext}.bak");
        File.WriteAllText(path, content);
        return path;
    }

    private async Task HandleSaveAsAfterExternalChangeAsync(string inMemory, string newDiskContent, string backupPath)
    {
        if (_currentPath is null) return;
        var dir = Path.GetDirectoryName(_currentPath) ?? "";
        var nameNoExt = Path.GetFileNameWithoutExtension(_currentPath);
        var ext = Path.GetExtension(_currentPath);
        var suggested = Path.GetFileName(backupPath).Replace(".bak", "");
        var dlg = new SaveFileDialog
        {
            Title = "Save your current version as…",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ext.Length > 0 ? ext : ".md",
            InitialDirectory = dir,
            FileName = suggested,
        };
        if (dlg.ShowDialog(this) != true)
        {
            // User backed out of save-as — treat like Keep Current.
            _cleanMarkdown = newDiskContent;
            _ = UpdateDirtyAsync();
            return;
        }

        try { await File.WriteAllTextAsync(dlg.FileName, inMemory); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AddRecent(dlg.FileName);

        // Now ask which to keep viewing.
        var fileName = Path.GetFileName(_currentPath);
        var savedFileName = Path.GetFileName(dlg.FileName);
        var pick = MessageBox.Show(
            $"Saved your version to:\n{dlg.FileName}\n\nKeep editing your saved version ({savedFileName})?\n\nYes = open '{savedFileName}'\nNo = continue with the externally-modified '{fileName}'",
            "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (pick == MessageBoxResult.Yes)
        {
            // Already on disk with inMemory content; load + retarget.
            await LoadDocumentAsync(inMemory, dlg.FileName);
        }
        else
        {
            await LoadDocumentAsync(newDiskContent, _currentPath);
        }
    }

    // ===== Windows integration (register / unregister as .md editor) =====

    private async void RegisterMdEditor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new RegisterDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var alreadyInstalled = RegistrationService.IsRunningFromAppDataInstall();
        var willMove = dlg.MoveInsteadOfCopy && !alreadyInstalled;

        // A move restarts the app from the installed copy, so make sure unsaved
        // work is dealt with first — otherwise the restart would drop it.
        if (willMove && !await ConfirmDiscardAsync()) return;

        try
        {
            // Register always installs a stable copy to the app folder (unless we're
            // already running it). "Move" additionally removes the original download.
            var download = RegistrationService.CurrentExePath;
            var exeToRegister = alreadyInstalled
                ? RegistrationService.CurrentExePath
                : RegistrationService.InstallToAppData();

            if (!alreadyInstalled)
                RegistrationService.SaveInstallInfo(download, moved: willMove);

            RegistrationService.Register(exeToRegister);
            if (dlg.AddStartMenu) RegistrationService.CreateStartMenuShortcut(exeToRegister);
            else RegistrationService.RemoveStartMenuShortcut();
            if (dlg.AddDesktop) RegistrationService.CreateDesktopShortcut(exeToRegister);
            else RegistrationService.RemoveDesktopShortcut();

            // A "move" means deleting the download — but it's the running exe, so we
            // hand off to the freshly-installed copy which deletes it after we exit.
            if (willMove)
            {
                var psi = new ProcessStartInfo(exeToRegister) { UseShellExecute = true };
                psi.ArgumentList.Add("--finish-move");
                psi.ArgumentList.Add(download);
                if (_currentPath is not null) psi.ArgumentList.Add(_currentPath);
                Process.Start(psi);
                _dirty = false; // handled above; don't let Closing re-prompt
                Application.Current.Shutdown();
                return;
            }

            var lines = new List<string> { "Registered Markdown Midget as an editor for .md files.", "", "Exe: " + exeToRegister };
            if (dlg.AddStartMenu) lines.Add("Start menu: added");
            if (dlg.AddDesktop) lines.Add("Desktop shortcut: added");
            lines.Add("");
            lines.Add("If Explorer's \"Open with\" menu still shows an old entry, sign out and back in — it caches aggressively.");
            if (dlg.SetAsDefault)
                lines.Add("\nSettings will open on the .md page — click \"Markdown Midget\" there to finish making it the default (Windows requires this last click).");

            MessageBox.Show(this, string.Join("\n", lines), "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
            if (dlg.SetAsDefault) RegistrationService.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't complete registration:\n\n" + ex.Message,
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UnregisterMdEditor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new UnregisterDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var done = new List<string>();
            if (dlg.RemoveRegistration) { RegistrationService.Unregister(); done.Add("• Removed from the Open with list"); }
            if (dlg.RestoreToOriginal && dlg.OriginalPath is { } orig)
            {
                var ok = RegistrationService.RestoreToOriginal(orig);
                done.Add(ok ? "• Restored a copy to " + orig : "• Couldn't restore to " + orig);
            }
            if (dlg.RemoveStartMenu) { RegistrationService.RemoveStartMenuShortcut(); done.Add("• Removed the Start-menu entry"); }
            if (dlg.RemoveDesktop) { RegistrationService.RemoveDesktopShortcut(); done.Add("• Removed the Desktop shortcut"); }
            if (dlg.RemoveAppDataCopy) { RegistrationService.UninstallFromAppData(); done.Add("• Removed the installed copy from the app folder"); }

            var summary = done.Count > 0 ? string.Join("\n", done) : "Nothing was selected.";
            MessageBox.Show(this, summary, "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't complete unregistration:\n\n" + ex.Message,
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // Win+arrow window management is handled natively by the Windows shell — the
    // window is a standard resizable window, so Snap works like Notepad/Explorer.
    // We deliberately do NOT intercept those keys; a custom handler only degrades
    // the OS behavior (no snap-assist, worse multi-monitor/DPI handling).

    // ===== Busy overlay (file open / large-doc load) =====

    private void ShowBusy(string text)
    {
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
    }

    private void HideBusy() => BusyOverlay.Visibility = Visibility.Collapsed;

    // ===== Find (modeless dialog, F3 / Shift+F3 navigation) =====

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_findDialog is null)
        {
            _findDialog = new FindDialog { Owner = this };
            _findDialog.FindRequested += OnFindRequested;
            _findDialog.Closed2 += (_, _) =>
            {
                _findDialog = null;
                _ = ClearWysiwygFindAsync();
                _sourceFindMatches = null;
                _sourceFindCursor = -1;
            };
            _findDialog.Show();
        }
        _findDialog.FocusQuery();
    }

    private void FindNextRequested(bool forward)
    {
        if (_findDialog is null) Find_Click(this, new RoutedEventArgs());
        else
            OnFindRequested(this, new FindRequest(
                _findDialog!.Query, _findDialog.CurrentMode,
                _findDialog.MatchCaseOn, _findDialog.WholeWordOn, _findDialog.WrapOn, forward));
    }

    private async void OnFindRequested(object? sender, FindRequest req)
    {
        var regex = FindEngine.Build(req.Query, req.Mode, req.MatchCase, req.WholeWord);
        if (regex is null)
        {
            _findDialog?.SetStatus(string.IsNullOrEmpty(req.Query)
                ? "Type to search."
                : "Invalid pattern.");
            return;
        }

        if (_sourceMode)
            DoSourceFind(regex, req);
        else
            await DoWysiwygFindAsync(regex, req);
    }

    private void DoSourceFind(System.Text.RegularExpressions.Regex regex, FindRequest req)
    {
        var text = SourceBox.Text;
        _sourceFindMatches = regex.Matches(text);
        var total = _sourceFindMatches.Count;
        if (total == 0)
        {
            _sourceFindCursor = -1;
            _findDialog?.SetStatus(req.LiveTyping ? "No matches." : "No matches found.");
            return;
        }

        if (req.LiveTyping)
        {
            // Land on the first match at or after the current caret position.
            var caret = SourceBox.SelectionStart;
            _sourceFindCursor = 0;
            for (var i = 0; i < total; i++)
                if (_sourceFindMatches[i].Index >= caret) { _sourceFindCursor = i; break; }
        }
        else
        {
            _sourceFindCursor = req.Forward
                ? (_sourceFindCursor + 1)
                : (_sourceFindCursor - 1);
            if (_sourceFindCursor >= total) _sourceFindCursor = req.Wrap ? 0 : total - 1;
            if (_sourceFindCursor < 0) _sourceFindCursor = req.Wrap ? total - 1 : 0;
        }

        var m = _sourceFindMatches[_sourceFindCursor];
        SourceBox.Select(m.Index, m.Length);
        SourceBox.ScrollToLine(SourceBox.GetLineIndexFromCharacterIndex(m.Index));
        _findDialog?.SetStatus($"Match {_sourceFindCursor + 1} of {total}");
    }

    private async Task DoWysiwygFindAsync(System.Text.RegularExpressions.Regex regex, FindRequest req)
    {
        if (!_editorReady) return;
        // Pass the regex source + flags to JS (which builds a JS RegExp from it).
        var flags = "g";
        if ((regex.Options & System.Text.RegularExpressions.RegexOptions.IgnoreCase) != 0) flags += "i";
        if ((regex.Options & System.Text.RegularExpressions.RegexOptions.Multiline) != 0) flags += "m";
        var src = regex.ToString();

        // Re-scan only when the pattern OR options change, so explicit Find Next /
        // Find Previous advance the match cursor rather than rebuilding from match 1.
        if (src != _lastFindSource || flags != _lastFindFlags)
        {
            await RunEditorAsync($"window.MDM.findReset({JsLiteral(src)}, {JsLiteral(flags)})");
            _lastFindSource = src;
            _lastFindFlags = flags;
        }

        var dir = req.Forward ? "Next" : "Prev";
        var result = await RunEditorAsync($"JSON.stringify(window.MDM.find{dir}({(req.Wrap ? "true" : "false")}))");
        ReportFindResult(result, req);
    }

    private void ReportFindResult(string? json, FindRequest req)
    {
        if (string.IsNullOrEmpty(json)) { _findDialog?.SetStatus("No matches."); return; }
        try
        {
            using var d = JsonDocument.Parse(json);
            var total = d.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            var current = d.RootElement.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
            if (total == 0)
                _findDialog?.SetStatus(req.LiveTyping ? "No matches." : "No matches found.");
            else
                _findDialog?.SetStatus($"Match {current} of {total}");
        }
        catch { _findDialog?.SetStatus("No matches."); }
    }

    private async Task ClearWysiwygFindAsync()
    {
        if (_editorReady)
            await RunEditorAsync("window.MDM.findClear()");
    }

    // ===== Print + PDF export (per-page-width prefs, persisted) =====

    private void PrintSubmenu_Opened(object sender, RoutedEventArgs e)
    {
        var p = GetPrintPrefs();
        PrintHeaderFooterMenu.IsChecked = p.ShowHeaderFooter;
        PrintColorCodeMenu.IsChecked = p.ColorCodeBlocks;
    }

    private void PrintHeaderFooter_Click(object sender, RoutedEventArgs e)
    {
        var p = GetPrintPrefs();
        p.ShowHeaderFooter = PrintHeaderFooterMenu.IsChecked;
        SaveSettings();
    }

    private void PrintColorCode_Click(object sender, RoutedEventArgs e)
    {
        var p = GetPrintPrefs();
        p.ColorCodeBlocks = PrintColorCodeMenu.IsChecked;
        SaveSettings();
    }

    /// <summary>
    /// Stashes the current print prefs in the editor. The editor applies them on
    /// the standard browser `beforeprint` event and clears them on `afterprint`,
    /// so the screen view is never disturbed and timing is correct for both
    /// ShowPrintUI and PrintToPdfAsync.
    /// </summary>
    private async Task PreparePrintModeAsync()
    {
        if (!_editorReady) return;
        var p = GetPrintPrefs();
        var sourceText = _sourceMode ? SourceBox.Text : string.Empty;
        var opts = $"{{sourceMode:{(_sourceMode ? "true" : "false")},colorCode:{(p.ColorCodeBlocks ? "true" : "false")},sourceText:{JsLiteral(sourceText)}}}";
        await RunEditorAsync($"window.MDM.setPrintMode({opts})");
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || Web.CoreWebView2 is null) return;
        try
        {
            await PreparePrintModeAsync();
            // The browser preview is modal-by-WebView; we cannot read what the user
            // toggles in it (printer, copies, header/footer), but our app-level
            // prefs (mono/colour code blocks, source vs WYSIWYG view) are applied
            // during print rendering via beforeprint/afterprint.
            Web.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Print failed:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || Web.CoreWebView2 is null) return;

        var defaultName = _currentPath is not null
            ? Path.GetFileNameWithoutExtension(_currentPath) + ".pdf"
            : (Path.GetFileNameWithoutExtension(_displayName) ?? "Untitled") + ".pdf";

        var dlg = new SaveFileDialog
        {
            Title = "Export to PDF",
            Filter = "PDF (*.pdf)|*.pdf|All files (*.*)|*.*",
            DefaultExt = ".pdf",
            FileName = defaultName,
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            await PreparePrintModeAsync();
            var prefs = GetPrintPrefs();
            var settings = Web.CoreWebView2.Environment.CreatePrintSettings();
            settings.ShouldPrintHeaderAndFooter = prefs.ShowHeaderFooter;
            settings.HeaderTitle = _currentPath is not null
                ? Path.GetFileName(_currentPath)
                : (_displayName ?? "Untitled");
            settings.FooterUri = string.Empty; // suppress markdownmidget.invalid URL
            settings.Orientation = _pageWidth == "landscape"
                ? CoreWebView2PrintOrientation.Landscape
                : CoreWebView2PrintOrientation.Portrait;

            var ok = await Web.CoreWebView2.PrintToPdfAsync(dlg.FileName, settings);
            if (!ok)
                MessageBox.Show("PDF export did not complete.", "Markdown Midget",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PDF export failed:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== Recent files (MRU, persisted) =====

    private static string RecentStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "recent.json");

    private void LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentStorePath)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentStorePath));
            if (list is not null) _recentFiles.AddRange(list.Take(MaxRecent));
        }
        catch { /* ignore a corrupt/absent MRU */ }
    }

    private void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecentStorePath)!);
            File.WriteAllText(RecentStorePath, JsonSerializer.Serialize(_recentFiles));
        }
        catch { /* MRU is best-effort */ }
    }

    private void AddRecent(string path)
    {
        var full = Path.GetFullPath(path);
        _recentFiles.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _recentFiles.Insert(0, full);
        while (_recentFiles.Count > MaxRecent) _recentFiles.RemoveAt(_recentFiles.Count - 1);
        SaveRecent();
        BuildRecentMenu();
    }

    private void FileMenu_Opened(object sender, RoutedEventArgs e) => BuildRecentMenu();

    // Built eagerly (not just on submenu-open) so an empty submenu still shows and
    // the list updates the moment a file is opened or saved.
    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        if (_recentFiles.Count == 0)
        {
            RecentMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        var i = 1;
        foreach (var path in _recentFiles)
        {
            var item = new MenuItem { Header = $"_{i} {Path.GetFileName(path)}", Tag = path, ToolTip = path };
            item.Click += RecentItem_Click;
            RecentMenu.Items.Add(item);
            i++;
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "_Clear Recent" };
        clear.Click += ClearRecent_Click;
        RecentMenu.Items.Add(clear);
    }

    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path }) return;
        if (!File.Exists(path))
        {
            MessageBox.Show($"File not found:\n{path}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _recentFiles.Remove(path);
            SaveRecent();
            BuildRecentMenu();
            return;
        }
        if (!await ConfirmDiscardAsync()) return;
        await OpenPathAsync(path);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        _recentFiles.Clear();
        SaveRecent();
        BuildRecentMenu();
    }

    // ===== Settings (persisted) =====

    private sealed class AppSettings
    {
        public string PageWidth { get; set; } = "portrait";
        public Dictionary<string, PrintPrefs> PrintPrefs { get; set; } = new();
        public bool SpellCheck { get; set; } = true;
        public bool SkipCodeSpellCheck { get; set; } = true;
    }

    private bool _spellCheck = true;         // persisted; applied on editor-ready
    private bool _skipCodeSpell = true;      // persisted; exempt code from spell check

    private sealed class PrintPrefs
    {
        public bool ShowHeaderFooter { get; set; } = true;
        public bool ColorCodeBlocks { get; set; } = true;
    }

    private readonly Dictionary<string, PrintPrefs> _printPrefs = new();

    private PrintPrefs GetPrintPrefs()
    {
        if (!_printPrefs.TryGetValue(_pageWidth, out var p))
        {
            p = new PrintPrefs();
            _printPrefs[_pageWidth] = p;
        }
        return p;
    }

    private static string SettingsStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarkdownMidget", "settings.json");

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsStorePath)) return;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsStorePath));
            if (s is null) return;
            if (s.PageWidth is "portrait" or "landscape" or "full")
                _pageWidth = s.PageWidth;
            if (s.PrintPrefs is not null)
                foreach (var kv in s.PrintPrefs)
                    if (kv.Key is "portrait" or "landscape" or "full" && kv.Value is not null)
                        _printPrefs[kv.Key] = kv.Value;
            _spellCheck = s.SpellCheck;
            _skipCodeSpell = s.SkipCodeSpellCheck;
        }
        catch { /* defaults are fine */ }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsStorePath)!);
            File.WriteAllText(SettingsStorePath, JsonSerializer.Serialize(new AppSettings
            {
                PageWidth = _pageWidth,
                PrintPrefs = _printPrefs,
                SpellCheck = _spellCheck,
                SkipCodeSpellCheck = _skipCodeSpell,
            }));
        }
        catch { /* best-effort */ }
    }

    // ===== Document width =====

    private void PageWidth_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string mode }) SetPageWidth(mode);
    }

    /// <summary>Opens the document-width dropdown from the View toolbar button.</summary>
    private void PageWidthMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var cm = new ContextMenu
        {
            PlacementTarget = b,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (label, mode) in new[] { ("_Portrait", "portrait"), ("_Landscape", "landscape"), ("_Full Width", "full") })
        {
            var item = new MenuItem { Header = label, Tag = mode, IsCheckable = true, IsChecked = _pageWidth == mode };
            item.Click += PageWidth_Click;
            cm.Items.Add(item);
        }
        cm.IsOpen = true;
    }

    private void SetPageWidth(string mode)
    {
        _pageWidth = mode;
        SaveSettings();
        UpdatePageWidthChecks();
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.setPageWidth({JsLiteral(mode)})");
        RefocusEditor();
    }

    private void UpdatePageWidthChecks()
    {
        PageWidthPortrait.IsChecked = _pageWidth == "portrait";
        PageWidthLandscape.IsChecked = _pageWidth == "landscape";
        PageWidthFull.IsChecked = _pageWidth == "full";
    }

    // ===== Zoom indicator =====

    private void OnZoomChanged(object? sender, EventArgs e) => UpdateZoomIndicator();

    private void UpdateZoomIndicator()
    {
        var pct = (int)System.Math.Round(Web.ZoomFactor * 100);
        StatusZoom.Text = $"{pct}%";
    }

    private void StatusZoom_Reset(object sender, MouseButtonEventArgs e) => Web.ZoomFactor = 1.0;

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty)
        {
            e.Cancel = true; // Defer; re-close after the async prompt resolves.
            if (await ConfirmDiscardAsync())
            {
                _dirty = false;
                StopWatching();
                Close();
            }
            return;
        }
        StopWatching();
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
        _spellCheck = on;
        SaveSettings();               // remember the choice across sessions
        SourceBox.SpellCheck.IsEnabled = on;
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.setSpellcheck({(on ? "true" : "false")})");
        RefocusEditor();
    }

    private void SkipCodeSpell_Click(object sender, RoutedEventArgs e)
    {
        _skipCodeSpell = MenuSkipCodeSpell.IsChecked;
        SaveSettings();
        if (_editorReady)
            _ = RunEditorAsync($"window.MDM.setCodeSpellcheck({(_skipCodeSpell ? "true" : "false")})");
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

    /// <summary>
    /// Opens a file dropped onto the editor area. Web content can't see the file
    /// path, so this loads the dropped text as an untitled document named after the
    /// file (Save will prompt for a location).
    /// </summary>
    private async void HandleDroppedContent(string name, string content)
    {
        if (!await ConfirmDiscardAsync()) return;
        ShowBusy($"Opening {name}…");
        try
        {
            StopWatching();
            _suppressDirty = true;
            await ApplyDocBaseAsync(null); // dropped content has no folder context
            await SetDocumentMarkdownAsync(content);
            _currentPath = null;
            _displayName = name;
            _suppressDirty = false;
            await SetCleanBaselineAsync();
            SetClosed(false);
        }
        finally { HideBusy(); }
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
        var name = _currentPath is not null ? Path.GetFileName(_currentPath)
                 : _displayName ?? "Untitled";
        var readOnly = _readOnly ? "  [Read Only]" : "";
        Title = $"{(_dirty ? "*" : "")}{name}{readOnly}  |  {ProductDesc}";
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
        Bind(Key.W, ModifierKeys.Control, (_, _) => Close_Click(this, new RoutedEventArgs()));
        Bind(Key.P, ModifierKeys.Control, (_, _) => Print_Click(this, new RoutedEventArgs()));
        Bind(Key.K, ModifierKeys.Control, (_, _) => Link_Click(this, new RoutedEventArgs()));
        Bind(Key.F, ModifierKeys.Control, (_, _) => Find_Click(this, new RoutedEventArgs()));
        Bind(Key.F3, ModifierKeys.None, (_, _) => FindNextRequested(forward: true));
        Bind(Key.F3, ModifierKeys.Shift, (_, _) => FindNextRequested(forward: false));
        Bind(Key.F1, ModifierKeys.None, (_, _) => Help_Click(this, new RoutedEventArgs()));
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
