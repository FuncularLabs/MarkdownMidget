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
    private static readonly string GlyphRich = char.ConvertFromUtf32(0xE8A1);   // rendered content card = formatted (WYSIWYG) view

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

    private readonly List<string> _recentFiles = new();
    private string _pageWidth = "landscape"; // portrait | landscape | full (persisted)
    private bool _startReadOnly;
    private bool _startInSource;  // --source: open showing the raw markdown
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
    private bool _externalChangeBusy;    // re-entrancy guard: dialog OR auto-reload in flight

    public MainWindow()
    {
        InitializeComponent();
        RegisterShortcuts();
        SourceToggle.Content = GlyphSource; // start in WYSIWYG; button offers source view
        _dirtyTimer.Tick += async (_, _) => { _dirtyTimer.Stop(); await UpdateDirtyAsync(); };

        // Settings first: LoadRecent and BuildRecentMenu both consult _recentLimit,
        // so loading them in the other order silently applies the default of 10.
        LoadSettings();
        LoadRecent();
        BuildRecentMenu();

        // Apply the persisted spell-check state. Checking is done by the app's own
        // engine (see MainWindow.Spell.cs); native spell check stays off everywhere.
        MenuSpellCheck.IsChecked = _spellCheck;
        MenuSkipCodeSpell.IsChecked = _skipCodeSpell;
        InitSpell();

        // Apply the persisted source-view word-wrap state (starts in WYSIWYG, so the
        // toolbar button starts disabled/off).
        MenuWordWrap.IsChecked = _wordWrap;
        ApplyWordWrap();
        UpdateWrapToggleUi();
        MenuAutoReload.IsChecked = _autoReload;
        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); StatusNote.Text = string.Empty; };

        // (Window placement is applied later, in OnSourceInitialized — it needs the HWND.)
        Updates.UpdateService.CleanupOldBinaries();
        _ = NotifyIfUpdateAvailableAsync();
        _externalChangeTimer.Tick += async (_, _) => await OnExternalChangeTimerAsync();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--readonly" or "-r" or "/readonly") _startReadOnly = true;
            else if (arg is "--source" or "/source") _startInSource = true;
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
                // Native (browser) spell check stays OFF — the app runs its own engine
                // with a private dictionary; squiggles come from host-computed ranges.
                _ = RunEditorAsync("window.MDM.setSpellcheck(false)");
                RequestSpellCheckSoon();
                UpdatePageWidthChecks();
                if (_pendingOpenPath is { } p)
                {
                    _pendingOpenPath = null;
                    _ = OpenThenApplyStartupViewAsync(p);
                }
                else if (_startWithBlankDocument)
                {
                    // Settings: land on an empty document with the caret in it, so a
                    // session can start by simply typing.
                    _ = StartBlankDocumentAsync();
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
                {
                    ScheduleDirtyCheck();
                    RequestSpellCheckSoon();
                }
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
                    // A right-click on a misspelled word carries its range + text, on
                    // whatever menu the click warranted — spelling rides along, it
                    // doesn't replace the structural menus.
                    SpellClick? spell = null;
                    if (!_readOnly && _spellCheck &&
                        d.RootElement.TryGetProperty("spell", out var sp) && sp.ValueKind == JsonValueKind.Object)
                    {
                        var w = sp.TryGetProperty("word", out var wv) ? wv.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(w))
                            spell = new SpellClick(
                                sp.GetProperty("from").GetInt32(),
                                sp.GetProperty("to").GetInt32(),
                                w!,
                                sp.TryGetProperty("before", out var bv) ? bv.GetString() ?? "" : "");
                    }
                    // Defer so showing the menu doesn't block the WebView2 message pump.
                    if (spell is { } si && menu == "text")
                        Dispatcher.BeginInvoke(async () => await ShowSpellContextMenuAsync(x, y, si));
                    else
                        Dispatcher.BeginInvoke(async () => await ShowEditorContextMenuAsync(menu, x, y, spell));
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

    /// <summary>
    /// Show a structural (table/image) or plain text menu, folding in the spelling
    /// actions when the click also landed on a misspelling — so a squiggle inside a
    /// table cell keeps both its spelling actions and the table commands.
    /// </summary>
    private async Task ShowEditorContextMenuAsync(string menu, double x, double y, SpellClick? spell)
    {
        var key = (!_readOnly && menu == "table") ? "TableContextMenu"
                : (!_readOnly && menu == "image") ? "ImageContextMenu"
                : "TextContextMenu";
        if (FindResource(key) is not ContextMenu cm) return;

        // Resource menus can't carry x:Name fields, so the placeholder is tagged.
        var spellRoot = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "spellRoot");
        var spellSep = cm.Items.OfType<Separator>().FirstOrDefault(m => (m.Tag as string) == "spellSep");
        if (spellRoot is not null)
        {
            if (spell is { } s)
            {
                // Build first, THEN swap in. This resource menu is a singleton, so a
                // second right-click arriving during the awaited engine calls would
                // otherwise interleave its items into the same collection.
                var built = await BuildSpellItemsAsync(s, WysiwygReplace(s), trailingSeparator: false);
                spellRoot.Items.Clear();
                foreach (var it in built) spellRoot.Items.Add(it);
                spellRoot.Header = $"Spellin_g: {s.Word.Replace("_", "__")}";
                spellRoot.Visibility = Visibility.Visible;
                if (spellSep is not null) spellSep.Visibility = Visibility.Visible;
            }
            else
            {
                spellRoot.Items.Clear();
                spellRoot.Visibility = Visibility.Collapsed;
                if (spellSep is not null) spellSep.Visibility = Visibility.Collapsed;
            }
        }
        ShowMenuOverEditor(cm, x, y);
    }

    /// <summary>Open a menu over the WebView2 surface with the focus dance the
    /// HwndHost needs (used by both the resource menus and the dynamic spell menu).</summary>
    private void ShowMenuOverEditor(ContextMenu cm, double x, double y)
    {
        // The WebView2 child HWND holds OS keyboard focus; pull it up to this window
        // first so the menu popup becomes keyboard-navigable.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetFocus(hwnd);

        cm.Opened -= ContextMenu_Opened;
        cm.Opened += ContextMenu_Opened;
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
            // Focus the first ACTIVATABLE item, not blindly item 0 — see
            // ContextMenuFocus for why a disabled first entry used to strand focus.
            if (ContextMenuFocus.FirstActivatableItem(cm) is { } item)
            {
                item.Focus();
                Keyboard.Focus(item);
                return;
            }
            cm.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
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
    /// Smallest saved rectangle worth believing, in physical pixels. Deliberately a
    /// flat number rather than MinWidth/MinHeight scaled by DPI: the window's DPI at
    /// this point is the monitor it was CREATED on, not the one it's about to move
    /// to, so on a 175% primary a legitimate 800x600 rect saved on a 100% secondary
    /// would be rejected as garbage - and then overwritten on close, destroying the
    /// user's size for good. This only has to reject nonsense; WPF's own MinWidth
    /// and MinHeight still enforce the real floor once the window is up.
    /// </summary>
    private static readonly Size MinSaved = new(240, 160);

    /// <summary>The rectangle we're trying to restore to, until it sticks.</summary>
    private Rect? _restoreTarget;

    /// <summary>
    /// Reapply the remembered size/position, having first checked it still lands on
    /// a screen that exists — a monitor can be gone since last run.
    ///
    /// Everything here is physical pixels: the saved rectangle, the monitor work
    /// areas, and the Win32 call that applies it. Mixing in WPF's device-independent
    /// Left/Top/Width/Height would put the window on the wrong monitor as soon as a
    /// display isn't at 100% scaling.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);   // the HWND exists from here on
        if (_isHelpWindow) return;     // a help viewer shouldn't land on top of the editor
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _restoreTarget = WindowPlacement.Sanitize(_savedBounds, MonitorInfo.WorkAreas(), MinSaved);
            if (_restoreTarget is { } b) NativeWindowPlacement.Apply(hwnd, b, _savedMaximized);
            else if (_savedMaximized) WindowState = WindowState.Maximized;
        }
        catch { /* the default placement is a fine outcome */ }
    }

    /// <summary>
    /// Re-assert the restored rectangle once the window is up.
    ///
    /// A window is created on the primary monitor, so restoring it onto a display
    /// with a different scale factor raises WM_DPICHANGED, and WPF answers that by
    /// resizing the window by the DPI ratio. The rectangle therefore arrives 1.5x
    /// too big on a 150% display — and because the inflated size is what gets saved
    /// on close, it inflates again on the next launch. Measured on a 150% monitor:
    /// 2700 -> 4050 -> 5760 px wide over three launches.
    ///
    /// By now the window is already on its target monitor in that monitor's DPI
    /// context, so applying the same rectangle a second time sticks and no further
    /// DPI change follows.
    ///
    /// This applies to a maximized window too. Its restore-down rectangle takes the
    /// same path and inflates the same way — invisibly, until the user restores down
    /// and finds the window half again too big, with the inflated size saved on top
    /// of theirs.
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var target = _restoreTarget;
        _restoreTarget = null;                       // one shot; the user owns it after this
        if (target is not { } b) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (NativeWindowPlacement.TryGet(hwnd, out var now, out _) && NearlyEqual(now, b)) return;
            NativeWindowPlacement.Apply(hwnd, b, _savedMaximized);
        }
        catch { /* where it landed is where it stays */ }
    }

    /// <summary>Within a pixel or two — rounding through the placement struct isn't
    /// worth a second resize.</summary>
    private static bool NearlyEqual(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 &&
        Math.Abs(a.Width - b.Width) <= 2 && Math.Abs(a.Height - b.Height) <= 2;

    /// <summary>Capture placement for next launch: the NORMAL rectangle even while
    /// maximized or minimized, which is what we want to come back to.</summary>
    private void CaptureWindowPlacement()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (NativeWindowPlacement.TryGet(hwnd, out var normal, out var maximized))
            {
                _savedBounds = normal;
                _savedMaximized = maximized;
            }
        }
        catch { /* keep whatever was loaded */ }
    }

    private async Task StartBlankDocumentAsync()
    {
        await LoadDocumentAsync(string.Empty, null);
        await FocusDocumentAsync();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_startWithBlankDocument, _recentLimit) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _startWithBlankDocument = dlg.StartWithBlankDocument;
        if (dlg.RecentLimit != _recentLimit)
        {
            // Only the menu length changes. Lowering the limit must not delete
            // history from disk — the user would have no way to get it back, and
            // raising the limit again should bring the older entries with it.
            _recentLimit = dlg.RecentLimit;
            BuildRecentMenu();
        }
        SaveSettings();
    }

    /// <summary>Cap what we keep on disk. This is the storage bound, deliberately
    /// larger than any display limit — see <see cref="Settings_Click"/>.</summary>
    private void TrimRecent()
    {
        while (_recentFiles.Count > SettingsDialog.MaxRecentLimit)
            _recentFiles.RemoveAt(_recentFiles.Count - 1);
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

    /// <summary>
    /// The document's markdown, or null when the editor can't be asked. Callers making
    /// destructive decisions must not treat "couldn't ask" as "the document is empty".
    /// </summary>
    private async Task<string?> TryGetDocumentMarkdownAsync()
    {
        if (_sourceMode) return SourceBox.Text;
        if (!_editorReady) return null;
        return await RunEditorAsync("window.MDM.getMarkdown()");
    }

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
        // Count here rather than in each caller: installing content doesn't raise a
        // 'change' message, so a freshly opened document would otherwise show no
        // count at all until the first keystroke.
        UpdateCounts(markdown);
    }

    private async Task OpenThenApplyStartupViewAsync(string path)
    {
        await OpenPathAsync(path);
        if (_startInSource && !_closed)
        {
            _startInSource = false;
            await SetSourceModeAsync(true);
        }
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
        // rendered-content glyph (-> formatted); in WYSIWYG show braces (-> source).
        SourceToggle.Content = on ? GlyphRich : GlyphSource;
        SourceToggle.ToolTip = on
            ? "Switch to formatted / WYSIWYG view (Ctrl+E)"
            : "Edit markdown source (Ctrl+E)";

        // Word wrap applies to the source view only.
        UpdateWrapToggleUi();

        if (on) _squiggles?.SetRanges(Array.Empty<(int, int)>()); // previous ranges are stale for this text
        if (on) SetUndoRedoEnabled(true, true); // the source TextBox manages its own undo
        RefocusEditor();
        _ = UpdateDirtyAsync();
        RequestSpellCheckSoon();
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
        await FocusDocumentAsync();   // a blank document should be ready to type into
    }

    /// <summary>
    /// Put the caret in the document itself. Without this, New leaves focus on
    /// whatever raised it — the menu, the toolbar button, the splash link — and the
    /// first keystroke goes nowhere, so the user has to click into an empty document
    /// before typing.
    /// </summary>
    private async Task FocusDocumentAsync()
    {
        if (_closed || _readOnly) return;      // nothing to type into

        // Let whatever raised this finish closing first: a menu hands focus back to
        // its owner as it tears down, which can undo a focus call made before that.
        await Dispatcher.Yield(DispatcherPriority.Background);
        // Background sits below Input, so a click on the window's close box can
        // preempt this continuation — don't come back and touch a torn-down WebView.
        if (_closed || !IsLoaded) return;

        if (_sourceMode)
        {
            SourceBox.Focus();
            SourceBox.CaretIndex = 0;
            return;
        }
        // WPF focus has to land on the WebView2 (an HwndHost) before the editor's
        // own DOM focus will stick, so do both.
        Web.Focus();
        if (_editorReady) await RunEditorAsync("window.MDM.focus()");
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
            await FocusDocumentAsync();   // same reason as New: don't eat the first keystroke
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
        // A new document invalidates any in-flight spell check — its results were
        // computed against the OLD document and must never decorate this one.
        _spellGeneration++;
        await ApplyDocBaseAsync(path);            // resolve relative images before render
        await SetDocumentMarkdownAsync(markdown); // setMarkdown flushes undo history
        _currentPath = path;
        _displayName = null;
        _suppressDirty = false;
        await SetCleanBaselineAsync();
        SetClosed(false);
        StartWatching(path);
        // setMarkdown doesn't surface as a 'change' message, so schedule explicitly —
        // without this, a freshly opened document shows no squiggles until edited.
        RequestSpellCheckSoon();
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
        if (_docFolder is null) return;
        try
        {
            var full = DocAsset.ResolveWithinRoot(_docFolder, e.Request.Uri);
            if (full is null || !File.Exists(full)) return;

            var ms = new MemoryStream(File.ReadAllBytes(full));
            var headers = $"Content-Type: {DocAsset.ContentTypeFor(Path.GetExtension(full))}\r\n" +
                          "Access-Control-Allow-Origin: *\r\nCache-Control: no-cache";
            e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(ms, 200, "OK", headers);
        }
        catch { /* fall through to a normal failure */ }
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
        ApplyCountText();   // no document, no count
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

    // External-change intake. Everything below `OnWatcherEvent` runs on the UI thread
    // only, so no flag can race between the FileSystemWatcher threadpool and the
    // dispatcher. Events are never dropped: each one records the LATEST changed path,
    // and a short quiet timer (which also coalesces save bursts) runs the pass. A pass
    // that ends with another event pending reschedules itself with the CURRENT pending
    // path — never the path it started with, which may be stale by then.
    private string? _pendingExternalChange;                     // UI-thread only
    private readonly DispatcherTimer _externalChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        if (_suppressWatcher) return;
        _ = Dispatcher.BeginInvoke(new Action(() => NoteExternalChange(e.FullPath)));
    }

    private void NoteExternalChange(string fullPath)
    {
        _pendingExternalChange = fullPath;
        if (_externalChangeBusy) return;   // the running pass reschedules us when it ends
        _externalChangeTimer.Stop();
        _externalChangeTimer.Start();      // wait for a quiet moment, coalescing bursts
    }

    private async Task OnExternalChangeTimerAsync()
    {
        _externalChangeTimer.Stop();
        var fullPath = _pendingExternalChange;
        _pendingExternalChange = null;
        if (fullPath is null || _externalChangeBusy || _currentPath is null) return;

        // Pin the document this pass is about. _currentPath can change under our awaits
        // (the user opens something else), and everything below must refuse to act on a
        // document it wasn't started for.
        var path = _currentPath;
        if (string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            _externalChangeBusy = true;
            try
            {
                await HandleExternalChangeAsync(path);
            }
            finally { _externalChangeBusy = false; }
        }

        // Anything that arrived while we were busy (or a path mismatch above) gets a
        // fresh look — against whatever document is open THEN, not the one we pinned.
        if (_pendingExternalChange is not null)
        {
            _externalChangeTimer.Stop();
            _externalChangeTimer.Start();
        }
    }

    /// <summary>Queue another look at the file through the normal intake, so an
    /// aborted pass is retried against the CURRENT document state, not acted on stale.</summary>
    private void RecheckExternalChange(string path) => NoteExternalChange(path);

    /// <summary>True while <paramref name="path"/> is still the open document.</summary>
    private bool StillEditing(string path) =>
        _currentPath is not null &&
        string.Equals(Path.GetFullPath(_currentPath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

    private async Task HandleExternalChangeAsync(string path)
    {
        // Pin the baseline as well as the path. A Save or Load mid-pass reassigns
        // _cleanMarkdown (always to a fresh string instance, even for identical text),
        // so a reference check detects ANY baseline movement — without it, a user Save
        // that lands during our awaits reads as "nothing to lose" and the pass would
        // quietly revert the just-saved document to the stale disk content it read.
        var cleanAtStart = _cleanMarkdown;
        bool PassValid() => StillEditing(path) && ReferenceEquals(_cleanMarkdown, cleanAtStart);

        // Don't read until the writer has stopped changing the file: a program that
        // truncates then streams lets a plain read succeed on half a document, and
        // the reload would then make that half document the new baseline.
        var newContent = await ReadWhenStableAsync(path);
        if (newContent is null) return;
        // Re-assert identity + freshness after EVERY await: if the user opened another
        // document or saved while we waited, acting now would clobber it — and the
        // unsaved-work check below would cheerfully call it "nothing to lose".
        if (!PassValid()) { RecheckExternalChange(path); return; }

        if (string.Equals(newContent, _cleanMarkdown, StringComparison.Ordinal)) return;

        // Ask the editor what it actually holds rather than trusting `_dirty`, which
        // is a debounced cache: ScheduleDirtyCheck() RESTARTS a 250ms timer on every
        // keystroke, so during continuous typing it never fires and `_dirty` stays
        // false for the whole burst. Believing it here would silently discard live
        // edits — with no backup, since this path deliberately writes none.
        var inMemory = await TryGetDocumentMarkdownAsync();
        if (inMemory is null) return;                       // couldn't ask; don't guess
        if (!PassValid()) { RecheckExternalChange(path); return; }
        var hasUnsavedWork = !string.Equals(inMemory, _cleanMarkdown, StringComparison.Ordinal);

        // Nothing unsaved: the in-memory copy IS the old disk version, so there's
        // nothing to lose, nothing worth backing up, and nothing to ask about.
        if (_autoReload && !hasUnsavedWork)
        {
            // Last look at the disk right before acting: if the file moved again after
            // our stable read (or a mid-pass save rewrote it), newContent is stale —
            // reload nothing, and let the recheck run against the current state.
            string? confirm;
            try { confirm = await File.ReadAllTextAsync(path); }
            catch { RecheckExternalChange(path); return; }
            if (!PassValid() || !string.Equals(confirm, newContent, StringComparison.Ordinal))
            {
                RecheckExternalChange(path);
                return;
            }
            await ReloadPreservingPositionAsync(path, newContent, PassValid);
            return;
        }

        // Save the current (possibly unsaved) in-memory version as a timestamped backup.
        string backupPath;
        try { backupPath = WriteTimestampedBackup(path, inMemory); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't write a backup of your current version:\n{ex.Message}\n\nThe disk version was NOT reloaded.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ExternalChangeDialog(Path.GetFileName(path), backupPath) { Owner = this };
        dlg.ShowDialog();
        if (!StillEditing(path)) return;   // the modal pumps messages; don't act on a swapped document

        switch (dlg.Choice)
        {
            case ExternalChangeChoice.Reload:
                await LoadDocumentAsync(newContent, path);
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

    /// <summary>
    /// Read only once the file has stopped changing.
    ///
    /// Retrying on IOException isn't enough: a writer that truncates and then streams
    /// leaves the file readable the whole time, so a plain read happily returns half a
    /// document. Reloading that would make the truncated text the new baseline and the
    /// next Save would write it over the good file. So sample size+timestamp until two
    /// consecutive looks agree, and only then read.
    /// </summary>
    private static async Task<string?> ReadWhenStableAsync(string path)
    {
        long lastLen = -1;
        var lastWrite = DateTime.MinValue;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length == lastLen && fi.LastWriteTimeUtc == lastWrite)
                    return await File.ReadAllTextAsync(path);
                lastLen = fi.Length;
                lastWrite = fi.LastWriteTimeUtc;
            }
            catch (IOException) { /* locked mid-write — keep sampling */ }
            catch { return null; }
            await Task.Delay(80);
        }
        // Still changing after ~1s: refuse rather than read a possibly half-written
        // document. A later watcher event (via the pending-path intake) brings us back.
        return null;
    }

    /// <summary>Character index where a 0-based source line starts, or -1.</summary>
    private static int CharIndexOfLine(string text, int line)
    {
        if (line <= 0) return 0;
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (++seen == line) return Math.Min(i + 1, text.Length);
        }
        return -1;
    }

    // JS speaks camelCase; DocAnchor is PascalCase.
    private static readonly JsonSerializerOptions AnchorJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Swap in the new content without the reader losing their place. Only ever called
    /// when nothing is unsaved (see HandleExternalChangeAsync). The caller's full
    /// validity predicate (identity + baseline pin) is re-checked after the anchor
    /// capture's editor round-trip — StillEditing alone would miss a Save landing in
    /// that window.
    /// </summary>
    private async Task ReloadPreservingPositionAsync(string path, string newContent, Func<bool> stillValid)
    {
        var anchor = await CaptureAnchorAsync();
        if (!stillValid()) { RecheckExternalChange(path); return; }
        await LoadDocumentAsync(newContent, path);
        await RestoreAnchorAsync(anchor);
        FlashStatus("Reloaded — file changed on disk");
    }

    private async Task<DocAnchor?> CaptureAnchorAsync()
    {
        if (_sourceMode)
        {
            // GetFirstVisibleLineIndex counts DISPLAY lines, which stop matching source
            // lines as soon as word wrap is on — so convert through a character index
            // rather than feeding a display index to ScrollAnchor's newline-based logic.
            var firstDisplay = SourceBox.GetFirstVisibleLineIndex();
            if (firstDisplay < 0) return null;   // no layout yet: no anchor beats a wrong one
            var text = SourceBox.Text;
            var charIndex = SourceBox.GetCharacterIndexFromLineIndex(firstDisplay);
            if (charIndex < 0 || charIndex > text.Length) return null;
            var sourceLine = 0;
            for (var i = 0; i < charIndex; i++) if (text[i] == '\n') sourceLine++;
            var totalLines = 1;
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') totalLines++;
            var ratio = (double)sourceLine / totalLines;   // ResolveLine consumes this as a LINE ratio
            return ScrollAnchor.Capture(text, sourceLine, ratio);
        }
        if (!_editorReady) return null;
        var json = await RunEditorAsync("JSON.stringify(window.MDM.getScrollAnchor())");
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<DocAnchor>(json, AnchorJson); }
        catch { return null; }
    }

    private async Task RestoreAnchorAsync(DocAnchor? anchor)
    {
        if (anchor is null) return;
        if (_sourceMode)
        {
            var line = ScrollAnchor.ResolveLine(SourceBox.Text, anchor);
            if (line < 0) return;
            // ResolveLine returns a SOURCE line; ScrollToLine wants a DISPLAY line, and
            // LineCount is -1 when layout info isn't available — in which case clamping
            // would hand ScrollToLine a bad index and it throws (on an async void path,
            // so it would take the process down). Skip instead.
            var lineCount = SourceBox.LineCount;
            if (lineCount <= 0) return;
            var charIndex = CharIndexOfLine(SourceBox.Text, line);
            var display = charIndex >= 0 ? SourceBox.GetLineIndexFromCharacterIndex(charIndex) : line;
            if (display < 0) return;
            SourceBox.ScrollToLine(Math.Clamp(display, 0, lineCount - 1));
            return;
        }
        if (!_editorReady) return;
        var json = JsonSerializer.Serialize(anchor, AnchorJson);
        await RunEditorAsync($"window.MDM.restoreScrollAnchor({json})");
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
            // Load up to the storage cap, not the display limit: the store is the
            // history, _recentLimit only decides how much of it the menu shows.
            if (list is not null) _recentFiles.AddRange(list.Take(SettingsDialog.MaxRecentLimit));
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
        TrimRecent();
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
        foreach (var path in _recentFiles.Take(_recentLimit))
        {
            // Only 1-9 get an access key: with a limit above 9, "_1" and "_10" would
            // claim the same key and pressing it would cycle rather than open.
            // Double the underscores in the name itself - WPF eats a single one and
            // turns the next character into a stray access key.
            var name = Path.GetFileName(path).Replace("_", "__");
            var header = i <= 9 ? $"_{i} {name}" : $"{i} {name}";
            var item = new MenuItem { Header = header, Tag = path, ToolTip = path };
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
        public bool WordWrap { get; set; } // source-view line wrapping; off = horizontal scroll
        public bool AutoReload { get; set; } = true; // silently reload externally-changed files when nothing is unsaved
        public int RecentLimit { get; set; } = 10;   // entries kept in Open Recent
        public bool StartWithBlankDocument { get; set; } // else the no-document placeholder
        // Remembered window placement. Null/zero means "never saved" -> default layout.
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }
        public double? WindowWidth { get; set; }
        public double? WindowHeight { get; set; }
        public bool WindowMaximized { get; set; }
    }

    private bool _spellCheck = true;         // persisted; applied on editor-ready
    private bool _skipCodeSpell = true;      // persisted; exempt code from spell check
    private bool _wordWrap;                  // persisted; wrap long lines in the source view
    private bool _autoReload = true;         // persisted; see OnExternalChangeAsync
    private int _recentLimit = 10;           // persisted; Open Recent length
    private bool _settingsUnknown;           // the settings read failed: never write this session
    private Rect? _savedBounds;              // persisted; normal (un-maximized) bounds
    private bool _savedMaximized;
    private bool _startWithBlankDocument;    // persisted; startup lands on a blank doc

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
            // A read that fails leaves every field at its default. Writing those
            // defaults back would wipe the user's real settings, so remember that we
            // don't know what's on disk and stay read-only for the session.
            if (!TryReadSettings(out var s))
            {
                _settingsUnknown = true;
                return;
            }
            if (s is null) return;
            if (s.PageWidth is "portrait" or "landscape" or "full")
                _pageWidth = s.PageWidth;
            if (s.PrintPrefs is not null)
                foreach (var kv in s.PrintPrefs)
                    if (kv.Key is "portrait" or "landscape" or "full" && kv.Value is not null)
                        _printPrefs[kv.Key] = kv.Value;
            _spellCheck = s.SpellCheck;
            _skipCodeSpell = s.SkipCodeSpellCheck;
            _wordWrap = s.WordWrap;
            _autoReload = s.AutoReload;
            _recentLimit = Math.Clamp(s.RecentLimit, SettingsDialog.MinRecent, SettingsDialog.MaxRecentLimit);
            _startWithBlankDocument = s.StartWithBlankDocument;
            _savedBounds = s.WindowWidth is > 0 && s.WindowHeight is > 0
                ? new Rect(s.WindowLeft ?? 0, s.WindowTop ?? 0, s.WindowWidth.Value, s.WindowHeight.Value)
                : null;
            _savedMaximized = s.WindowMaximized;
        }
        catch { /* defaults are fine */ }
    }

    /// <summary>
    /// Persist only the window rectangle, merged onto whatever is on disk right now.
    /// Every window closes, including ones that never touched a setting: help windows
    /// and the extra instances spawned by a multi-file drop all share settings.json,
    /// so writing this window's whole launch-time snapshot on close would silently
    /// revert toggles another instance changed in the meantime.
    /// </summary>
    private void SaveWindowPlacement()
    {
        if (_isHelpWindow || _settingsUnknown) return;   // a help viewer's geometry isn't the app's
        try
        {
            // A read that FAILS is not the same as no file. Another instance may be
            // mid-write, or the file briefly locked; treating that as "no settings"
            // and writing defaults would wipe every preference the user has. Only a
            // genuinely absent file justifies starting from defaults — otherwise
            // skip this write entirely, because geometry is not worth that.
            if (!TryReadSettings(out var s)) return;
            // No file to merge onto — either a first run or the file was just
            // quarantined as corrupt. Either way this session's own preferences are
            // the best record there is; writing defaults would discard them.
            s ??= CurrentSettings();
            s.WindowLeft = _savedBounds?.X;
            s.WindowTop = _savedBounds?.Y;
            s.WindowWidth = _savedBounds?.Width;
            s.WindowHeight = _savedBounds?.Height;
            s.WindowMaximized = _savedMaximized;
            WriteSettings(s);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// True when the stored settings are known: either parsed (<paramref name="s"/>
    /// set) or definitely absent (<paramref name="s"/> null). False means the read
    /// failed and the caller must not assume anything about what's on disk.
    /// </summary>
    private static bool TryReadSettings(out AppSettings? s)
    {
        s = null;
        try
        {
            if (!File.Exists(SettingsStorePath)) return true;   // absent, not unknown
            s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsStorePath));
            return true;
        }
        catch (JsonException)
        {
            // Definitively unreadable rather than momentarily unavailable. Set it
            // aside and carry on as though there were no settings file: refusing to
            // write would leave the app permanently unable to save anything, on this
            // and every future launch, with nothing to show the user why.
            try { File.Move(SettingsStorePath, SettingsStorePath + ".bad", overwrite: true); }
            catch { /* if it can't be moved, the next write overwrites it anyway */ }
            return true;
        }
        catch { return false; }   // locked, in use, denied — unknown, so don't write
    }

    /// <summary>
    /// Write via a temp file so a reader never sees a half-written document — extra
    /// instances of the app share this file. The temp name carries the process id:
    /// with one shared name, two instances writing at once would publish each
    /// other's content, and the second's move would fail outright.
    /// </summary>
    private static void WriteSettings(AppSettings s)
    {
        var dir = Path.GetDirectoryName(SettingsStorePath)!;
        Directory.CreateDirectory(dir);
        var tmp = $"{SettingsStorePath}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            File.Move(tmp, SettingsStorePath, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave it */ } }
    }

    /// <summary>
    /// Persist the preferences, leaving the stored window rectangle alone — geometry
    /// is authored only at close, by <see cref="SaveWindowPlacement"/>. Writing this
    /// instance's launch-time copy of it here would revert a position another
    /// instance saved in the meantime.
    /// </summary>
    private void SaveSettings()
    {
        if (_settingsUnknown) return;   // see LoadSettings: don't write over the unknown
        try
        {
            // Same rule as SaveWindowPlacement: if we can't read what's there, we
            // can't preserve the geometry it holds, and writing nulls over it would
            // lose the user's window position.
            if (!TryReadSettings(out var existing)) return;
            var s = CurrentSettings();
            // Geometry is carried through from disk, not from this instance's fields.
            s.WindowLeft = existing?.WindowLeft;
            s.WindowTop = existing?.WindowTop;
            s.WindowWidth = existing?.WindowWidth;
            s.WindowHeight = existing?.WindowHeight;
            s.WindowMaximized = existing?.WindowMaximized ?? false;
            WriteSettings(s);
        }
        catch { /* best-effort */ }
    }

    /// <summary>This session's preferences, without any window geometry — the two
    /// are persisted on different schedules and by different writers.</summary>
    private AppSettings CurrentSettings() => new()
    {
        PageWidth = _pageWidth,
        PrintPrefs = _printPrefs,
        SpellCheck = _spellCheck,
        SkipCodeSpellCheck = _skipCodeSpell,
        WordWrap = _wordWrap,
        AutoReload = _autoReload,
        RecentLimit = _recentLimit,
        StartWithBlankDocument = _startWithBlankDocument,
    };

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
        // Record placement first: the close may be cancelled by the prompt below, and
        // this is the geometry the user last chose either way.
        CaptureWindowPlacement();
        SaveWindowPlacement();
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
        _spellCheck = MenuSpellCheck.IsChecked;
        SaveSettings();               // remember the choice across sessions
        RequestSpellCheckSoon();      // runs, or clears squiggles, per the new state
        RefocusEditor();
    }

    private void SkipCodeSpell_Click(object sender, RoutedEventArgs e)
    {
        _skipCodeSpell = MenuSkipCodeSpell.IsChecked;
        SaveSettings();
        RequestSpellCheckSoon();
        RefocusEditor();
    }

    private void AutoReload_Click(object sender, RoutedEventArgs e)
    {
        _autoReload = MenuAutoReload.IsChecked;
        SaveSettings();
    }

    // A transient status-bar note. Silently swapping the document under the reader
    // would feel like a glitch; this says what happened without stealing focus.
    private readonly DispatcherTimer _noteTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private void FlashStatus(string message)
    {
        StatusNote.Text = message;
        _noteTimer.Stop();
        _noteTimer.Start();
    }

    private void WordWrap_Click(object sender, RoutedEventArgs e) =>
        SetWordWrap(MenuWordWrap.IsChecked);

    private void WordWrapButton_Click(object sender, RoutedEventArgs e) =>
        SetWordWrap(WrapToggle.IsChecked == true);

    // Single entry point so the View menu item and the toolbar button can't drift.
    private void SetWordWrap(bool on)
    {
        _wordWrap = on;
        MenuWordWrap.IsChecked = on;
        ApplyWordWrap();
        UpdateWrapToggleUi();
        SaveSettings();               // remember the choice across sessions
        if (_sourceMode) SourceBox.Focus();
    }

    // Wrap long lines in the raw-markdown source view, or scroll horizontally.
    // Applies only to the source TextBox; the WYSIWYG view always reflows.
    private void ApplyWordWrap()
    {
        SourceBox.TextWrapping = _wordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        SourceBox.HorizontalScrollBarVisibility =
            _wordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    // Word wrap only means anything in the source view, so the toolbar button is
    // disabled and reads "off" in WYSIWYG (which always reflows). The persisted
    // preference is untouched — the View menu still shows it — so switching back to
    // source restores the button to the real setting.
    private void UpdateWrapToggleUi()
    {
        WrapToggle.IsEnabled = _sourceMode;
        WrapToggle.IsChecked = _sourceMode && _wordWrap;
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
            await FocusDocumentAsync();
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
        new AboutDialog { Owner = this }.ShowDialog();
    }

    // Quiet startup check: a status note, never a dialog. Prereleases are only
    // suggested to users already running a prerelease.
    private async Task NotifyIfUpdateAvailableAsync()
    {
        try
        {
            var current = Updates.UpdateVersion.Parse(AppVersion);
            var check = await Updates.UpdateService.CheckAsync();
            if (check is null) return;
            // Same predicates the About box uses, including how they behave when the
            // running version can't be read — bailing out here instead would mean no
            // flash on launch and a live Update button in About, which is the kind of
            // disagreement between two surfaces that reads as a bug either way.
            // The extra IsPrerelease gate is deliberate and stays: someone running a
            // stable build shouldn't be nudged toward a beta they didn't ask for.
            var stableNewer = Updates.UpdateOffer.ShowStableUpdate(check.Stable, current);
            var preNewer = current is { IsPrerelease: true } &&
                Updates.UpdateOffer.ShowPrerelease(check.PrereleaseRelease, check.Stable, current);
            if (stableNewer || preNewer)
                FlashStatus("Update available — Help ▸ About");
        }
        catch { /* purely advisory */ }
    }

    // ===== Dirty tracking (content vs. last opened/saved markdown) =====

    private void ScheduleDirtyCheck()
    {
        _dirtyTimer.Stop();
        _dirtyTimer.Start();
    }

    private void Source_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_sourceMode) return;
        _ = UpdateDirtyAsync();
        foreach (var c in e.Changes)
            _squiggles?.ShiftForEdit(c.Offset, c.AddedLength, c.RemovedLength);
        RequestSpellCheckSoon();
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
        UpdateCounts(current);
    }

    /// <summary>Word/character count in the status bar, from markdown we already have.</summary>
    private void UpdateCounts(string? markdown)
    {
        _countText = TextStats.Measure(markdown).ToStatusText();
        ApplyCountText();
    }

    /// <summary>
    /// Show the count only while a document is open. Held separately from the
    /// measurement because content is installed before the closed flag clears, so
    /// reading _closed at measure time would blank the count of the document that
    /// was just opened.
    /// </summary>
    private void ApplyCountText()
    {
        StatusCount.Text = _closed ? string.Empty : _countText;
        StatusCountDivider.Visibility = _closed ? Visibility.Collapsed : Visibility.Visible;
    }

    private string _countText = string.Empty;

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
