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

    /// <summary>
    /// How many times the window has STARTED switching between the formatted and
    /// markdown views. Bumped at the top of <see cref="SetSourceModeAsync"/> — the
    /// only writer of <see cref="_sourceMode"/> — before its first await, so a
    /// switch that is half-done counts as a movement even though the flag has not
    /// turned over yet.
    ///
    /// Only drops read it (<see cref="DropHandshake.StillApplies"/>): a drop decides
    /// at drop time and inserts after a read it does not control. Everything else
    /// reads <see cref="_sourceMode"/> where it acts on it — <c>FocusDocumentAsync</c>
    /// and <c>ApplyUpdate_Click</c> come back from an await and read it THERE, not to
    /// apply a decision taken earlier — so a stale value cannot outlive its decision.
    ///
    /// One exception, and it routes CONTENT: <c>Picture_Click</c> awaits
    /// <c>PickedPicture.ReadAsync</c> (the picked file's read) and then calls <c>InsertMarkdownFragment</c>,
    /// which routes on <see cref="_sourceMode"/>. A Ctrl+E during that read can land
    /// the picture in the half being discarded, because the flag does not turn over
    /// until the bottom of <see cref="SetSourceModeAsync"/>. Left untracked on
    /// purpose — the file is one the user picked a moment ago and the window is far
    /// shorter than a drop's — but it IS a window, not the "no gap to see" this
    /// comment used to claim of everything but drops.
    /// </summary>
    private long _viewGeneration;

    private bool _syncingStyle;
    private bool _showMarks;

    // Dirty tracking by content comparison: the document is "unchanged" whenever it
    // matches the last opened/saved markdown — so undoing back to that state clears
    // the modified flag, and undo past the Open state is impossible (history flushed).
    // This is the AUTHORITATIVE SURFACE's spelling of that state: normally the
    // editor's serialisation, and the source box's own text whenever the box is the
    // authoritative copy (a load while in source view sets it from there, a save from
    // the source view — Save, Encrypt, Change Password, Convert to plaintext — takes
    // it from SourceBox.Text, and leaving the source view unmodified adopts the
    // editor's words back).
    private string _cleanMarkdown = string.Empty;
    // The file as it was last read from or written to disk, folded to LF - the
    // second baseline (issue #4). _cleanMarkdown is normally the EDITOR's
    // serialisation of that state and differs from it whenever the editor
    // normalises, so "did the file change on disk?" is asked of this one AS WELL AS
    // _cleanMarkdown: a rewrite equal to either is not a change (see ExternalChange).
    // It is also what the source view SHOWS for an unmodified document, so that a
    // file edited and saved only there comes back as it was typed (see SourceText);
    // IsUnmodifiedText therefore accepts either spelling while the source view is up.
    // Set wherever _cleanMarkdown is set from disk: load, save, reload, Keep.
    private string _diskBaseline = string.Empty;
    // What the open file's bytes looked like, so Save writes them back the same way
    // (issue #3): its line-ending convention and whether it began with a UTF-8
    // byte-order mark. In memory the document is always LF (DocumentText). Set on
    // every load path: a new document gets DocumentText's defaults, a dropped one
    // keeps the endings the browser handed over (its mark is already gone).
    private LineEnding _lineEnding = DocumentText.DefaultLineEnding;
    private bool _hadBom;
    private bool _suppressDirty;
    private string? _pendingOpenPath;

    // ===== Secure Markdown document state =====
    // The password lives only in this window's memory for the life of the
    // document (design 7c/7e: never on disk, never on a relaunch command line).
    // _docEncrypted implies _currentPath is a .mdenc the password opens.
    private bool _docEncrypted;
    private string? _docPassword;
    // The crash-backup session key: the full KDF paid once per (window,password),
    // then every 5-second snapshot is AES-only. Cleared whenever the password
    // changes or the document stops being encrypted.
    private byte[]? _backupKey;
    private byte[]? _backupSalt;
    private bool _showEncryptedInOpen;
    private bool _useBuiltInPicker;

    private readonly List<string> _recentFiles = new();
    private string _pageWidth = "landscape"; // portrait | landscape | full (persisted)
    private bool _startReadOnly;
    private bool _startInSource;  // --source: open showing the raw markdown
    private bool _isHelpWindow;
    // --new: force a blank editable document regardless of _startWithBlankDocument,
    // so File ▸ New always lands you somewhere you can type, never on the splash.
    private bool _forceBlankDocument;
    private string? _lastSeenChangelogVersion;
    private bool _readOnly;
    private bool _closed;            // closed/no-document state — shows ClosedSplash
    private (int curW, int curH, int natW, int natH) _imgResize;
    private readonly DispatcherTimer _dirtyTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private FindDialog? _findDialog;
    private int _sourceFindCursor = -1; // index into _sourceFindMatches
    private System.Text.RegularExpressions.MatchCollection? _sourceFindMatches;
    private string _lastFindSource = "";
    private string _lastFindFlags = "";
    private (int Start, int Length)? _sourceFindSelection;   // the selection Find/Replace last made in the source view
    // The source selection as it was when the Find dialog opened, kept as anchors so
    // it follows the replacements made through it: the Replace All scope once Find's
    // own selection has taken the place of the user's. Any other edit drops it.
    private (ICSharpCode.AvalonEdit.Document.TextAnchor Start, ICSharpCode.AvalonEdit.Document.TextAnchor End)? _sourceReplaceScope;
    private bool _applyingReplace;      // our own source edits must not drop that scope
    private bool _replaceScopeHooked;

    // External-change tracking for the currently-open file.
    private FileSystemWatcher? _watcher;
    private bool _suppressWatcher;       // set true around our own writes
    private bool _externalChangeBusy;    // re-entrancy guard: dialog OR auto-reload in flight

    public MainWindow()
    {
        InitializeComponent();
        RegisterShortcuts();
        InitAccessKeys();
        SourceToggle.Content = GlyphSource; // start in WYSIWYG; button offers source view
        _dirtyTimer.Tick += async (_, _) => { _dirtyTimer.Stop(); await UpdateDirtyAsync(); };

        // Settings first: LoadRecent and BuildRecentMenu both consult _recentLimit,
        // so loading them in the other order silently applies the default of 10.
        LoadSettings();
        LoadRecent();
        BuildRecentMenu();

        // After LoadSettings, which is where the persisted theme name comes from, and
        // before the editor is ready — the theme itself is applied on 'ready', once
        // there is a page to apply it to.
        InitializeThemes();
        BuildThemeMenu();

        // Apply the persisted spell-check state. Checking is done by the app's own
        // engine (see MainWindow.Spell.cs); native spell check stays off everywhere.
        MenuSpellCheck.IsChecked = _spellCheck;
        MenuSkipCodeSpell.IsChecked = _skipCodeSpell;
        InitSpell();
        InitSource();

        // Apply the persisted source-view word-wrap state (starts in WYSIWYG, so the
        // toolbar button starts disabled/off).
        MenuWordWrap.IsChecked = _wordWrap;
        ApplyWordWrap();
        UpdateWrapToggleUi();
        MenuAutoReload.IsChecked = _autoReload;
        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); StatusNote.Text = string.Empty; };
        _backupTimer.Tick += async (_, _) => await WriteBackupAsync();

        // (Window placement is applied later, in OnSourceInitialized — it needs the HWND.)
        Updates.UpdateService.CleanupOldBinaries();
        _ = NotifyIfUpdateAvailableAsync();
        _externalChangeTimer.Tick += async (_, _) => await OnExternalChangeTimerAsync();

        // The strategy is per PROCESS, and every window is its own process here,
        // so this is simply "what this window does" — read from the shared
        // settings file at launch and re-read by each new window.
        Picker.FilePickerService.UseBuiltIn = _useBuiltInPicker;
        Picker.FilePickerService.AutoSwitchedToBuiltIn = OnPickerAutoSwitched;

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--readonly" or "-r" or "/readonly") _startReadOnly = true;
            else if (arg is "--source" or "/source") _startInSource = true;
            else if (arg is "--help-window") { _isHelpWindow = true; _startReadOnly = true; }
            else if (arg is "--new") _forceBlankDocument = true;
            else if (arg == "--recover" && i + 1 < args.Length)
            {
                // Launched by another window to take one specific crashed session.
                _recoverSessionId = args[++i];
            }
            else if (arg == "--finish-move" && i + 1 < args.Length)
            {
                // We were launched by a "move" install to delete the original download
                // once its process has exited. Consume the path so it isn't opened.
                RegistrationService.FinishMove(args[++i]);
            }
        }
        // Which argument is the document is decided in one place, because
        // App.OnStartup asks the same question before this window exists (the
        // already-open check) and the two answers must not drift.
        _pendingOpenPath = DocumentArgument(args);

        if (_isHelpWindow) MenuViewHelp.IsEnabled = MenuWhatsNew.IsEnabled = false; // no help-of-help
        else UpdateWhatsNewBadge();   // a help/changelog viewer doesn't get its own badge
        StartBackup();

        Loaded += async (_, _) => await InitializeEditorAsync();
        Closing += MainWindow_Closing;
        UpdateTitle();
    }

    /// <summary>
    /// The document path on a command line: the first argument that names an
    /// existing file and is not the VALUE of --recover or --finish-move. The
    /// latter's value is the downloaded exe, which exists and must not be opened as
    /// a document. Mirrors the loop in the constructor, which consumes those values
    /// the same way for its own purposes.
    /// </summary>
    internal static string? DocumentArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--recover" or "--finish-move") { if (i + 1 < args.Length) i++; continue; }
            if (File.Exists(arg)) return arg;
        }
        return null;
    }

    /// <summary>
    /// What a relaunch that reopens this document (Apply update, the About dialog's
    /// update) starts the new process with: the document path, then the view flags,
    /// all of which the parser above honours. --readonly rides only when read-only
    /// is the user's own (ImposedReadOnly.IsUsersOwn): what the already-open
    /// fallback imposed would read to the new process as the user's choice, and
    /// every later normal open there would stay read-only, Save disabled, with no
    /// message. The new process claims the document afresh and imposes its own
    /// read-only if it must. The one place that decides these, so the two relaunch
    /// sites cannot drift; pure, so it can be tested.
    /// </summary>
    internal static List<string> RelaunchArguments(string? currentPath, bool readOnly,
                                                   Instances.ImposedReadOnly imposed, bool sourceMode)
    {
        var args = new List<string>();
        if (currentPath is not null) args.Add(currentPath);
        if (imposed.IsUsersOwn(readOnly)) args.Add("--readonly");
        if (sourceMode) args.Add("--source");
        return args;
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
            // The renderer holds decrypted Secure Markdown content; a renderer
            // crash must not serialize it into a Crashpad minidump under the
            // profile folder. (The per-launch profile is best-effort deleted on a
            // LATER launch, so a dump could otherwise linger on disk.)
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-crash-reporter",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, userData, envOptions);
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
        _webEnvironment = core.Environment;
        core.SetVirtualHostNameToFolderMapping(
            VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += OnEditorNavigationCompleted;

        // Every request, not just the document host's. This is where off-origin is
        // refused, and serving the open document's images (see ApplyDocBaseAsync) is
        // now one branch of a handler whose main job is to let nothing else out.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;

        // The request filter refuses an off-origin LOAD, but a top-level navigation
        // still commits — the WebView would become the refusal page at the foreign
        // address, with the editing surface gone and no way back. Cancel it instead.
        core.NavigationStarting += OnNavigationStarting;

        // And no second window, ever. A new window is a SEPARATE CoreWebView2: it does
        // not inherit the request filter, the navigation guard, or the CSP — that last
        // one because the policy is a meta tag on our own page. So a document holding
        // `<a target="_blank" href="http://…">` was one click from a fully unguarded
        // browser, where an off-origin script ran and fetch reached the wire. `target`
        // survives the sanitizer deliberately, because asking to open in a new tab is
        // a reasonable thing for a document to do.
        //
        // Suppressed rather than redirected, which also makes links behave uniformly:
        // a plain link click is already cancelled by the navigation guard, so this is
        // the same answer to the same question. Opening the system browser instead
        // would be a product decision rather than a security one.
        core.NewWindowRequested += (_, e) => e.Handled = true;

        // Lock down the host shell: it is a local app, not a browser.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // The WebView covers the window centre, so let it accept drops; the editor
        // intercepts file drops and posts them to the host (see the 'fileDrop'
        // message). Drops on the toolbar/menu, and on the source view, are still
        // handled by Window_Drop; both routes decide by DropRouting.
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
                // A fresh page carries a fresh dropSeq, which starts at 0 and numbers
                // its first drop 1. The high-water mark has to start over with it, or
                // every drop this page ever posts is older than one already handled
                // and is discarded in silence. (See _newestDrop.)
                _newestDrop = 0;
                // Bridge is wired; hand the editor its initial (empty) document and
                // its options — the picture ceiling its paste guard applies. The
                // script is built (and pinned) in EditorScripts: spelled out here,
                // the options could be quoted without a single test noticing, and
                // the guard would take no ceiling at all.
                _ = RunEditorAsync(EditorScripts.Create(string.Empty));
                break;
            case "ready":
                _editorReady = true;
                _ = RunEditorAsync($"window.MDM.setPageWidth({JsLiteral(_pageWidth)})");
                // Native (browser) spell check stays OFF — the app runs its own engine
                // with a private dictionary; squiggles come from host-computed ranges.
                _ = RunEditorAsync("window.MDM.setSpellcheck(false)");
                // Applied here rather than at construction: setTheme needs a page.
                // A theme that has gone missing between launches says so and falls
                // back WITHOUT forgetting the choice — reverting silently is the thing
                // that reads as the app losing a setting.
                _ = ApplyStartupThemesAsync();
                RequestSpellCheckSoon();
                UpdatePageWidthChecks();
                _ = ApplyLandingStateAsync();
                break;
            case "change":
                if (!_sourceMode)
                {
                    ScheduleDirtyCheck();
                    RequestSpellCheckSoon();
                }
                // Edits invalidate the WYSIWYG find index — force a re-scan on next find.
                ForgetWysiwygFindIndex();
                break;
            case "selection":
                // Reflect what's at the cursor: block type in the Style dropdown,
                // mark states on the B/I/U/S toggles (Word protocol — the editor's
                // answer is authoritative, so a user click is confirmed or reverted
                // by the state the editor reports back).
                if (!_sourceMode)
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    if (d.RootElement.TryGetProperty("style", out var s))
                        SyncStyleCombo(s.GetString() ?? "paragraph");
                    if (d.RootElement.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Object)
                    {
                        bool On(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                        SyncMarkToggles(On("bold"), On("italic"), On("underline"), On("strike"), On("code"));
                    }
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
                    // doesn't replace the structural menus. Read-only does NOT gate
                    // this: squiggles are drawn in read-only windows, and the
                    // dictionary actions (Add to Dictionary, Ignore All) change
                    // editor state, not the document. BuildSpellItemsAsync disables
                    // the document-mutating items instead.
                    SpellClick? spell = null;
                    if (_spellCheck &&
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
                    // Read-only routes EVERY spell click to the dynamic menu: a
                    // read-only table/image click falls back to TextContextMenu,
                    // which has no spellRoot placeholder to fold the block into —
                    // and HELP.md keeps its squiggles inside tables, so that
                    // fallback was exactly the reported repro.
                    if (spell is { } si && (menu == "text" || _readOnly))
                        Dispatcher.BeginInvoke(async () => await ShowSpellContextMenuAsync(x, y, si));
                    else
                        Dispatcher.BeginInvoke(async () => await ShowEditorContextMenuAsync(menu, x, y, spell));
                }
                break;
            case "fileDrop":
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    var message = DropRouting.ParseMessage(d.RootElement);
                    Dispatcher.BeginInvoke(() => HandleDroppedFiles(message));
                }
                break;
            case "droppedFileBytes":
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    var answer = DropRouting.ParseBytesMessage(d.RootElement);
                    Dispatcher.BeginInvoke(() => CompleteDroppedFileRead(answer));
                }
                break;
            case PictureLimit.RefusedMessageType:
                // The editor cancelled a paste of a picture past the ceiling — the
                // paste is Chromium's, so the editor is where it can be stopped.
                // Said in the words every other route uses for the same refusal.
                FlashStatus(PictureLimit.Notice(null));
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
        await LoadDocumentAsync(DocumentText.NewDocument, null);
        await FocusDocumentAsync();
    }

    /// <summary>
    /// The native dialog crashed in its child process and the service switched
    /// to the built-in picker. Persist that so LATER launches start where this
    /// one ended up (windows already open are separate processes and keep their
    /// own setting until they restart); the service has already told the user.
    /// </summary>
    private void OnPickerAutoSwitched()
    {
        _useBuiltInPicker = true;
        // SavePersistentField, not SaveSettings: this fires from a background
        // event while other WINDOWS (separate processes) may have written their
        // own preferences since we launched, and SaveSettings would republish
        // this instance's launch-time snapshot over them.
        SavePersistentField(s => s.UseBuiltInPicker = true);
    }

    /// <summary>Folders worth offering in the built-in picker's rail: where the
    /// open document lives, then recently used files' folders.</summary>
    private IReadOnlyList<string> PickerRecentFolders()
    {
        var folders = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !folders.Contains(dir, StringComparer.OrdinalIgnoreCase))
                    folders.Add(dir);
            }
            catch { /* an unparseable recent entry simply doesn't contribute */ }
        }
        Add(_currentPath);
        foreach (var recent in _recentFiles) Add(recent);
        return folders;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_startWithBlankDocument, _recentLimit, _backupEnabled,
                                     _showEncryptedInOpen, _useBuiltInPicker, ImportCustomDic) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _startWithBlankDocument = dlg.StartWithBlankDocument;
        if (dlg.KeepBackup != _backupEnabled)
        {
            _backupEnabled = dlg.KeepBackup;
            // Turning it off must take the existing copy with it — leaving unsaved
            // content on disk after the user said not to keep it is the opposite of
            // what they asked for. Turning it on starts protecting from here.
            if (_backupEnabled) { StartBackup(); _backupDirty = true; }
            else EndBackup();
        }
        _showEncryptedInOpen = dlg.ShowEncryptedInOpen;
        _useBuiltInPicker = dlg.UseBuiltInPicker;
        Picker.FilePickerService.UseBuiltIn = _useBuiltInPicker;
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

    // The one spelling of a JavaScript literal, in EditorScripts with the scripts
    // that are built from it (and testable there, as nothing in this file is).
    private static string JsLiteral(string value) => EditorScripts.JsLiteral(value);

    /// <summary>
    /// The document's markdown, or null when the editor can't be asked. Callers making
    /// destructive decisions must not treat "couldn't ask" as "the document is empty".
    /// </summary>
    private async Task<string?> TryGetDocumentMarkdownAsync()
    {
        if (_sourceMode) return SourceBox.Text;
        if (!_editorReady) return null;
        // "Couldn't be asked" has to include a WebView2 that has died, which throws
        // instead of answering — and nothing sets _editorReady back to false, because
        // there is no ProcessFailed handler. Without this catch every guard built on
        // the null return is unreachable in exactly the failure it was written for,
        // and the throw escapes through an async void handler and takes the process
        // with it, unsaved work and all.
        try { return await RunEditorAsync("window.MDM.getMarkdown()"); }
        catch { return null; }
    }

    // There is deliberately no variant that returns "" when the editor can't answer.
    // Every caller here either saves, discards, or decides whether there is unsaved
    // work, and each of those reads an empty document as "nothing to protect". Make
    // the failure impossible to ignore instead: TryGet returns null, and the caller
    // has to say what that means.

    /// <summary>
    /// Installs <paramref name="markdown"/> in both surfaces and hands back what the
    /// editor settled on — the serialisation it will return from now on, and so the
    /// markdown a clean baseline should be taken from. Null when the editor could not
    /// be asked (not ready, or it did not answer): a caller that needs to know whether
    /// the document landed in the formatted view reads that, rather than serialising
    /// the whole document a second time to ask the same question.
    /// </summary>
    private async Task<string?> SetDocumentMarkdownAsync(string markdown)
    {
        // A new document is not the one the find index was built from, and installing
        // it raises no change message to say so (#5 NF-9, the same invariant). Forget
        // it BEFORE the awaits below rather than after: if setMarkdown lands and the
        // getMarkdown after it throws — a WebView2 that has died mid-call — the new
        // document is installed while _lastFindSource still names the OLD document's
        // index, and the next Find would skip findReset and answer from it. Forgetting
        // a moment too early costs one findReset that was not needed; forgetting too
        // late is the stale answer this helper exists to prevent.
        ForgetWysiwygFindIndex();
        string? settled = null;
        // Editor first, source box second. If the script throws, both surfaces are
        // left showing the OLD document — which is what _currentPath still says, since
        // callers assign it after this returns. Setting the source box first would
        // leave the visible document ahead of the fields, and in source view that
        // mismatch becomes a crash snapshot labelled with the wrong file.
        if (_editorReady)
        {
            await RunEditorAsync($"window.MDM.setMarkdown({JsLiteral(markdown)})");
            // setMarkdown settles the document before it returns (editor-src/src/settle.js):
            // one that ends in anything but a paragraph or a heading — a list, a table, a
            // code block, a blockquote, a thematic break — gains the editor's trailing empty
            // paragraph THERE rather than on the reader's first click or first F3.
            // What it hands back from now on is therefore what it holds now — so mirror
            // that, not what we asked for, or the clean baseline taken in source view
            // would differ from the formatted view's markdown by one blank line and the
            // document would read as modified the moment the views were swapped (#5 NF-5).
            settled = await RunEditorAsync("window.MDM.getMarkdown()");
            if (settled is not null) markdown = settled;
        }
        // The box mirrors the settled serialisation the editor just handed back, which
        // is what makes it the same document the baseline is taken from. Whether a
        // file-backed document then shows its OWN spelling in the source view (#2) is
        // not this helper's question: it runs before its callers make _diskBaseline
        // the new document's, so SourceText.For asked here would weigh the new text
        // against the OLD document's baselines. It is answered where the baselines are
        // current — SetSourceModeAsync(true) on Ctrl+E, and LoadDocumentAsync
        // (SourceText.AfterLoad) for a document loaded while the source view is
        // already showing. A document dropped on the formatted view has no file behind
        // it, so under the same rule the editor's text is the one to show.
        SourceBox.Text = markdown;
        // Count here rather than in each caller: installing content doesn't raise a
        // 'change' message, so a freshly opened document would otherwise show no
        // count at all until the first keystroke.
        UpdateCounts(markdown);
        return settled;
    }

    private async Task OpenThenApplyStartupViewAsync(string path)
    {
        // startup: this process exists only to open this file, so if the file turns
        // out to be open in another window (and that window can be focused) the
        // right outcome is for this one to close - in which case the source-view
        // flag is not applied to a window that is about to go.
        await OpenPathAsync(path, startup: true);
        if (_startInSource && !_closed && !_yieldingToOtherWindow)
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
        if (_closed)
        {
            // No document to flip between views. Put the controls back — a click has
            // already flipped them, and nothing below will resync them.
            SourceToggle.IsChecked = _sourceMode;
            MenuViewSource.IsChecked = _sourceMode;
            return;
        }

        // The view is moving, and from HERE it is unsafe for a drop to insert:
        // _sourceMode does not change until the bottom of this method, so anything
        // pinned on the flag alone sees no movement across either await below —
        // which is the whole of the window in which InsertMarkdownFragment would
        // route to the half being discarded (DropHandshake.StillApplies). Bumped
        // past the two returns above — already in that view, and no document to flip
        // between views; the view stays where it is in both — and before the first
        // await, so an in-flight switch counts as much as a finished one. A
        // switch that fails below leaves the view where it was and still bumps: a
        // drop is abandoned that need not have been, which is the safe way round.
        _viewGeneration++;

        if (on)
        {
            // Entering source: pull the latest markdown out of the editor. If it
            // can't answer, stay where we are rather than showing an empty box —
            // once in source view that emptiness becomes the document, because the
            // source box is then the authoritative copy.
            var latest = await TryGetDocumentMarkdownAsync();
            if (latest is null)
            {
                MessageBox.Show(this, "Couldn't read the document from the editor, so the " +
                    "markdown view wasn't opened. Your work is unaffected.",
                    "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
                // The toggle button flipped itself on the click; put it back.
                SourceToggle.IsChecked = _sourceMode;
                MenuViewSource.IsChecked = _sourceMode;
                return;
            }
            // …and show the FILE's own spelling of it while the document is still
            // the one that was opened or saved (SourceText). The editor's
            // serialisation normalises — a setext heading comes back ATX, a
            // reference link inlined — so handing `latest` straight to the box put
            // a rewritten document in front of anyone who pressed Ctrl+E to see
            // their file, and a save from there wrote the rewrite. Once the
            // formatted view HAS changed the document, `latest` is the only copy of
            // that work and is what arrives.
            SourceBox.Text = SourceText.For(latest, _cleanMarkdown, _diskBaseline);
            Web.Visibility = Visibility.Collapsed;
            SourceBox.Visibility = Visibility.Visible;
            SourceBox.Focus();
        }
        else
        {
            // Leaving source: push edits back into the WYSIWYG editor, then check it
            // actually landed. A silently failed setMarkdown would leave the editor
            // showing the pre-edit document while the source box — the only copy of
            // the edits — is hidden and about to be treated as stale.
            //
            // Asked BEFORE the push, while _sourceMode still makes the box the
            // authoritative copy: did the user actually change anything in here?
            // It has to be read here and not after SetDocumentMarkdownAsync, which
            // overwrites SourceBox.Text with the editor's settled serialisation —
            // by then the question "did the user change anything" can no longer be
            // asked of the box.
            var wasUnmodified = IsUnmodifiedText(SourceBox.Text);
            // What it returns IS the editor's own answer, read straight after the set:
            // asking again here serialised the whole document a second time for the
            // same fact. (TryGetDocumentMarkdownAsync is not that answer either way —
            // it would hand back SourceBox.Text, since _sourceMode is still true until
            // below.) Null means the editor could not be asked, which is the same
            // "it didn't land" this has always refused to switch views on.
            var landed = await SetDocumentMarkdownAsync(SourceBox.Text);
            if (landed is null)
            {
                MessageBox.Show(this, "Couldn't hand your markdown back to the formatted " +
                    "view, so it's been left as it is. Your edits are still here.",
                    "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
                SourceToggle.IsChecked = _sourceMode;
                MenuViewSource.IsChecked = _sourceMode;
                return;
            }
            // Nothing was edited, so the document leaving this view is the same saved
            // state that entered it — now spelled the way the editor spells it, which
            // is the spelling the formatted view's dirty tracking compares against.
            // Adopting it keeps the one invariant this pair of views runs on: the
            // clean baseline is the AUTHORITATIVE surface's words for the last
            // opened/saved state. Without it a serialiser that does not come back to
            // the same text through a parse marks an untouched document modified on a
            // Ctrl+E round trip. _diskBaseline is deliberately NOT touched: the file
            // has not changed, and external-change detection is asked of that one.
            if (wasUnmodified) _cleanMarkdown = landed;
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
        // Same name the title bar shows. Dropped content has no path but does have a
        // name, and asking "Save changes to Untitled?" about a file the user can see
        // named in the title is its own small betrayal.
        var name = _currentPath is not null ? Path.GetFileName(_currentPath)
                 : _displayName ?? "Untitled";
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

    // Word-like: New always opens a fresh window with its own blank document rather
    // than replacing what's in this one. That's not just fidelity to the reference —
    // it removes the discard prompt entirely, since nothing about the current window
    // changes. --new forces the new instance to land on a blank document rather than
    // whatever its own startup settings would otherwise choose.
    private void New_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try { Process.Start(new ProcessStartInfo(exe, "--new") { UseShellExecute = false }); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open a new window:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
        {
            Filter = Secure.SecureUi.OpenFilter(_showEncryptedInOpen),
            DefaultExt = ".md",
            CheckFileExists = true,
            // RecentFolders only reaches the BUILT-IN picker's rail; the native
            // dialog needs this to start anywhere in particular.
            InitialDirectory = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : null,
            RecentFolders = PickerRecentFolders(),
        });
        if (picked is null) return;
        await OpenPathAsync(picked);
    }

    private async Task OpenPathAsync(string path, bool startup = false)
    {
        // Before a byte is read: is this file open in another window? Every open
        // route lands here (the chokepoint comment below lists them), so this is
        // the one place to ask. Reloads of the document this window already shows
        // call LoadDocumentAsync directly and never come through - correctly, since
        // the claim is already this window's. The claim is BEGUN, not taken: the
        // document this window still shows stays guarded through the read and the
        // password prompt below, and only a load that completes commits the switch.
        var verdict = BeginDocumentClaim(path);
        var heldElsewhere = false;
        if (verdict.Verdict == Instances.OpenVerdict.FocusOther)
        {
            var focused = Instances.OpenGuard.TryFocusWindow(verdict.HolderHwnd);
            switch (Instances.OpenGuardDecision.AfterFocus(focused, startup))
            {
                case Instances.OpenFallback.Yield:
                    FlashStatus($"{Path.GetFileName(path)} is already open in another window");
                    return;
                case Instances.OpenFallback.Exit:
                    // Launched only to open this file, and the window that has it is
                    // now in front: there is nothing for this process to show. Queued
                    // rather than Close() here: at startup this runs synchronously
                    // inside the editor's 'ready' message handler, and the WebView2
                    // must not be torn down from inside its own event. One hop lets
                    // that handler unwind; MainWindow_Closing then runs as usual.
                    _yieldingToOtherWindow = true;
                    _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
                    return;
                default:
                    heldElsewhere = true;   // nothing to focus: open here, read-only
                    break;
            }
        }
        var loaded = false;
        ShowBusy($"Opening {Path.GetFileName(path)}…");
        try
        {
            // Bytes first, then sniff: encryption is detected by CONTENT, not
            // extension, so a .mdenc renamed to .md still prompts instead of
            // filling the editor with ciphertext. This method is the single
            // chokepoint for the Open dialog, file association, command line,
            // Open Recent and multi-file drops - the password prompt therefore
            // covers every one of those entry points at once.
            var bytes = await File.ReadAllBytesAsync(path);
            if (Secure.SecureMarkdownFormat.LooksLikeContainer(bytes))
            {
                HideBusy();   // the prompt shouldn't sit under a busy overlay
                string? error = null;
                while (true)
                {
                    var pw = PasswordDialog.Enter(this, "Encrypted Document",
                        $"{Path.GetFileName(path)} is password-protected. Enter its password to open it.",
                        error);
                    if (pw is null) return;   // cancelled: nothing opened, nothing changed
                    try
                    {
                        // Task.Run: the KDF is deliberately ~half a second of work.
                        var text = await Task.Run(() => Secure.SecureMarkdownFormat.Decrypt(bytes, pw));
                        ShowBusy($"Opening {Path.GetFileName(path)}…");
                        // Detect on the plaintext: the line ending lives inside the
                        // container (Save puts it there); a byte-order mark does not.
                        await LoadDocumentAsync(DocumentText.Detect(text), path, pw);
                        loaded = true;
                        break;
                    }
                    catch (Secure.SecureMarkdownException ex)
                        when (ex.Error == Secure.SecureMarkdownError.WrongPasswordOrCorrupt)
                    {
                        error = "Incorrect password, or the file has been damaged.";
                    }
                    catch (Secure.SecureMarkdownException ex)
                    {
                        MessageBox.Show(this, ex.Message, "Markdown Midget",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }
            else
            {
                // The same BOM-detecting decode File.ReadAllTextAsync used, plus the
                // file's line ending and mark remembered so Save can put them back.
                await LoadDocumentAsync(DocumentText.Detect(bytes), path);
                loaded = true;
            }
            // The new document is on screen: now, and not before, the old claim goes.
            _openGuard.Commit(path);
            if (heldElsewhere) OpenedReadOnlyBecauseHeld(path);
            // Read-only is window state: what the fallback imposed for the LAST
            // document must not stick to this one, which opened normally.
            else if (_imposedReadOnly.Clear()) SetReadOnly(false);
            AddRecent(path);
            await FocusDocumentAsync();   // same reason as New: don't eat the first keystroke
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open the file:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            HideBusy();
            // A cancelled password prompt, an unreadable file, an editor that threw:
            // the window still shows what it showed before, and that document's
            // claim was never let go of. Only the one begun on the file that never
            // opened is released - by path, so that a second open started while
            // this one was in flight (Ctrl+O is not gated by the busy overlay)
            // keeps its own pending claim for its own commit.
            if (!loaded) _openGuard.Abandon(path);
        }
    }

    /// <summary>
    /// The read-only fallback: the file is open in another window that couldn't be
    /// brought forward (no usable handle, or Windows declined). Shown here without
    /// a claim - the other window has it, and the commit let this window's old one
    /// go - and read-only, so this window can't save over that one; said out loud,
    /// because a document that won't take keystrokes with no explanation reads as a
    /// broken app. The way out is Edit ▸ Read Only, which claims the file again
    /// before it lets anyone edit (ReadOnly_Click).
    /// </summary>
    private void OpenedReadOnlyBecauseHeld(string path)
    {
        _imposedReadOnly.Impose(readOnlyAlready: _readOnly);
        SetReadOnly(true);
        FlashStatus("Already open in another window; opened read-only here");
        HideBusy();   // the message shouldn't sit under a busy overlay
        MessageBox.Show(this,
            $"{Path.GetFileName(path)} is open in another Markdown Midget window, " +
            "which couldn't be brought to the front.\n\n" +
            "It has been opened read-only here. Once it is closed in the other " +
            "window, turn off Edit ▸ Read Only to edit it here.",
            "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Loads a document into the editor and resets the clean baseline + history.
    /// <paramref name="doc"/> is the text plus, from whichever file it came out of, the
    /// line ending and byte-order mark Save must reproduce (issue #3).
    /// <paramref name="password"/> is non-null exactly when the document came out of a
    /// .mdenc container; every other load clears the window's encryption state.</summary>
    private async Task LoadDocumentAsync(DocumentText.Decoded doc, string? path, string? password = null)
    {
        // try/finally, because the awaits below reach into the editor and throw
        // outright when the WebView2 has died. A _suppressDirty left stuck true stops
        // dirty tracking AND backups for the rest of the session, so the app would
        // close a modified document without asking and with no crash copy — the
        // failure this whole feature exists to prevent, caused by its own guard.
        _suppressDirty = true;
        try
        {
            // The document being replaced is gone: every caller has already asked,
            // and the user either saved it or said discard. Its crash copy has to go
            // with it, or a later crash would hand back the very content they told us
            // to throw away — and "the copy is deleted as soon as you save or close"
            // would be a promise the app didn't keep.
            DiscardBackup();
            _backupDirty = false;   // don't let a timer tick resurrect what we dropped
            // A new document invalidates any in-flight spell check — its results were
            // computed against the OLD document and must never decorate this one.
            _spellGeneration++;
            await ApplyDocBaseAsync(path);            // resolve relative images first
            await SetDocumentMarkdownAsync(doc.Text); // setMarkdown flushes undo history
            _currentPath = path;
            _lineEnding = doc.Ending;
            _hadBom = doc.HadBom;
            // Folded here rather than trusted: most callers hand over Detect's
            // output, which already is, but the Save-As-after-external-change path
            // hands over the editor's own text, and AvalonEdit's Enter can put CRLF
            // in that (DocumentText.DefaultLineEnding says why).
            _diskBaseline = DocumentText.Fold(doc.Text);
            _docEncrypted = password is not null;
            _docPassword = password;
            ClearBackupKey();   // the old document's cached backup key must not outlive it
            _displayName = null;
        }
        finally { _suppressDirty = false; }
        await SetCleanBaselineAsync();
        // The source view already showing? Then the install above put the editor's
        // settled serialisation in front of the user — the rewrite Ctrl+E no longer
        // shows (#2) — and no Ctrl+E is coming to replace it. Ctrl+E's rule is
        // applied here instead (SourceText.AfterLoad is SourceText.For), and here
        // rather than earlier: both baselines are this document's now, and the clean
        // one was taken from the editor's serialisation, exactly as for a load in the
        // formatted view, which the #4 external-change check relies on. Asked before
        // SetCleanBaselineAsync, the source view's clean baseline would have been
        // read off the box as the file's text. The write replaces the whole text, so
        // the box's undo history is cleared as the install's own write cleared it, and
        // IsUnmodifiedText accepts the file's spelling, so the title gains no `*`. For
        // a crash recovery the disk baseline here is still the snapshot the recovery
        // handed over (it points both baselines at the file afterwards), so the box
        // shows the recovered work, never the file.
        if (SourceText.AfterLoad(_sourceMode, SourceBox.Text, _cleanMarkdown, _diskBaseline) is { } ownSpelling)
            SourceBox.Text = ownSpelling;
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

    /// <summary>
    /// The editor navigates exactly once, to its own bundle. Anything else — script
    /// setting `location`, a form, a redirect off our hosts — would replace the
    /// editing surface with whatever loaded, so it is cancelled rather than blocked
    /// at the request layer, where the navigation commits anyway.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) { e.Cancel = true; return; }
        // Any non-http(s) scheme is refused outright, not just left uncancelled —
        // that used to include `javascript:`, which this handler let straight
        // through. It's very likely already stopped by the page's own CSP
        // (script-src 'self' treats a javascript: navigation as inline script,
        // and Chromium enforces that), but this handler's own doc comment claims
        // to be an INDEPENDENT guard alongside the resource filter, and resting
        // that claim on a single remaining layer — the CSP meta tag — is exactly
        // the single-point-of-failure this file exists to avoid. The app itself
        // only ever navigates to https://{VirtualHost}/..., so nothing legitimate
        // is lost by refusing every other scheme at the top level.
        if (uri.Scheme is not ("http" or "https")) { e.Cancel = true; return; }
        if (!string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase))
            e.Cancel = true;
    }

    /// <summary>
    /// Every resource request THIS WebView makes passes through here, and anything
    /// off-origin that is not a picture is refused.
    ///
    /// Both halves of that are narrower than they look. It is resources, not sockets
    /// — a WebSocket, a `preconnect`, and the TCP connect behind a cancelled
    /// navigation never reach this handler, and the CSP covers those. And it is THIS
    /// WebView: a new window would be a separate one with none of this attached,
    /// which is why NewWindowRequested is refused outright.
    ///
    /// This is the answer to a question the CSS theme validator kept getting wrong.
    /// That validator reads a stylesheet and decides whether it contains a network
    /// reference — and four review rounds each found another way to write one it
    /// could not see, because it pattern-matches text that the URL parser rewrites
    /// before it parses: leading control characters stripped, tabs deleted from the
    /// middle, backslashes read as slashes, `//` optional, identifiers spelled with
    /// escapes. Every round fixed the spellings that round had found.
    ///
    /// A filter here does not have to recognise how a URL was written. It sees the
    /// request the renderer actually made, after all of that, and asks a question with
    /// an easy answer.
    ///
    /// The question is NOT "is this host ours", because it cannot be: a markdown file
    /// referencing an image on githubusercontent renders that image everywhere else
    /// markdown is read, and an editor that quietly stopped would be broken rather
    /// than careful. So pictures and media are allowed off-origin and everything else
    /// is not — no off-origin script, stylesheet, font, fetch or XHR, whoever asks.
    ///
    /// That exception is also the limit of what this layer can do about themes. A CSS
    /// `url()` produces an Image request like any other, and nothing here can tell a
    /// document's picture from a stylesheet's. CssValidator WILL refuse those once
    /// themes are wired — it is called by nothing yet — and will still be best-effort
    /// at it. What changed is that its other job, no remote script and no remote
    /// stylesheet, is now enforced somewhere that cannot be out-spelled.
    ///
    /// Only http and https are judged. Anything else the renderer resolves without a
    /// request — `data:`, `blob:`, and WebView2's own internal schemes — never
    /// reaches the network and is left alone.
    /// </summary>
    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            // A URI that won't parse gets refused rather than waved through. This is a
            // chokepoint, and the only safe direction for "I don't understand this" is
            // the one that doesn't reach the network.
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri))
            {
                e.Response = Blocked("unparseable request URI");
                return;
            }

            // `data:`, `blob:` and WebView2's own internal schemes resolve without a
            // request, so there is nothing here to allow or refuse.
            if (uri.Scheme is not ("http" or "https")) return;

            // Our own bundle. In practice this never fires: a folder mapping is served
            // without raising WebResourceRequested at all, which is also why widening
            // the filter to `*` costs nothing. Kept as an explicit allow rather than
            // removed, in case that ever changes.
            if (string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase)) return;

            if (!string.Equals(uri.Host, DocHost, StringComparison.OrdinalIgnoreCase))
            {
                // Pictures are the exception, and not a grudging one: a markdown file
                // that references an image on githubusercontent renders that image
                // everywhere else markdown is read, and an editor that quietly stopped
                // would be broken rather than careful. Media goes with them.
                //
                // Which is the honest cost of this design: a CSS `url()` produces an
                // Image request too, so a theme CAN still fetch a picture, and nothing
                // at this layer can tell one initiator from the other. CssValidator is
                // what refuses those, best-effort — see the note at the top of it. The
                // narrower thing this buys is still worth having: no off-origin script,
                // stylesheet, font, fetch or XHR, whoever asks.
                if (e.ResourceContext is CoreWebView2WebResourceContext.Image
                                      or CoreWebView2WebResourceContext.Media) return;

                // Everything else is refused with a response rather than left to fail
                // on its own: a real failure would still have gone out to DNS first.
                e.Response = Blocked($"off-origin {e.ResourceContext} request");
                return;
            }

            ServeDocumentAsset(e);
        }
        catch
        {
            // Nothing may escape into a WebView2 event handler — an exception here
            // takes the whole app down, and during teardown reading e.Request can
            // throw. Fail closed.
            try { e.Response = Blocked("request could not be handled"); } catch { }
        }
    }

    // Held from initialisation rather than reached through `Web.CoreWebView2` each
    // time: during teardown that property is exactly what goes away, and it is also
    // what BUILDS the refusal — so a throw there sent the outer catch to retry the
    // same call, swallow it, and return with no response set, which hands the request
    // to WebView2's default handling. The fail-safe failed open.
    private CoreWebView2Environment? _webEnvironment;

    private CoreWebView2WebResourceResponse? Blocked(string why) =>
        _webEnvironment?.CreateWebResourceResponse(
            null, 403, "Blocked", $"Content-Type: text/plain\r\nX-MDM-Blocked: {why}");

    /// <summary>
    /// Serve files from the current document's folder, restricted to that folder's
    /// subtree — and ALWAYS answer, even when there is nothing to serve.
    ///
    /// Returning without a response used to look harmless: WebView2 would fall back
    /// to default handling and the image would simply fail. Except default handling
    /// means the real network stack, so `mdm-doc.invalid/beacon-123.png` left the
    /// handler and reached the OS resolver — with the attacker's path attached, which
    /// a system proxy or a resolver that hijacks NXDOMAIN will happily carry. The one
    /// host this trusts was the way out. A 404 here costs nothing and closes it.
    /// </summary>
    private void ServeDocumentAsset(CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var full = _docFolder is null ? null : DocAsset.ResolveWithinRoot(_docFolder, e.Request.Uri);
            if (full is null || !File.Exists(full))
            {
                e.Response = _webEnvironment?.CreateWebResourceResponse(
                    null, 404, "Not Found", "Content-Type: text/plain");
                return;
            }

            var ms = new MemoryStream(File.ReadAllBytes(full));
            var headers = $"Content-Type: {DocAsset.ContentTypeFor(Path.GetExtension(full))}\r\n" +
                          "Access-Control-Allow-Origin: *\r\nCache-Control: no-cache";
            e.Response = _webEnvironment?.CreateWebResourceResponse(ms, 200, "OK", headers);
        }
        catch
        {
            // Unreadable, locked, too large — still answered here rather than handed
            // to the network stack.
            try
            {
                e.Response = _webEnvironment?.CreateWebResourceResponse(
                    null, 404, "Not Found", "Content-Type: text/plain");
            }
            catch { }
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(false);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(true);

    private async Task<bool> SaveAsync(bool forcePrompt)
    {
        // Plain Save is disabled in read-only mode (it would overwrite the same file);
        // Save As (forcePrompt) still works so the content can be kept elsewhere.
        if (_readOnly && !forcePrompt) return false;

        var path = _currentPath;
        var wantEncrypted = _docEncrypted;
        string? newPassword = null;   // set when THIS save establishes encryption
        if (forcePrompt || path is null)
        {
            var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
            {
                Save = true,
                // Secure Markdown in the type dropdown is the design's second path
                // to encryption - equivalent to File > Encrypt Document.
                Filter = Secure.SecureUi.SaveFilter,
                DefaultExt = _docEncrypted ? Secure.SecureMarkdownFormat.Extension : ".md",
                FilterIndex = _docEncrypted ? Secure.SecureUi.SaveFilterEncryptedIndex : 1,
                // Dropped content already has a sensible name; offer it rather than
                // making the user retype it. Through GetFileName even so — it reaches
                // us from the browser and, after a recovery, from a file on disk.
                FileName = _currentPath is not null ? Path.GetFileName(_currentPath)
                         : (_displayName is null ? "Untitled.md" : Path.GetFileName(_displayName)),
                InitialDirectory = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : null,
                RecentFolders = PickerRecentFolders(),
            });
            if (picked is null) return false;
            path = picked;
            wantEncrypted = Secure.SecureUi.IsEncryptedPath(path);
            if (wantEncrypted && !_docEncrypted)
            {
                newPassword = PasswordDialog.Set(this, "Encrypt Document",
                    $"Choose a password for {Path.GetFileName(path)}.");
                if (newPassword is null) return false;
            }
            else if (!wantEncrypted && _docEncrypted)
            {
                // Design section 8: the user must not stumble into a readable copy.
                if (MessageBox.Show(this,
                        "This writes a readable copy with no password. Anyone with the " +
                        "file can read it. The encrypted file stays where it is.\n\nContinue?",
                        "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return false;
            }
        }

        // TryGet, not Get. Get reports "the editor didn't answer" as an empty
        // document, and this method truncates the file with FileMode.Create and then
        // deletes the crash copy — so one unanswered call would replace the user's
        // document with nothing AND destroy the backup holding the real content, with
        // no error and a title bar that says saved.
        var markdown = await TryGetDocumentMarkdownAsync();
        if (markdown is null)
        {
            MessageBox.Show(this,
                "Couldn't read the document from the editor, so nothing was saved.\n\n" +
                "Your work is untouched — the file on disk is unchanged and the " +
                "unsaved-changes copy has been kept. Try again, or restart the app.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _suppressWatcher = true;
        try
        {
            if (wantEncrypted)
            {
                var pw = newPassword ?? _docPassword
                    ?? throw new InvalidOperationException("encrypted save without a password");
                // The file's line ending is applied to the plaintext the container
                // seals; its byte-order mark is not - inside ciphertext a mark would
                // mark nothing. Transactional with read-back decrypt-verify; the KDF
                // makes this ~half a second of real work, so off the UI thread.
                var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);
                await Task.Run(() => Secure.SecureMarkdownFile.Save(path, plaintext, pw));
            }
            else
            {
                // Flushed to the device, not just handed to the cache: the crash copy is
                // deleted a few lines below on the strength of this write having happened.
                // Losing power inside the write-back window would otherwise leave the
                // classic zero-length file AND no backup — the exact case this feature is
                // for. A few milliseconds per save is a fair price.
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write,
                    FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
                // Bytes, not a StreamWriter: the file's own line ending and mark go
                // back on here (DocumentText.Encode). A StreamWriter wrote the endings
                // as the serialiser left them - mixed, for a CRLF file with a code
                // block - and never a mark.
                await file.WriteAsync(DocumentText.Encode(markdown, _lineEnding, _hadBom));
                file.Flush(flushToDisk: true);
            }
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
        if (pathChanged) RekeyDocumentClaim(path);   // the document lives at the new path now
        // Format transition bookkeeping. Saving an encrypted doc as plaintext makes
        // THIS WINDOW a plaintext editor of the new file (the .mdenc on disk keeps
        // its own life); saving plaintext as .mdenc adopts the just-set password.
        _docEncrypted = wantEncrypted;
        if (newPassword is not null) { _docPassword = newPassword; ClearBackupKey(); }
        else if (!wantEncrypted && _docPassword is not null) { _docPassword = null; ClearBackupKey(); }
        _cleanMarkdown = markdown; // new clean baseline; undo history is left intact
        _diskBaseline = DocumentText.Fold(markdown);   // what the file holds now, as Detect reads it back
        _dirty = false;
        UpdateTitle();
        if (pathChanged) StartWatching(path);
        SetClosed(false);
        AddRecent(path);
        DiscardBackup();   // it's on disk now; the crash copy has nothing left to protect
        return true;
    }

    // ===== Secure Markdown operations =====

    private void ClearBackupKey()
    {
        if (_backupKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(_backupKey);
        _backupKey = null;
        _backupSalt = null;
    }

    private async void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || _isHelpWindow || _closed || _docEncrypted) return;
        var markdown = await TryGetDocumentMarkdownAsync();
        if (markdown is null) { ReportEditorUnavailable(); return; }

        // Where the encrypted file goes: beside the original for a saved document,
        // or wherever the user picks for an untitled one.
        string target;
        var original = _currentPath;
        if (original is not null)
        {
            target = Secure.SecureUi.EncryptedPathFor(original);
            if (File.Exists(target) && MessageBox.Show(this,
                    $"{Path.GetFileName(target)} already exists. Replace it?",
                    "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
        }
        else
        {
            var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
            {
                Save = true,
                Filter = "Secure Markdown (*.mdenc)|*.mdenc",
                DefaultExt = Secure.SecureMarkdownFormat.Extension,
                InitialDirectory = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : null,
                FileName = _displayName is null ? "Untitled.mdenc"
                         : Path.ChangeExtension(Path.GetFileName(_displayName), Secure.SecureMarkdownFormat.Extension),
                RecentFolders = PickerRecentFolders(),
            });
            if (picked is null) return;
            target = picked;
        }

        var pw = PasswordDialog.Set(this, "Encrypt Document",
            $"Choose a password for {Path.GetFileName(target)}.",
            original is not null ? "The unencrypted copy will be removed from disk." : null);
        if (pw is null) return;

        StopWatching();
        _suppressWatcher = true;
        try
        {
            // Line ending applied, no mark - as SaveAsync's encrypted branch.
            var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);
            await Task.Run(() => Secure.SecureMarkdownFile.Save(target, plaintext, pw));
        }
        catch (Exception ex)
        {
            _suppressWatcher = false;
            if (original is not null) StartWatching(original);
            MessageBox.Show(this, $"Couldn't encrypt the document:\n{ex.Message}",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // The encrypted file is written AND verified — only now may the plaintext
        // original go. Best-effort scrub first: helps on spinning disks, does
        // little on SSDs, and the honest promise is only "no ACCESSIBLE copy".
        if (original is not null && !string.Equals(original, target, StringComparison.OrdinalIgnoreCase))
            RemovePlaintextOriginal(original);

        _currentPath = target;
        _docEncrypted = true;
        _docPassword = pw;
        ClearBackupKey();
        _cleanMarkdown = markdown;
        _diskBaseline = DocumentText.Fold(markdown);
        _dirty = false;
        UpdateTitle();
        StartWatching(target);
        RekeyDocumentClaim(target);   // the .md claim goes with the .md
        SetClosed(false);
        AddRecent(target);
        DiscardBackup();   // takes the pre-encryption plaintext snapshot with it
        _ = Dispatcher.BeginInvoke(new Action(() => _suppressWatcher = false), DispatcherPriority.Background);
        FlashStatus("Encrypted. There is no password recovery — keep it safe");
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (!_docEncrypted || _docPassword is null || _currentPath is null || _readOnly) return;
        var markdown = await TryGetDocumentMarkdownAsync();
        if (markdown is null) { ReportEditorUnavailable(); return; }
        var pw = PasswordDialog.Set(this, "Change Password",
            $"Choose a new password for {Path.GetFileName(_currentPath)}. " +
            "The document is saved immediately with the new password.");
        if (pw is null) return;
        _suppressWatcher = true;
        try
        {
            var plaintext = DocumentText.ApplyLineEnding(markdown, _lineEnding);   // as SaveAsync
            await Task.Run(() => Secure.SecureMarkdownFile.Save(_currentPath, plaintext, pw));
        }
        catch (Exception ex)
        {
            _suppressWatcher = false;
            MessageBox.Show(this, $"Couldn't change the password:\n{ex.Message}\n\nThe old password still applies.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _docPassword = pw;
        ClearBackupKey();
        _cleanMarkdown = markdown;
        _diskBaseline = DocumentText.Fold(markdown);
        _dirty = false;
        UpdateTitle();
        DiscardBackup();
        _ = Dispatcher.BeginInvoke(new Action(() => _suppressWatcher = false), DispatcherPriority.Background);
        FlashStatus("Password changed and document saved");
    }

    private async void ConvertPlain_Click(object sender, RoutedEventArgs e)
    {
        if (!_docEncrypted || _currentPath is null || _readOnly) return;
        if (MessageBox.Show(this,
                "This writes a readable copy with no password. Anyone with the file can " +
                "read it. The encrypted file is removed.\n\nContinue?",
                "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;
        var markdown = await TryGetDocumentMarkdownAsync();
        if (markdown is null) { ReportEditorUnavailable(); return; }

        var encryptedOriginal = _currentPath;
        var target = Secure.SecureUi.PlaintextPathFor(encryptedOriginal);
        if (File.Exists(target) && MessageBox.Show(this,
                $"{Path.GetFileName(target)} already exists. Replace it?",
                "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        StopWatching();
        _suppressWatcher = true;
        try
        {
            await using var file = new FileStream(target, FileMode.Create, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
            // The document's line ending, as SaveAsync; the mark too, if the window
            // still remembers one from before the document was encrypted.
            await file.WriteAsync(DocumentText.Encode(markdown, _lineEnding, _hadBom));
            file.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            _suppressWatcher = false;
            StartWatching(encryptedOriginal);
            MessageBox.Show(this, $"Couldn't convert the document:\n{ex.Message}",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Plaintext written and flushed; the ciphertext original can go (no scrub
        // needed — it never held readable content).
        try { File.Delete(encryptedOriginal); }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"The readable copy was written, but the encrypted file couldn't be removed:\n" +
                $"{encryptedOriginal}\n{ex.Message}",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _currentPath = target;
        _docEncrypted = false;
        _docPassword = null;
        ClearBackupKey();
        _cleanMarkdown = markdown;
        _diskBaseline = DocumentText.Fold(markdown);
        _dirty = false;
        UpdateTitle();
        StartWatching(target);
        RekeyDocumentClaim(target);   // the .mdenc claim goes with the .mdenc
        AddRecent(target);
        DiscardBackup();
        _ = Dispatcher.BeginInvoke(new Action(() => _suppressWatcher = false), DispatcherPriority.Background);
        FlashStatus("Converted to a regular markdown file");
    }

    /// <summary>
    /// Retire the plaintext original after File > Encrypt. One pass of random
    /// bytes before the delete: genuinely useful on spinning disks, mostly
    /// theatre on SSDs with wear-levelling — which is why the UI promises only
    /// that no ACCESSIBLE copy remains (design section 1). Failures degrade to a
    /// plain delete, and a failed delete is reported rather than swallowed: a
    /// readable copy silently left behind is the one outcome that must not be
    /// silent.
    /// </summary>
    private void RemovePlaintextOriginal(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var noise = System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                    (int)Math.Min(length, 4 * 1024 * 1024));
                long written = 0;
                while (written < length)
                {
                    var chunk = (int)Math.Min(noise.Length, length - written);
                    fs.Write(noise, 0, chunk);
                    written += chunk;
                }
                fs.Flush(flushToDisk: true);
            }
        }
        catch { /* scrub is best-effort; the delete below is the real promise */ }
        try { File.Delete(path); }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"The document was encrypted, but the readable original couldn't be removed:\n" +
                $"{path}\n{ex.Message}\n\nDelete it by hand when whatever holds it lets go.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReportEditorUnavailable() => MessageBox.Show(this,
        "Couldn't read the document from the editor, so nothing was changed. " +
        "Try again, or restart the app.",
        "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// What the window shows when the editor comes up, and only then any crash
    /// recovery. These have to be sequential: recovery decides whether it may use
    /// this window by looking at what's in it, and firing both off concurrently
    /// would have it read a half-applied landing state.
    /// </summary>
    private async Task ApplyLandingStateAsync()
    {
        // First, not last. Everything below awaits into the editor, and a script call
        // that throws would fault this task silently (it's fire-and-forget), leaving a
        // --readonly or Help window fully editable. Applying it up front also means
        // the loads below already see the right value.
        if (_startReadOnly) SetReadOnly(true);
        try
        {
            if (_pendingOpenPath is { } path)
            {
                _pendingOpenPath = null;
                await OpenThenApplyStartupViewAsync(path);
            }
            else if (_forceBlankDocument || _startWithBlankDocument)
            {
                // Either File ▸ New's --new (always blank, whatever the setting says —
                // that's the whole point of New) or the setting itself: land on an
                // empty document with the caret in it, so a session can start by
                // simply typing.
                await StartBlankDocumentAsync();
                // --source must hold for a pathless launch too — an untitled window
                // relaunched by Apply-update carries the flag with no file argument,
                // and dropping it here silently broke the "same view mode" promise.
                if (_startInSource && !_closed)
                {
                    _startInSource = false;
                    await SetSourceModeAsync(true);
                }
            }
            else
            {
                // Default landing state is the "no document open" splash, so a
                // brand-new session is purely a drop target / Open / New prompt.
                await SetCleanBaselineAsync();
                SetClosed(true);
            }
        }
        catch { /* a landing state that failed must not also cost them recovery */ }
        if (_yieldingToOtherWindow) return;   // closing; the next launch will recover
        await RecoverAsync();   // swallows its own failures
    }

    // ===== Already-open guard (issue #1) =====

    // This window's claim on the document it shows, held for as long as it shows
    // it, so a second window opening the same file finds this one instead of making
    // a copy that would save over it. One per window; every window is a process.
    private readonly Instances.OpenGuard _openGuard = new(Instances.OpenGuard.DefaultDirectory);

    // Read-only imposed by the fallback (OpenedReadOnlyBecauseHeld), as distinct
    // from read-only the user chose: lifting it is a claim attempt, and the next
    // normal open clears it.
    private readonly Instances.ImposedReadOnly _imposedReadOnly = new();

    // Set when this window was launched only to open a file that another window
    // already has, and that window has been brought forward. The close is queued
    // (see OpenPathAsync), and the rest of the landing state - the source view,
    // crash recovery - must not start in a window that is on its way out: recovery
    // adopting a snapshot here would make the queued close prompt about unsaved work.
    private bool _yieldingToOtherWindow;

    private long GuardHandle() => new WindowInteropHelper(this).Handle.ToInt64();

    /// <summary>
    /// Claim <paramref name="path"/> for this window at once (the read-only lift;
    /// see OpenGuard.Acquire). The handle recorded with the claim is what another
    /// window will bring to the front; it is 0 only before OnSourceInitialized, and
    /// a holder recorded as 0 reads to that other window as no live holder at all
    /// (OpenGuard.HolderIsLive), so it opens the file as if the lock were stale.
    /// The same check keeps a backup or AV tool holding a stale lock from sending
    /// the user to a dead session's window handle.
    /// </summary>
    private Instances.OpenGuardDecision ClaimDocument(string path) =>
        Instances.OpenGuardDecision.Decide(
            _openGuard.Acquire(path, Environment.ProcessId, GuardHandle()),
            Environment.ProcessId, Instances.OpenGuard.HolderIsLive);

    /// <summary>
    /// OpenPathAsync's claim: begun on a second handle, so the document this window
    /// still shows stays guarded until the new one has loaded (OpenGuard.Commit) or
    /// hasn't (OpenGuard.Abandon). Same decision as ClaimDocument.
    /// </summary>
    private Instances.OpenGuardDecision BeginDocumentClaim(string path) =>
        Instances.OpenGuardDecision.Decide(
            _openGuard.Begin(path, Environment.ProcessId, GuardHandle()),
            Environment.ProcessId, Instances.OpenGuard.HolderIsLive);

    /// <summary>
    /// The document moved to <paramref name="path"/> (Save As, Encrypt, Convert to
    /// Unencrypted, a recovered snapshot's file): the claim follows it. Best effort
    /// only: if another window holds the new path this window ends up unguarded
    /// there, which is honest - the write has happened - and is said in the status
    /// bar.
    /// </summary>
    private void RekeyDocumentClaim(string path)
    {
        var probe = _openGuard.Rekey(path, Environment.ProcessId, GuardHandle());
        if (probe.State == Instances.OpenGuard.ProbeState.Held && probe.HolderPid != Environment.ProcessId)
            FlashStatus($"{Path.GetFileName(path)} is also open in another window");
    }

    private void ReleaseDocumentClaim() => _openGuard.Release();

    /// <summary>
    /// Run a relaunch that reopens this document (Apply update, the "move" install,
    /// the About dialog's update). The new process starts while this one is still
    /// alive, so it would find this window holding the file, focus it, and quit -
    /// and then this window would shut down, leaving nothing open. Let go first;
    /// if the start fails and this window is staying, take the claim back.
    /// </summary>
    private void StartHandingOffDocument(Action start)
    {
        ReleaseDocumentClaim();
        try { start(); }
        catch
        {
            if (_currentPath is not null) RekeyDocumentClaim(_currentPath);
            throw;
        }
    }

    // ===== Crash recovery (periodic backup of unsaved work) =====

    private Backup.BackupStore? _backup;
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _backupDirty;      // content changed since the last snapshot

    /// <summary>
    /// Claim a backup session for this window. Read-only and help windows are
    /// skipped: they can't have unsaved work, and a lock file each would be noise
    /// that later launches have to probe.
    /// </summary>
    private void StartBackup()
    {
        // _startReadOnly, not _readOnly: this runs from the constructor, before the
        // editor is up and SetReadOnly has applied it.
        if (_startReadOnly || _isHelpWindow || !_backupEnabled) return;
        _backup = new Backup.BackupStore(Backup.BackupStore.DefaultDirectory, Guid.NewGuid().ToString("N"));
        if (!_backup.Start()) { _backup = null; return; }   // couldn't lock; don't pretend
        _backupTimer.Start();   // Tick is wired once in the constructor, not here:
                                // toggling the setting would otherwise stack handlers
    }

    /// <summary>
    /// Snapshot the document if it has changed since last time. Reads the editor
    /// directly rather than trusting a cached copy — the whole point is to be right
    /// about what the user would lose.
    /// </summary>
    private async Task WriteBackupAsync()
    {
        // _suppressDirty means a document swap is mid-flight: the editor still holds
        // the OLD content while the fields have moved on, so a tick landing here
        // would snapshot the wrong document — and recreate a copy just discarded.
        if (_backup is null || !_backupDirty || _closed || _suppressDirty) return;
        _backupDirty = false;
        try
        {
            // TryGet, not Get: a failed editor call comes back as "" from the latter,
            // and this method's two decisions are both destructive. Empty would either
            // delete the snapshot (when there's nothing on disk to compare against) or
            // overwrite it with nothing — and a zero-byte snapshot handed back after a
            // crash is a blank document the user is invited to save over their file.
            var markdown = await TryGetDocumentMarkdownAsync();
            if (markdown is null) { _backupDirty = true; return; }   // keep the last good one
            // Only unsaved content is worth keeping. If it matches what's on disk,
            // the file itself is the backup. Asked of the VISIBLE surface's own
            // baseline: in the source view the box legitimately holds the file's
            // spelling, and comparing that against the editor's serialisation alone
            // would keep a snapshot of a document with nothing unsaved in it — which
            // the next launch offers back as "recovered unsaved changes".
            if (IsUnmodifiedText(markdown)) { DiscardBackup(); return; }
            if (_docEncrypted && _docPassword is { } pw)
            {
                // The snapshot of an encrypted document is itself encrypted
                // (design 7a) — this tick must never be the plaintext leak of
                // exactly the content the user chose to protect. Session key:
                // the KDF runs once per (window, password), snapshots are then
                // AES-only.
                if (_backupKey is null || _backupSalt is null)
                {
                    var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
                    var key = await Task.Run(() => Secure.SecureMarkdownFormat.DeriveSessionKey(
                        pw, salt, Secure.SecureMarkdownFormat.KdfProfile.Default));
                    // The document may have changed during the derivation; the
                    // cached key is still right (it's about the password, not the
                    // content) but re-check the password didn't move underneath.
                    if (!ReferenceEquals(_docPassword, pw)) { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); _backupDirty = true; return; }
                    _backupKey = key;
                    _backupSalt = salt;
                }
                var container = Secure.SecureMarkdownFormat.EncryptWithKey(
                    markdown, _backupKey, _backupSalt, Secure.SecureMarkdownFormat.KdfProfile.Default);
                _backup.SaveEncrypted(container, _currentPath, _displayName);
            }
            else
            {
                _backup.Save(markdown, _currentPath, _displayName);
            }
        }
        catch { _backupDirty = true; }   // try again on the next tick
    }

    private void DiscardBackup() => _backup?.Discard();

    /// <summary>
    /// Hand back whatever a crashed session left. Runs once the editor is ready, so
    /// recovered content can go straight into it.
    /// </summary>
    private async Task RecoverAsync()
    {
        if (_backup is null || _readOnly || _isHelpWindow) return;
        try
        {
            // Launched to recover one specific snapshot: take exactly that one and
            // don't scan, or this window would race the parent for the others.
            if (_recoverSessionId is { } wanted)
            {
                // Claim before recovering, exactly like the scan path: an encrypted
                // orphan's password prompt can sit open indefinitely, and without
                // the claim a later launch's scan would recover the same snapshot a
                // second time. The parent that spawned us may still hold the claim
                // for a moment - wait it out briefly rather than giving up.
                IDisposable? idClaim = null;
                for (var i = 0; i < 20 && (idClaim = _backup.BeginRecovery()) is null; i++)
                    await Task.Delay(250);
                if (idClaim is null) return;   // orphan stays on disk for a later launch
                using var _ = idClaim;
                if (_backup.FindOrphan(wanted) is { } target)
                    await LoadRecoveredAsync(target.Meta, target.Markdown);
                else if (_backup.FindEncryptedOrphan(wanted) is { } encTarget)
                {
                    // The child counts the attempt, not the spawning parent: the
                    // prompt is about to display, which is the event worth counting.
                    _backup.RecordAttempt(encTarget.Meta);
                    await RecoverEncryptedAsync(encTarget.Meta, encTarget.Container);
                }
                return;
            }

            // One instance recovers at a time; whoever holds this is doing the work.
            using var claim = _backup.BeginRecovery();
            if (claim is null) return;

            var orphans = _backup.FindOrphans();
            // NO early return on an empty plaintext list: encrypted orphans are
            // enumerated separately below, and a crashed session holding ONLY an
            // encrypted document - the headline scenario - produces exactly zero
            // plaintext orphans. Returning here starved that pass entirely
            // (taxonomy 14: the early return that starves a downstream sweep).

            // "Free" means this window holds nothing the user would miss — the splash,
            // or the empty document the blank-doc setting lands on. Recovery must
            // never displace something they opened or typed. It also must never
            // displace what File ▸ New explicitly asked for: --new's whole point is a
            // guaranteed-blank document, stated three times in this release's own
            // docs, and a blank document is otherwise indistinguishable from "free" —
            // an orphaned crash snapshot claiming this window would silently swap it
            // out from under a deliberate, freshly-clicked New. The orphan isn't lost;
            // it just waits for a launch that didn't ask to be guaranteed blank.
            var free = !_forceBlankDocument
                && _currentPath is null && !_dirty && string.IsNullOrEmpty(_cleanMarkdown);
            var hostFreeForEncrypted = free;
            if (orphans.Count > 0)
            {
            var plan = Backup.RecoveryPlan.Decide([.. orphans.Select(o => o.Meta)], free);
            var byId = orphans.ToDictionary(o => o.Meta.SessionId, o => o.Markdown);

            var opened = 0;
            foreach (var other in plan.Elsewhere)
            {
                // Count the attempt BEFORE handing it over: if opening it kills the
                // app, the incremented count is what stops the next launch repeating.
                // The window we hand it to does NOT count it again. But only count a
                // hand-off that actually happened — charging an attempt for a window
                // that never opened would retire the document after three launches
                // without it ever having been seen.
                if (!OpenRecovered(other)) continue;
                _backup.RecordAttempt(other);
                opened++;
            }

            if (plan.Here is { } mine && byId.TryGetValue(mine.SessionId, out var markdown))
            {
                _backup.RecordAttempt(mine);
                await LoadRecoveredAsync(mine, markdown);
            }
            else if (opened > 0)
            {
                FlashStatus($"Restoring {opened} unsaved document(s) from a previous session");
            }
            // Knowable only here, USED only below the plan block - assigning it
            // any later is a dead write, and the encrypted pass would host into a
            // window the plaintext plan already filled (round 2 finding N2).
            hostFreeForEncrypted = plan.Here is null && free;

            // Last, and not as a flash. These are documents we are giving up on, so
            // the one message the user must not miss is the one that would otherwise
            // be overwritten four seconds later by the recovery notice above — or by
            // the next given-up document in the list. Windows that failed to open are
            // in the same message: uncounted and still on disk, so they WILL be tried
            // again — but the user should hear it now, not discover it never happened.
            ReportAbandoned(plan.GivenUp, plan.Elsewhere.Count - opened);
            foreach (var abandoned in plan.GivenUp) _backup.MarkGiveUpReported(abandoned);
            }

            // Encrypted orphans - OUTSIDE the plaintext-plan block, because a
            // crashed session holding only an encrypted document produces ZERO
            // plaintext orphans, and this pass must still run (review rounds 1 AND
            // 2 both caught this exact starvation; taxonomy 14). The first orphan
            // is prompted for HERE only when the plaintext plan left the window
            // free; the rest go to their own windows via the same --recover spawn
            // the plaintext flow uses. Attempts for spawned orphans are counted by
            // the CHILD when its prompt actually shows - charging them at spawn
            // would tick the counter for prompts never displayed. Give-up
            // bookkeeping stays with the plaintext plan: an encrypted snapshot the
            // user keeps cancelling simply keeps waiting (the prompt Discard
            // button is the exit), which for content they password-protected
            // beats quietly retiring it.
            var encOrphans = _backup.FindEncryptedOrphans();
            if (encOrphans.Count > 0)
            {
                var hostHere = hostFreeForEncrypted;
                foreach (var (encMeta, container) in encOrphans)
                {
                    if (hostHere)
                    {
                        hostHere = false;
                        _backup.RecordAttempt(encMeta);   // about to display right now
                        await RecoverEncryptedAsync(encMeta, container);
                    }
                    else
                    {
                        OpenRecovered(encMeta);   // child claims + counts when it prompts
                    }
                }
            }
        }
        catch { /* recovery is a bonus; never let it stop the app starting */ }
    }

    /// <summary>
    /// Put recovered content in this window, dirty, still pointing at its original
    /// file. Deliberately NOT written back to disk — the user decides whether this
    /// version is the one they want.
    /// </summary>
    private async Task LoadRecoveredAsync(Backup.BackupSnapshot mine, string markdown)
    {
        // The snapshot is the editor's text; the FILE is where the line ending and
        // byte-order mark come from, so it is read first and the recovered text is
        // loaded under its conventions. No file (untitled, or since deleted) means a
        // new document's defaults.
        var disk = mine.Path is not null && File.Exists(mine.Path)
            ? await ReadFileOrEmptyAsync(mine.Path)
            : DocumentText.NewDocument;
        await LoadDocumentAsync(disk with { Text = markdown }, mine.Path);
        // The snapshot's file is open in this window now, so it is claimed like any
        // other document: a double-click on it must find this window.
        if (mine.Path is not null) RekeyDocumentClaim(mine.Path);
        // Everything above treats a freshly loaded document as clean. This one isn't:
        // it's unsaved work that never reached the file, so both baselines are the
        // file, not the snapshot.
        _cleanMarkdown = disk.Text;
        _diskBaseline = disk.Text;
        _displayName = mine.Path is null ? mine.DisplayName : null;
        _dirty = !string.Equals(markdown, _cleanMarkdown, StringComparison.Ordinal);
        UpdateTitle();
        // Take ownership so this window's timer keeps protecting it from here on. If
        // that write fails the orphan is deliberately left alone, but nothing would
        // retry — _backupDirty is false after a load — so arm the next tick.
        if (_backup?.Adopt(mine, markdown) == false) _backupDirty = true;
        FlashStatus($"Recovered unsaved changes to {mine.Describe()} — not yet saved");
        await FocusDocumentAsync();
    }

    private static async Task<DocumentText.Decoded> ReadFileOrEmptyAsync(string path)
    {
        try { return DocumentText.Detect(await File.ReadAllBytesAsync(path)); }
        catch { return DocumentText.NewDocument; }   // empty baseline: everything reads as unsaved
    }

    /// <summary>
    /// The encrypted flavour of LoadRecoveredAsync: prompt for the password
    /// (retry on a miss, Cancel keeps the snapshot for a later launch), then
    /// hand the window an ENCRYPTED document whose unsaved changes these are.
    /// </summary>
    private async Task RecoverEncryptedAsync(Backup.BackupSnapshot meta, byte[] container)
    {
        string? error = null;
        while (true)
        {
            var (pw, discard) = PasswordDialog.EnterForRecovery(this, "Recover Encrypted Document",
                $"A previous session left unsaved changes to the encrypted document " +
                $"{meta.Describe()}. Enter its password to recover them. " +
                "Cancel keeps the copy for a later launch.", error);
            if (discard)
            {
                // The exit for a password that is genuinely lost - without it the
                // only escape from this prompt-on-every-launch is deleting files
                // under %LocalAppData% by hand. Destruction stays a two-step,
                // spelled-out choice.
                if (MessageBox.Show(this,
                        $"Permanently delete the unsaved encrypted copy of {meta.Describe()}? " +
                        "Without its password it can never be read, and this cannot be undone.",
                        "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    == MessageBoxResult.Yes)
                {
                    _backup?.Purge(meta.SessionId);
                    return;
                }
                error = null;   // stale wrong-password text does not apply to a fresh prompt
                continue;
            }
            if (pw is null) return;   // snapshot stays on disk, untouched
            string text;
            try
            {
                text = await Task.Run(() => Secure.SecureMarkdownFormat.Decrypt(container, pw));
            }
            catch (Secure.SecureMarkdownException ex)
                when (ex.Error == Secure.SecureMarkdownError.WrongPasswordOrCorrupt)
            {
                error = "Incorrect password, or the copy is damaged.";
                continue;
            }
            catch (Secure.SecureMarkdownException ex)
            {
                MessageBox.Show(this, ex.Message, "Markdown Midget",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // The clean baseline is what the FILE decrypts to — recovered content
            // is unsaved work on top of it. A file that is missing or no longer
            // opens with this password baselines to empty, so everything reads as
            // unsaved, which errs toward protecting it. Read before the load, as in
            // LoadRecoveredAsync: the file's line ending is the one the recovered
            // text is loaded under.
            var disk = await ReadEncryptedCleanAsync(meta.Path, pw);
            await LoadDocumentAsync(disk with { Text = text }, meta.Path, pw);
            if (meta.Path is not null) RekeyDocumentClaim(meta.Path);   // same as LoadRecoveredAsync
            _cleanMarkdown = disk.Text;
            _diskBaseline = disk.Text;
            _displayName = meta.Path is null ? meta.DisplayName : null;
            _dirty = !string.Equals(text, _cleanMarkdown, StringComparison.Ordinal);
            UpdateTitle();
            if (_backup?.AdoptEncrypted(meta, container) == false) _backupDirty = true;
            FlashStatus($"Recovered unsaved changes to {meta.Describe()} — not yet saved");
            await FocusDocumentAsync();
            return;
        }
    }

    private static async Task<DocumentText.Decoded> ReadEncryptedCleanAsync(string? path, string password)
    {
        if (path is null || !File.Exists(path)) return DocumentText.NewDocument;
        try { return DocumentText.Detect(Secure.SecureMarkdownFormat.Decrypt(await File.ReadAllBytesAsync(path), password)); }
        catch { return DocumentText.NewDocument; }
    }

    /// <summary>
    /// Hand a snapshot to a new window, which takes it by name. False means the
    /// window never started, so the caller must not charge it an attempt.
    /// </summary>
    private bool OpenRecovered(Backup.BackupSnapshot snapshot)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        // The id comes from a filename in a folder we don't exclusively own, and it
        // is about to become a child process's arguments. Require the shape we
        // actually write, and pass it as an argument rather than splicing it into a
        // command line, so a crafted name can't turn into extra switches.
        if (!Backup.BackupStore.IsSessionId(snapshot.SessionId)) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--recover");
            psi.ArgumentList.Add(snapshot.SessionId);
            Process.Start(psi);
            return true;
        }
        catch { return false; }   // it stays on disk, uncounted, for the next launch
    }

    /// <summary>
    /// Tell the user about work we have stopped trying to restore. Deliberately a
    /// dialog rather than a status flash: this is the last time the app will mention
    /// unsaved work that still exists on disk, and a message that disappears after
    /// four seconds is the same as no message.
    /// </summary>
    private void ReportAbandoned(IReadOnlyList<Backup.BackupSnapshot> abandoned, int failedToOpen)
    {
        if (abandoned.Count == 0 && failedToOpen <= 0) return;
        var body = new System.Text.StringBuilder();
        if (abandoned.Count > 0)
        {
            body.Append("Markdown Midget couldn't restore this unsaved work after several attempts:\n\n  ");
            body.Append(string.Join("\n  ", abandoned.Select(a => a.Describe())));
            body.Append("\n\n");
        }
        if (failedToOpen > 0)
            body.Append(abandoned.Count > 0
                ? $"It also couldn't open a window for {failedToOpen} more recovered document(s); "
                : $"Markdown Midget couldn't open a window for {failedToOpen} recovered document(s); ")
                .Append("they'll be offered again next time.\n\n");
        body.Append("The files are still here, as plain markdown you can open in any editor:\n");
        body.Append(Backup.BackupStore.DefaultDirectory);
        MessageBox.Show(this, body.ToString(),
            "Unsaved work couldn't be restored", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _suppressDirty = true;   // see LoadDocumentAsync: must not stay true on a throw
        try
        {
            await SetDocumentMarkdownAsync(string.Empty);
            _currentPath = null;
            _displayName = null;
            _cleanMarkdown = string.Empty;
            _diskBaseline = string.Empty;
            _lineEnding = DocumentText.DefaultLineEnding;   // nothing open; nothing to keep
            _hadBom = false;
            _dirty = false;
        }
        finally { _suppressDirty = false; }
        UpdateTitle();
        SetClosed(true);
        DiscardBackup();   // the user was asked and chose to let it go
        ReleaseDocumentClaim();   // nothing is open here any more for another window to find
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
        // _cleanMarkdown to a fresh string instance even for identical text, so a
        // reference check detects that baseline movement — without it, a user Save
        // that lands during our awaits reads as "nothing to lose" and the pass would
        // quietly revert the just-saved document to the stale disk content it read.
        //
        // Except for an EMPTY document, where PassValid() is only its StillEditing
        // half: "" is interned, every route hands back the one string.Empty, and a
        // reassignment of "" to "" is invisible to a reference check (see
        // SetCleanBaselineAsync). What stands in for it on the reload path is the
        // re-read below — `confirm` against `newContent`, taken right before
        // ReloadPreservingPositionAsync — which a mid-pass save of an empty document
        // fails, so the pass rechecks instead of reloading.
        var cleanAtStart = _cleanMarkdown;
        bool PassValid() => StillEditing(path) && ReferenceEquals(_cleanMarkdown, cleanAtStart);

        // Don't read until the writer has stopped changing the file: a program that
        // truncates then streams lets a plain read succeed on half a document, and
        // the reload would then make that half document the new baseline.
        DocumentText.Decoded fresh;
        if (_docEncrypted && _docPassword is { } docPw)
        {
            // An encrypted document compares DECRYPTED content — text-reading the
            // container would "differ" every time and reload garbage. A file that
            // stops opening with our password (re-encrypted elsewhere, or damaged)
            // is reported, not reloaded: never replace the user's buffer with
            // content we can't verify.
            var bytes = await ReadBytesWhenStableAsync(path);
            if (bytes is null) return;
            try { fresh = DocumentText.Detect(Secure.SecureMarkdownFormat.Decrypt(bytes, docPw)); }
            catch (Secure.SecureMarkdownException)
            {
                if (!PassValid()) { RecheckExternalChange(path); return; }
                FlashStatus("This file changed on disk and no longer opens with the current password — your copy is untouched");
                return;
            }
        }
        else
        {
            var read = await ReadWhenStableAsync(path);
            if (read is null) return;
            fresh = read.Value;
        }
        var newContent = fresh.Text;
        // Re-assert identity + freshness after EVERY await: if the user opened another
        // document or saved while we waited, acting now would clobber it — and the
        // unsaved-work check below would cheerfully call it "nothing to lose".
        if (!PassValid()) { RecheckExternalChange(path); return; }

        // Judged against the DISK baseline as well as the editor's serialisation
        // of it (issue #4): the two differ for any file the editor normalises, and
        // only the latter used to be consulted, so a tool that rewrote identical
        // bytes prompted about a file that had not changed. Equal to either is not
        // a change; ExternalChange says which baseline means what.
        if (!ExternalChange.IsRealChange(newContent, _cleanMarkdown, _diskBaseline))
        {
            // The same document, possibly re-encoded - dos2unix ran, a tool added
            // or stripped the mark. Nothing to reload or ask about, but the file's
            // conventions are whatever it has NOW: "kept as found" means the next
            // Save writes those, not the ones it had when it was opened.
            _diskBaseline = newContent;
            _lineEnding = fresh.Ending;
            _hadBom = fresh.HadBom;
            return;
        }

        // Ask the editor what it actually holds rather than trusting `_dirty`, which
        // is a debounced cache: ScheduleDirtyCheck() RESTARTS a 250ms timer on every
        // keystroke, so during continuous typing it never fires and `_dirty` stays
        // false for the whole burst. Believing it here would silently discard live
        // edits — with no backup, since this path deliberately writes none.
        var inMemory = await TryGetDocumentMarkdownAsync();
        if (inMemory is null) return;                       // couldn't ask; don't guess
        if (!PassValid()) { RecheckExternalChange(path); return; }
        // Same two-baseline question as everywhere else (IsUnmodifiedText): a source
        // view showing the file's own spelling holds no unsaved work, and reading it
        // as work would write a timestamped .bak and raise the external-change dialog
        // for a document the user has not touched.
        var hasUnsavedWork = !IsUnmodifiedText(inMemory);

        // Nothing unsaved: the in-memory copy IS the old disk version, so there's
        // nothing to lose, nothing worth backing up, and nothing to ask about.
        if (_autoReload && !hasUnsavedWork)
        {
            // Last look at the disk right before acting: if the file moved again after
            // our stable read (or a mid-pass save rewrote it), newContent is stale —
            // reload nothing, and let the recheck run against the current state.
            string? confirm;
            try
            {
                // Same decode the ORIGINAL read used: an encrypted document
                // compares decrypted text. Reading the container as text here
                // compared ciphertext against plaintext - never equal - and spun
                // reload/recheck (one full KDF per lap) forever. And the same fold,
                // so it is text against text: a raw read would differ from newContent
                // on every CRLF file and spin the same way.
                confirm = _docEncrypted && _docPassword is { } confirmPw
                    ? DocumentText.Fold(await Task.Run(() => Secure.SecureMarkdownFormat.Decrypt(File.ReadAllBytes(path), confirmPw)))
                    : DocumentText.Detect(await File.ReadAllBytesAsync(path)).Text;
            }
            catch { RecheckExternalChange(path); return; }
            if (!PassValid() || !string.Equals(confirm, newContent, StringComparison.Ordinal))
            {
                RecheckExternalChange(path);
                return;
            }
            await ReloadPreservingPositionAsync(path, fresh, PassValid);
            return;
        }

        // Save the current (possibly unsaved) in-memory version as a timestamped backup.
        // For an encrypted document the backup is SEALED with the same password -
        // writing the decrypted text beside the file would be a silent plaintext
        // leak of exactly the content the user protected (design 7b).
        string backupPath;
        try
        {
            var (ending, bom) = (_lineEnding, _hadBom);   // read on the UI thread, not inside Task.Run
            backupPath = _docEncrypted && _docPassword is { } bakPw
                ? await Task.Run(() => WriteTimestampedEncryptedBackup(path, inMemory, ending, bakPw))
                : WriteTimestampedBackup(path, inMemory, ending, bom);
        }
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
                // Same password-preservation as ReloadPreservingPositionAsync.
                await LoadDocumentAsync(fresh, path, _docEncrypted ? _docPassword : null);
                break;
            case ExternalChangeChoice.SaveAs:
                await HandleSaveAsAfterExternalChangeAsync(inMemory, fresh, backupPath);
                break;
            case ExternalChangeChoice.Keep:
            default:
                AcceptDiskAsBaseline(fresh);   // the next Save will overwrite the disk
                _ = UpdateDirtyAsync();
                break;
        }
    }

    /// <summary>
    /// The disk version becomes the baseline without being loaded (Keep Current, or
    /// a Save As backed out of): dirty now means "my edits differ from the file",
    /// and the file's conventions - which may have changed along with it - are the
    /// ones the next Save writes.
    /// </summary>
    private void AcceptDiskAsBaseline(DocumentText.Decoded disk)
    {
        _cleanMarkdown = disk.Text;
        _diskBaseline = disk.Text;
        _lineEnding = disk.Ending;
        _hadBom = disk.HadBom;
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
    private static async Task<byte[]?> ReadBytesWhenStableAsync(string path)
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
                    return await File.ReadAllBytesAsync(path);
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

    /// <summary>The plaintext flavour: the same stable read, decoded the way Open
    /// decodes - folded, with the file's line ending and mark alongside.</summary>
    private static async Task<DocumentText.Decoded?> ReadWhenStableAsync(string path)
    {
        var bytes = await ReadBytesWhenStableAsync(path);
        return bytes is null ? null : DocumentText.Detect(bytes);
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
    private async Task ReloadPreservingPositionAsync(string path, DocumentText.Decoded fresh, Func<bool> stillValid)
    {
        var anchor = await CaptureAnchorAsync();
        if (!stillValid()) { RecheckExternalChange(path); return; }
        // Keep the encryption state through the reload: dropping the password here
        // demoted the window to plaintext while _currentPath stayed the .mdenc, and
        // the next silent Ctrl+S would have written DECRYPTED text into it.
        await LoadDocumentAsync(fresh, path, _docEncrypted ? _docPassword : null);
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

    private static string TimestampedBakPath(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(dir, $"{name}.{stamp}{ext}.bak");
        // Highly unlikely collision (same second) — append milliseconds.
        if (File.Exists(path))
            path = Path.Combine(dir, $"{name}.{stamp}-{DateTime.Now.Millisecond:D3}{ext}.bak");
        return path;
    }

    /// <summary>Your version, in the file's own conventions: the .bak sits beside
    /// the original and may be opened in its place, so it should look like it.</summary>
    private static string WriteTimestampedBackup(string originalPath, string content, LineEnding ending, bool bom)
    {
        var path = TimestampedBakPath(originalPath);
        File.WriteAllBytes(path, DocumentText.Encode(content, ending, bom));
        return path;
    }

    /// <summary>The encrypted flavour: same naming, sealed contents (full KDF -
    /// rare path, correctness over speed). Line ending applied, no mark - as Save.</summary>
    private static string WriteTimestampedEncryptedBackup(string originalPath, string content, LineEnding ending, string password)
    {
        var path = TimestampedBakPath(originalPath);
        File.WriteAllBytes(path, Secure.SecureMarkdownFormat.Encrypt(DocumentText.ApplyLineEnding(content, ending), password));
        return path;
    }

    private async Task HandleSaveAsAfterExternalChangeAsync(string inMemory, DocumentText.Decoded disk, string backupPath)
    {
        if (_currentPath is null) return;
        var dir = Path.GetDirectoryName(_currentPath) ?? "";
        var nameNoExt = Path.GetFileNameWithoutExtension(_currentPath);
        var ext = Path.GetExtension(_currentPath);
        var suggested = Path.GetFileName(backupPath).Replace(".bak", "");
        var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
        {
            Save = true,
            Title = "Save your current version as…",
            Filter = _docEncrypted
                ? "Secure Markdown (*.mdenc)|*.mdenc|All files (*.*)|*.*"
                : "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files (*.*)|*.*",
            DefaultExt = ext.Length > 0 ? ext : ".md",
            InitialDirectory = dir,
            FileName = suggested,
            RecentFolders = PickerRecentFolders(),
        });
        if (picked is null)
        {
            // User backed out of save-as — treat like Keep Current.
            AcceptDiskAsBaseline(disk);
            _ = UpdateDirtyAsync();
            return;
        }

        try
        {
            // An encrypted document's "your version" stays encrypted - a plaintext
            // file wearing the .mdenc name would be both a leak and a lie. Either
            // way it is written in the document's own conventions, as Save would.
            if (_docEncrypted && _docPassword is { } savePw)
            {
                var plaintext = DocumentText.ApplyLineEnding(inMemory, _lineEnding);
                await Task.Run(() => Secure.SecureMarkdownFile.Save(picked, plaintext, savePw));
            }
            else
                await File.WriteAllBytesAsync(picked, DocumentText.Encode(inMemory, _lineEnding, _hadBom));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't save:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        AddRecent(picked);

        // Now ask which to keep viewing.
        var fileName = Path.GetFileName(_currentPath);
        var savedFileName = Path.GetFileName(picked);
        var pick = MessageBox.Show(
            $"Saved your version to:\n{picked}\n\nKeep editing your saved version ({savedFileName})?\n\nYes = open '{savedFileName}'\nNo = continue with the externally-modified '{fileName}'",
            "Markdown Midget", MessageBoxButton.YesNo, MessageBoxImage.Question);
        // Both loads keep the encryption state (rounds 1 and 2 each found a
        // password-dropping load; these are call sites 3 and 4 of 4 - every
        // LoadDocumentAsync caller on an encrypted path now passes it through).
        if (pick == MessageBoxResult.Yes)
        {
            // Already on disk with inMemory content, in this document's conventions;
            // load + retarget.
            await LoadDocumentAsync(new DocumentText.Decoded(inMemory, _lineEnding, _hadBom), picked,
                _docEncrypted ? _docPassword : null);
            RekeyDocumentClaim(picked);   // a Save As by another name: the claim moves too
        }
        else
        {
            await LoadDocumentAsync(disk, _currentPath, _docEncrypted ? _docPassword : null);
        }
    }

    // ===== Windows integration (register / unregister as .md editor) =====

    private async void RegisterMdEditor_Click(object sender, RoutedEventArgs e)
    {
        var alreadyInstalled = RegistrationService.IsRunningFromAppDataInstall();

        // A development build's exe is only the .NET apphost. Installed without the
        // files beside it, it can't start; with Move checked, the hand-off below would
        // launch it and shut the app down, and the app would simply vanish. So it
        // is refused here, before the dialog: nothing is asked, copied, registered or
        // handed off. Only when a copy would happen, since registering the installed
        // copy in place copies nothing. InstallToAppData refuses the same exe itself.
        if (!alreadyInstalled && RegistrationService.CurrentExeNeedsFilesBesideIt())
        {
            MessageBox.Show(this, RegistrationService.DevelopmentBuildRefusal, "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new RegisterDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;

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
                StartHandingOffDocument(() => Process.Start(psi));
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
            _findDialog.ReplaceRequested += OnReplaceRequested;
            _findDialog.SetReadOnly(_readOnly);
            _findDialog.Closed2 += (_, _) =>
            {
                _findDialog = null;
                _ = ClearWysiwygFindAsync();
                _sourceFindMatches = null;
                _sourceFindCursor = -1;
                _sourceFindSelection = null;
                _sourceReplaceScope = null;
            };
            _findDialog.Show();
        }
        CaptureReplaceScope();
        _findDialog.FocusQuery();
    }

    /// <summary>
    /// Keep the selection as it is when Find opens (or is brought back with Ctrl+F)
    /// for Replace All. Find moves the selection onto each match as the query is
    /// typed, so by the time Replace All is pressed the selection the user made is
    /// gone; this is it. A selection that is Find's own — the current match — is not
    /// taken as a new range (what was kept stays, so Ctrl+F to bring the dialog back
    /// changes nothing), and that is asked FIRST, because Find's selection of a
    /// zero-width match is itself a caret; any other caret drops what was kept.
    /// The decision is <see cref="FindEngine.CaptureDecision"/>, which the formatted
    /// view follows too. Source view: two anchors follow the range
    /// through the replacements made here, and any other edit drops it. Formatted
    /// view: the editor keeps it, under the same rules (find.js findCaptureScope).
    /// </summary>
    private void CaptureReplaceScope()
    {
        if (!_sourceMode)
        {
            if (_editorReady) _ = RunEditorAsync("window.MDM.findCaptureScope()");
            return;
        }
        if (!_replaceScopeHooked)
        {
            _replaceScopeHooked = true;
            SourceBox.TextEdited += (_, _, _) => { if (!_applyingReplace) _sourceReplaceScope = null; };
        }
        var start = SourceBox.SelectionStart;
        var length = SourceBox.SelectionLength;
        switch (FindEngine.CaptureDecision(length, IsSourceFindSelection()))
        {
            case FindEngine.ScopeCapture.Keep: return;              // Find's own: keep what was kept
            case FindEngine.ScopeCapture.Drop: _sourceReplaceScope = null; return;
        }
        var doc = SourceBox.Document;
        var s = doc.CreateAnchor(start);
        s.MovementType = ICSharpCode.AvalonEdit.Document.AnchorMovementType.BeforeInsertion;
        s.SurviveDeletion = true;
        var e = doc.CreateAnchor(start + length);
        e.MovementType = ICSharpCode.AvalonEdit.Document.AnchorMovementType.AfterInsertion;
        e.SurviveDeletion = true;
        _sourceReplaceScope = (s, e);
    }

    /// <summary>Is the source selection the one Find (or Replace) last made?</summary>
    private bool IsSourceFindSelection() =>
        _sourceFindSelection is { } f && SourceBox.SelectionStart == f.Start && SourceBox.SelectionLength == f.Length;

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
                : FindEngine.InvalidPatternMessage);
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

        SelectSourceMatch(_sourceFindMatches[_sourceFindCursor]);
        _findDialog?.SetStatus($"Match {_sourceFindCursor + 1} of {total}");
    }

    private void SelectSourceMatch(System.Text.RegularExpressions.Match m)
    {
        SourceBox.Select(m.Index, m.Length);
        SourceBox.ScrollToLine(SourceBox.GetLineIndexFromCharacterIndex(m.Index));
        _sourceFindSelection = (m.Index, m.Length);
    }

    private async Task DoWysiwygFindAsync(System.Text.RegularExpressions.Regex regex, FindRequest req)
    {
        if (!_editorReady) return;
        if (!await EnsureWysiwygIndexAsync(regex)) return;   // already reported
        var dir = req.Forward ? "Next" : "Prev";
        var result = await RunEditorAsync($"JSON.stringify(window.MDM.find{dir}({Js(req.Wrap)}))");
        ReportFindResult(result, req);
    }

    /// <summary>
    /// Forget which pattern the editor's find index was built from, so the next Find
    /// or Replace rebuilds it instead of trusting what is there.
    ///
    /// <see cref="EnsureWysiwygIndexAsync"/> skips findReset when the pattern and
    /// options are the ones it last sent, which is what lets F3 advance the match
    /// cursor rather than starting at match 1 every time. That shortcut is only true
    /// while the index it remembers still exists and still describes THIS document —
    /// so every place that empties the index or replaces the document has to say so
    /// here. Closing the Find dialog empties it (#5 NF-9: the dialog reopens with the
    /// last query, so without this the answer came back from an empty index —
    /// "Nothing to replace." on a document full of matches); loading a document
    /// replaces what the index describes, and unlike an edit it raises no change
    /// message to clear the cache the other way; and a pattern the editor refused
    /// built no index at all, so nothing may claim to describe one.
    /// </summary>
    private void ForgetWysiwygFindIndex()
    {
        _lastFindSource = "";
        _lastFindFlags = "";
    }

    /// <summary>
    /// Pass the regex source + flags to JS (which builds a JS RegExp from it) when
    /// the pattern OR options changed, so explicit Find Next / Find Previous advance
    /// the match cursor rather than rebuilding from match 1. The editor keeps its
    /// index and current match when asked for the same pattern on an unchanged
    /// document, so re-issuing this after one of our own replacements — the change
    /// message it raises clears the cache below — loses the place to nothing.
    ///
    /// False when the editor refused the pattern (#5 F-6): the status line already
    /// says <see cref="FindEngine.InvalidPatternMessage"/> and the caller must not go
    /// on to find or replace against an index that was never built. FindEngine.Build
    /// refuses the constructs we know the two engines disagree about before we get
    /// here, so this is the backstop rather than the gate — and a refusal is not
    /// cached, so the next attempt asks again and reports again.
    /// </summary>
    private async Task<bool> EnsureWysiwygIndexAsync(System.Text.RegularExpressions.Regex regex)
    {
        var flags = "g";
        if ((regex.Options & System.Text.RegularExpressions.RegexOptions.IgnoreCase) != 0) flags += "i";
        if ((regex.Options & System.Text.RegularExpressions.RegexOptions.Multiline) != 0) flags += "m";
        var src = regex.ToString();
        if (src != _lastFindSource || flags != _lastFindFlags)
        {
            var json = await RunEditorAsync(
                $"JSON.stringify(window.MDM.findReset({JsLiteral(src)}, {JsLiteral(flags)}))");
            if (FindEngine.ReportsInvalidPattern(json))
            {
                // A refusal built no index, so nothing here describes one (#5 NF-9).
                ForgetWysiwygFindIndex();
                _findDialog?.SetStatus(FindEngine.InvalidPatternMessage);
                return false;
            }
            _lastFindSource = src;
            _lastFindFlags = flags;
        }
        return true;
    }

    private static string Js(bool value) => value ? "true" : "false";

    private static int JsonInt(JsonDocument d, string name) =>
        d.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private void ReportFindResult(string? json, FindRequest req)
    {
        if (string.IsNullOrEmpty(json)) { _findDialog?.SetStatus("No matches."); return; }
        // A refusal is not a count of zero: say so rather than "No matches found."
        if (FindEngine.ReportsInvalidPattern(json))
        {
            _findDialog?.SetStatus(FindEngine.InvalidPatternMessage);
            return;
        }
        try
        {
            using var d = JsonDocument.Parse(json);
            var total = d.RootElement.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            var current = d.RootElement.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
            // A count from an index that stopped at the cap is a count of what it
            // reached, not of the document; say so rather than let it read as all of
            // them (#5 NF-10).
            var note = FindEngine.ReportsTruncated(json) ? FindEngine.TruncatedFindNote : "";
            if (total == 0)
                _findDialog?.SetStatus(req.LiveTyping ? "No matches." : "No matches found.");
            else
                _findDialog?.SetStatus($"Match {current} of {total}{note}");
        }
        catch { _findDialog?.SetStatus("No matches."); }
    }

    private async Task ClearWysiwygFindAsync()
    {
        // The cached pattern describes the index this is about to empty (#5 NF-9).
        ForgetWysiwygFindIndex();
        if (_editorReady)
            await RunEditorAsync("window.MDM.findClear()");
    }

    // ===== Replace (#5): the current match then the next, or every match as one undo step =====

    private async void OnReplaceRequested(object? sender, ReplaceRequest req)
    {
        // The dialog greys its buttons while read-only; this is the gate on the
        // request itself. A programmatic replace goes past AvalonEdit's IsReadOnly
        // and the editor's editable flag alike, so nothing below may run read-only.
        if (_readOnly)
        {
            _findDialog?.SetStatus("Read-only document — nothing replaced.");
            return;
        }

        var find = req.Find;
        var spec = FindEngine.Prepare(find.Query, find.Mode, find.MatchCase, find.WholeWord, req.Replacement);
        if (spec is null)
        {
            // The refusal Find gives, and nothing has been applied.
            _findDialog?.SetStatus(string.IsNullOrEmpty(find.Query)
                ? "Type to search."
                : FindEngine.InvalidPatternMessage);
            return;
        }

        if (_sourceMode)
        {
            if (req.All) DoSourceReplaceAll(spec);
            else DoSourceReplace(spec, find);
        }
        else if (req.All) await DoWysiwygReplaceAllAsync(spec);
        else await DoWysiwygReplaceAsync(spec, find);
    }

    /// <summary>
    /// Replace the current match — the one Find selected — and go on to the next.
    /// With no current match, or the selection no longer on it (moved away, or the
    /// text changed under it), this is Find Next, as the button promises.
    /// </summary>
    private void DoSourceReplace(FindEngine.ReplaceSpec spec, FindRequest req)
    {
        var text = SourceBox.Text;
        var m = _sourceFindMatches is { } ms && _sourceFindCursor >= 0 && _sourceFindCursor < ms.Count
            ? ms[_sourceFindCursor]
            : null;
        if (m is null || !IsSourceFindSelection() || m.Index + m.Length > text.Length
            || !string.Equals(text.Substring(m.Index, m.Length), m.Value, StringComparison.Ordinal))
        {
            DoSourceFind(spec.Regex, req);
            return;
        }

        var replacement = spec.ReplacementFor(m);
        _applyingReplace = true;
        try { SourceBox.Document.Replace(m.Index, m.Length, replacement); }
        finally { _applyingReplace = false; }

        // Search again from the end of the replacement — past it when the match was
        // empty, or the same empty match would be found there again forever.
        var resume = m.Index + replacement.Length;
        var strictlyAfter = m.Length == 0;
        _sourceFindMatches = spec.Regex.Matches(SourceBox.Text);
        var total = _sourceFindMatches.Count;
        _sourceFindCursor = -1;
        for (var i = 0; i < total; i++)
        {
            var idx = _sourceFindMatches[i].Index;
            if (strictlyAfter ? idx > resume : idx >= resume) { _sourceFindCursor = i; break; }
        }
        if (_sourceFindCursor < 0 && req.Wrap && total > 0) _sourceFindCursor = 0;
        if (_sourceFindCursor < 0)
        {
            // AvalonEdit has moved the selection onto the replacement text; left
            // there it would pass for a selection of the user's and scope the next
            // Replace All to three characters. A caret after it instead.
            SourceBox.Select(resume, 0);
            _sourceFindSelection = null;
            _findDialog?.SetStatus(total == 0 ? "Replaced. No more matches." : "Replaced. No further matches.");
            return;
        }
        SelectSourceMatch(_sourceFindMatches[_sourceFindCursor]);
        _findDialog?.SetStatus($"Replaced. Match {_sourceFindCursor + 1} of {total}");
    }

    private void DoSourceReplaceAll(FindEngine.ReplaceSpec spec)
    {
        var text = SourceBox.Text;
        var wasFinds = IsSourceFindSelection();
        var scope = SourceReplaceScope();
        var edits = scope is { } s ? spec.ReplaceAllEdits(text, s.Start, s.Length) : spec.ReplaceAllEdits(text);
        _applyingReplace = true;
        int replaced;
        try { replaced = SourceBox.ApplyEdits(edits); }
        finally { _applyingReplace = false; }
        // The match list describes the old text; the next Find rebuilds it.
        _sourceFindMatches = null;
        _sourceFindCursor = -1;
        _sourceFindSelection = null;
        if (wasFinds)
        {
            // Find's selection of a match is now that match's replacement, which
            // would pass for the user's own next time. The kept range, selected
            // outright, is what a second Replace All should work on; failing that,
            // a caret.
            if (scope is not null && _sourceReplaceScope is { } a && a.End.Offset > a.Start.Offset)
                SourceBox.Select(a.Start.Offset, a.End.Offset - a.Start.Offset);
            else
                SourceBox.Select(SourceBox.SelectionStart, 0);
        }
        // The source view edits the document text directly: nothing spans a block it
        // cannot cross, and nothing has moved out from under the plan.
        ReportReplaceAll(replaced, scope is not null, spanningBlocks: 0, moved: 0);
    }

    /// <summary>
    /// The Replace All scope in the source view: selected text is the scope — unless
    /// it is Find's own selection of the current match (a caret, when that match has
    /// no width), in which case the selection kept when the dialog opened is
    /// (<see cref="CaptureReplaceScope"/>). A caret of the user's own, or nothing
    /// kept, means the whole document. The decision is
    /// <see cref="FindEngine.ResolveScope"/>, which the formatted view follows too.
    /// </summary>
    private (int Start, int Length)? SourceReplaceScope()
    {
        var kept = _sourceReplaceScope is { } a && a.End.Offset > a.Start.Offset
            ? ((int Start, int Length)?)(a.Start.Offset, a.End.Offset - a.Start.Offset)
            : null;
        return FindEngine.ResolveScope(
            SourceBox.SelectionStart, SourceBox.SelectionLength, IsSourceFindSelection(), kept);
    }

    /// <summary>The count, in the status bar and in the dialog.</summary>
    private void ReportReplaceAll(int replaced, bool inSelection, int spanningBlocks, int moved)
    {
        var msg = FindEngine.ReplaceAllStatus(replaced, inSelection, spanningBlocks, moved);
        FlashStatus(msg);
        _findDialog?.SetStatus(msg);
    }

    private async Task DoWysiwygReplaceAsync(FindEngine.ReplaceSpec spec, FindRequest req)
    {
        if (!_editorReady) return;
        if (!await EnsureWysiwygIndexAsync(spec.Regex)) return;   // already reported
        var json = await RunEditorAsync(
            $"JSON.stringify(window.MDM.findReplace({JsLiteral(spec.Replacement)}, {Js(spec.Literal)}, {Js(req.Wrap)}))");
        if (string.IsNullOrEmpty(json)) { _findDialog?.SetStatus("No matches."); return; }
        if (FindEngine.ReportsInvalidPattern(json))
        {
            _findDialog?.SetStatus(FindEngine.InvalidPatternMessage);
            return;
        }
        try
        {
            using var d = JsonDocument.Parse(json);
            var replaced = JsonInt(d, "replaced");
            var skipped = JsonInt(d, "skipped");
            var total = JsonInt(d, "total");
            var current = JsonInt(d, "current");
            // Replace works from a truncated index — it changes the match Find is on,
            // which that index does know about — but the count beside it is short, so
            // it is reported as short (#5 NF-10).
            var note = FindEngine.ReportsTruncated(json) ? FindEngine.TruncatedFindNote : "";
            var place = total == 0 ? "No more matches."
                      : current == 0 ? "No further matches."
                      : $"Match {current} of {total}{note}";
            _findDialog?.SetStatus(
                replaced > 0 ? $"Replaced. {place}"
                : skipped > 0 ? $"Left alone — that match spans paragraphs. {place}"
                : total == 0 ? "No matches found."
                : place);
        }
        catch { _findDialog?.SetStatus("No matches."); }
    }

    private async Task DoWysiwygReplaceAllAsync(FindEngine.ReplaceSpec spec)
    {
        if (!_editorReady) return;
        if (!await EnsureWysiwygIndexAsync(spec.Regex)) return;   // already reported
        var json = await RunEditorAsync(
            $"JSON.stringify(window.MDM.findReplaceAll({JsLiteral(spec.Replacement)}, {Js(spec.Literal)}))");
        if (string.IsNullOrEmpty(json)) { _findDialog?.SetStatus("No matches."); return; }
        if (FindEngine.ReportsInvalidPattern(json))
        {
            _findDialog?.SetStatus(FindEngine.InvalidPatternMessage);
            return;
        }
        // The editor refuses Replace All from an index that stopped at its cap: that
        // index describes the start of the document and nothing past it, so working
        // from it would change that much and report the number as though it were every
        // match (#5 NF-10). Nothing has been applied.
        if (FindEngine.ReportsTruncated(json))
        {
            FlashStatus(FindEngine.TruncatedReplaceAllMessage);
            _findDialog?.SetStatus(FindEngine.TruncatedReplaceAllMessage);
            return;
        }
        try
        {
            using var d = JsonDocument.Parse(json);
            var inSelection = d.RootElement.TryGetProperty("inSelection", out var s) && s.ValueKind == JsonValueKind.True;
            ReportReplaceAll(JsonInt(d, "replaced"), inSelection, JsonInt(d, "skipped"), JsonInt(d, "moved"));
        }
        catch { _findDialog?.SetStatus("No matches."); }
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

        var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
        {
            Save = true,
            Title = "Export to PDF",
            Filter = "PDF (*.pdf)|*.pdf|All files (*.*)|*.*",
            DefaultExt = ".pdf",
            FileName = defaultName,
            InitialDirectory = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : null,
            RecentFolders = PickerRecentFolders(),
        });
        if (picked is null) return;

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

            var ok = await Web.CoreWebView2.PrintToPdfAsync(picked, settings);
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

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        BuildRecentMenu();
        // Guard at the operation as well (each handler re-checks) — the menu state
        // is UX, not the enforcement.
        var editableDoc = !_readOnly && !_isHelpWindow && !_closed;
        EncryptMenu.IsEnabled = editableDoc && !_docEncrypted;
        ChangePasswordMenu.IsEnabled = editableDoc && _docEncrypted;
        ConvertPlainMenu.IsEnabled = editableDoc && _docEncrypted && _currentPath is not null;
    }

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
        public bool KeepBackup { get; set; } = true;     // crash copy of unsaved work
        public bool ShowEncryptedInOpen { get; set; }    // *.mdenc in the Open filter (opt-in)
        public bool UseBuiltInPicker { get; set; }       // skip the native dialog entirely
        // The theme's FILENAME, not its position in the menu — the list changes when
        // a file is added or removed, and an index would then select a different one.
        public string Theme { get; set; } = "";
        // The source view's own theme when the two views are unlinked — same list, same
        // filename rule. Ignored while linked; relinking resets it to the document theme.
        public string? SourceTheme { get; set; }
        // View ▸ Theme ▸ "Same Theme for Both Views". Default on.
        public bool LinkThemes { get; set; } = true;
        // The newest version whose changelog the user has actually opened — compared
        // with WhatsNewState, not equality, so an older value after an update is the
        // ordinary case rather than something to migrate.
        public string? LastSeenChangelogVersion { get; set; }
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
    private bool _backupEnabled = true;      // persisted; keep a crash copy of unsaved work
    private string? _recoverSessionId;       // --recover <id>: restore exactly this snapshot
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
            _showEncryptedInOpen = s.ShowEncryptedInOpen;
            _useBuiltInPicker = s.UseBuiltInPicker;
            _recentLimit = Math.Clamp(s.RecentLimit, SettingsDialog.MinRecent, SettingsDialog.MaxRecentLimit);
            _startWithBlankDocument = s.StartWithBlankDocument;
            _backupEnabled = s.KeepBackup;
            _themeKey = s.Theme ?? Themes.ThemeStore.DefaultKey;
            _linkThemes = s.LinkThemes;
            _sourceThemeKey = s.SourceTheme ?? _themeKey;
            _lastSeenChangelogVersion = s.LastSeenChangelogVersion;
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
    ///
    /// Theme and LastSeenChangelogVersion are carried through from disk the same
    /// way, and for the same reason: they are each written by their OWN dedicated
    /// caller (<see cref="Themes.ThemeStore"/>'s consumer via SetThemeKey, and
    /// MarkChangelogSeen) via <see cref="SavePersistentField"/>, not by this generic
    /// path. Multiple windows share this one file; a window that toggled word wrap
    /// an hour ago is not carrying today's theme choice or today's "seen" version in
    /// its in-memory fields, and a generic save from THAT toggle must not overwrite
    /// what a different, more current window already wrote for either.
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
            s.Theme = existing?.Theme ?? s.Theme;
            s.SourceTheme = existing?.SourceTheme ?? s.SourceTheme;
            s.LinkThemes = existing?.LinkThemes ?? s.LinkThemes;
            s.LastSeenChangelogVersion = existing?.LastSeenChangelogVersion ?? s.LastSeenChangelogVersion;
            WriteSettings(s);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Persist ONE field, merged onto whatever else is on disk right now — the same
    /// pattern <see cref="SaveWindowPlacement"/> already uses for geometry, extended
    /// to any single value whose writer knows it just changed something real.
    ///
    /// The generic <see cref="SaveSettings"/> restates every OTHER field from this
    /// window's own in-memory snapshot, which is exactly correct for a toggle this
    /// window just made and exactly wrong for a field it merely loaded once at
    /// startup and never touched again. Going through disk here, rather than through
    /// CurrentSettings(), means a caller that only wants to change one thing can't
    /// accidentally also republish a stale copy of everything else.
    /// </summary>
    private void SavePersistentField(Action<AppSettings> apply)
    {
        if (_settingsUnknown) return;
        try
        {
            if (!TryReadSettings(out var existing)) return;
            var s = existing ?? CurrentSettings();
            apply(s);
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
        ShowEncryptedInOpen = _showEncryptedInOpen,
        UseBuiltInPicker = _useBuiltInPicker,
        RecentLimit = _recentLimit,
        StartWithBlankDocument = _startWithBlankDocument,
        KeepBackup = _backupEnabled,
        Theme = _themeKey,
        SourceTheme = _sourceThemeKey,
        LinkThemes = _linkThemes,
        LastSeenChangelogVersion = _lastSeenChangelogVersion,
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
                // Asked and answered: they either saved it or chose to let it go, so
                // there is nothing left for a crash copy to rescue.
                EndBackup();
                ReleaseDocumentClaim();
                // NOT a direct Close(): on "Don't Save" the await above completed
                // synchronously (a modal MessageBox returns on the same stack, and
                // that branch awaits nothing else), so this continuation is still
                // INSIDE the original Closing dispatch — and Close() while a window
                // is closing throws. Latent since the MVP: the unhandled exception
                // killed the process, which is indistinguishable from a successful
                // close, until the crash handler started catching it (crash.log,
                // 2026-08-30 — its first real-world catch). One dispatcher hop lets
                // the first close fully unwind before the second begins; Normal
                // priority (BeginInvoke never runs inline at any priority) keeps
                // the open-window gap as small as it can be.
                _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
            }
            return;
        }
        StopWatching();
        EndBackup();
        ReleaseDocumentClaim();   // the kernel would do it at exit; this is just sooner
    }

    /// <summary>
    /// Close this window's backup session on a clean exit: drop the snapshot and
    /// release the lock. Anything still on disk afterwards therefore means a session
    /// that ended without getting here — which is exactly what recovery looks for.
    /// </summary>
    private void EndBackup()
    {
        _backupTimer.Stop();
        DiscardBackup();
        _backup?.Dispose();
        _backup = null;
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
        // Exact match first; then any code block whose language isn't in the
        // fixed list (mermaid, python, ...) falls back to the generic code item.
        // Without the fallback the combo silently kept its previous value, so a
        // caret inside a mermaid block still said "Paragraph" (dogfooding,
        // 0.9.0-beta1) - a wrong answer is worse than a generic one.
        ComboBoxItem? fallback = null;
        foreach (var obj in StyleCombo.Items)
            if (obj is ComboBoxItem ci && ci.Tag is string t)
            {
                if (t == tag)
                {
                    _syncingStyle = true;
                    StyleCombo.SelectedItem = ci;
                    _syncingStyle = false;
                    return;
                }
                if (t == "codeblock:" && tag.StartsWith("codeblock:", StringComparison.Ordinal))
                    fallback = ci;
            }
        if (fallback is not null)
        {
            _syncingStyle = true;
            StyleCombo.SelectedItem = fallback;
            _syncingStyle = false;
        }
    }

    /// <summary>
    /// Apply the editor-reported mark states to the toolbar toggles and the
    /// Format menu checkmarks. Programmatic IsChecked changes don't raise Click,
    /// so no guard flag is needed — the Click handlers only fire for user input.
    /// </summary>
    private void SyncMarkToggles(bool bold, bool italic, bool underline, bool strike, bool code)
    {
        BoldToggle.IsChecked = MenuBoldItem.IsChecked = bold;
        ItalicToggle.IsChecked = MenuItalicItem.IsChecked = italic;
        UnderlineToggle.IsChecked = MenuUnderlineItem.IsChecked = underline;
        StrikeToggle.IsChecked = MenuStrikeItem.IsChecked = strike;
        CodeToggle.IsChecked = MenuCodeItem.IsChecked = code;
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
        var picked = Picker.FilePickerService.Show(this, new Picker.FilePickerRequest
        {
            Title = "Insert Picture",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : null,
            RecentFolders = PickerRecentFolders(),
        });
        if (picked is null) return;

        PickedPictureResult picture;
        try
        {
            // Embedded as a base64 data URI — ImageMarkdown says why, and a picture
            // pasted into the source view or dropped on either view takes the same
            // shape from the same place (a dropped file gets this alt text too).
            // PickedPicture applies the one picture ceiling on the way: to the size
            // on disk before anything is read, and to what the bounded read returns.
            picture = await PickedPicture.ReadAsync(picked);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read the image:\n{ex.Message}", "Insert Picture",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Past the ceiling: nothing is inserted, and the status bar says so in the
        // words a refused drop uses.
        if (picture.Notice is { } notice)
        {
            FlashStatus(notice);
            return;
        }
        InsertMarkdownFragment(picture.Markdown!);
    }

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

    // ===== Drag & drop: pictures go in, markdown opens, anything else is refused =====
    //
    // Two surfaces receive drops. The WPF window — toolbar, menu bar, status bar,
    // the closed-document splash, and the source view: AvalonEdit's TextArea takes
    // only text drags (its handler marks nothing handled for a file drag) and lets a
    // file drop bubble up to Window_Drop, with the file's path. The formatted view
    // is a WebView2, a separate HWND that takes its own drops and posts them as the
    // 'fileDrop' message, with each file's name, size and first bytes but no path
    // (HandleDroppedFiles). Both hand their files to DropRouting and act on its
    // plan, so the rules are in one place and tested there; what differs here is
    // only how a picture's bytes are fetched and how a document opens.
    //
    // Neither route reads a whole file to route one. The path route reads
    // SniffLength bytes off disk; the content route is sent SniffLength bytes and
    // asks the editor for the rest of whichever files the plan chose
    // ('droppedFileBytes'). A dropped 200 MB video costs 32 bytes on both.

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        // This drop owns the window now, so a read the formatted view left
        // outstanding ends here — the same thing a second drop on the formatted view
        // does. Without it, a toolbar drop during a content-route wait let that wait
        // finish and insert its own picture on top of this one.
        if (AbandonDroppedRead()) FlashStatus(DropHandshake.SupersededNotice);

        // Only the first bytes of each file are read to route it (DropFiles.Read); a
        // picture's bytes are read in full below, and a document's by OpenPathAsync.
        var files = paths.Select(DropFiles.Read).ToList();
        // This drop is the window's now. The bump is what a drop on the formatted
        // view will see if it lands while ReadAllAsync below is still going —
        // AbandonDroppedRead cannot reach that read, so this is what ends it.
        var then = DropPin(++_dropGeneration);
        var plan = DropRouting.Plan(files, then.Target, oneDocument: false);

        // False means the drop is no longer the window's to act on, and it has said
        // so. Everything below is about the same stale plan — the opens would put a
        // document the user never asked for into a window that has moved on, and
        // FlashStatus ASSIGNS, so plan.Notice() would replace the abandonment notice
        // with a line about files that were never going in. The content route has
        // always returned here; this is that route's answer, not a new policy.
        if (!await InsertDroppedPicturesAsync(plan, i => DropFiles.ReadAllAsync(paths[i]), then)) return;

        // Documents open as a drop here always opened them: the first in this window
        // only if it holds an untitled, unmodified document; otherwise (a file is
        // open, or there are unsaved edits) keep it and open everything in fresh
        // instances. The plan leaves this list empty when a picture went in.
        var openHere = _currentPath is null && !_dirty;
        for (var n = 0; n < plan.Open.Count; n++)
        {
            if (n == 0 && openHere) _ = OpenPathAsync(paths[plan.Open[0]]);
            else OpenInNewInstance(paths[plan.Open[n]]);
        }
        if (plan.Notice() is { } notice) FlashStatus(notice);
        Activate();
    }

    /// <summary>What the window can do with a dropped picture right now: the same
    /// two states that gate every other edit (SetReadOnly, SetClosed).</summary>
    private DropTarget DropTargetNow() =>
        _closed ? DropTarget.NoDocument : _readOnly ? DropTarget.ReadOnly : DropTarget.Editable;

    /// <summary>
    /// What a drop plan is decided against, captured BEFORE the drop's first await
    /// so it can be compared with the same four values afterwards
    /// (<see cref="DropHandshake.StillApplies"/>). Every await in a drop — a path
    /// read, and the content route's whole round trip to the editor — leaves the
    /// menus live, so all four can move underneath one.
    ///
    /// The fourth is the VIEW, and it is pinned as <see cref="_viewGeneration"/>
    /// rather than as _sourceMode itself. InsertMarkdownFragment routes by
    /// _sourceMode, and SetSourceModeAsync yields twice with _sourceMode still at
    /// its OLD value — so the flag compared equal for the whole of the switch, and
    /// an insertion landing in either gap went into the half of the window that was
    /// about to be thrown away, with neither half saying anything. The counter moves
    /// before the first of those awaits, so mid-switch is a movement too.
    ///
    /// <paramref name="generation"/> is not about the document at all: it is which
    /// DROP this is (<see cref="_dropGeneration"/>), and every caller claims a fresh
    /// one as it starts. Two drops racing on the same unchanged document match on
    /// all four of the above, so nothing else can tell them apart.
    /// </summary>
    private (string? Path, string Clean, DropTarget Target, long View, long Generation) DropPin(long generation) =>
        (_currentPath, _cleanMarkdown, DropTargetNow(), _viewGeneration, generation);

    /// <summary>
    /// Which drop the window belongs to, claimed by BOTH surfaces as they start
    /// (<see cref="DropHandshake.StillTheCurrentDrop"/>).
    ///
    /// AbandonDroppedRead only ends the content route's read — it completes a
    /// waiter, and the path route has none: its DropFiles.ReadAllAsync is a plain
    /// await inside Window_Drop that no one can reach. So a drop on the formatted
    /// view during a toolbar drop's disk read left both drops inserting into the
    /// same document. The pin's other four values cannot see that, because both
    /// drops are about the same document.
    ///
    /// Not _newestDrop: that is the EDITOR's counter, it numbers only formatted-view
    /// drops, and it restarts when the page does. This one is the host's and covers
    /// both surfaces.
    /// </summary>
    private long _dropGeneration;

    /// <summary>Whether the window is still the one <paramref name="then"/> was taken
    /// from. False means the plan made against it is stale and nothing from the drop
    /// may be applied.</summary>
    private bool DropStillApplies((string? Path, string Clean, DropTarget Target, long View, long Generation) then) =>
        DropHandshake.StillApplies(then.Path, _currentPath, then.Clean, _cleanMarkdown,
            then.Target, DropTargetNow(), then.View, _viewGeneration);

    /// <summary>Whether <paramref name="then"/>'s drop is still the one the window
    /// belongs to. Separate from <see cref="DropStillApplies"/> because it is a
    /// different failure with a different notice: no document moved, a newer drop
    /// simply took over.</summary>
    private bool DropIsStillCurrent((string? Path, string Clean, DropTarget Target, long View, long Generation) then) =>
        DropHandshake.StillTheCurrentDrop(then.Generation, _dropGeneration);

    /// <summary>
    /// Embed the pictures a drop plan chose, as ONE insertion into whichever view is
    /// active — through InsertMarkdownFragment, the path Insert ▸ Picture takes, so a
    /// dropped photo.png and a picked photo.png land as the same markdown at the
    /// caret. <paramref name="bytesOf"/> fetches a picture's bytes by its index in
    /// the plan (a path read, or the bytes the editor already sent). A read that
    /// fails inserts none of the pictures, with Insert ▸ Picture's message.
    /// </summary>
    /// <param name="then">What the plan was decided against, taken before the
    /// caller's first await. Fetching the bytes is itself an await, so the document
    /// can move between the plan and the insertion here too; this is the chokepoint
    /// both routes' insertions go through, so the last word on whether the insertion
    /// still applies is taken here.</param>
    /// <returns>
    /// Whether the drop is still the window's to act on
    /// (<see cref="DropHandshake.CallerFinishesTheDrop"/>). False means the drop was
    /// abandoned and the status line already says so — so the CALLER must stop too.
    /// This used to be void, and Window_Drop carried on: it opened documents against
    /// the stale plan and then flashed <c>plan.Notice()</c>, which ASSIGNS, straight
    /// over the abandonment notice. The content route returns on the same condition,
    /// so both routes now tell the user the same thing about the same failure. A
    /// failed READ is not this: the modal has spoken, the drop is still live, and
    /// the caller carries on as it always has.
    /// </returns>
    /// <remarks>
    /// The ORDER of the gates, what each arm says, and which arms stop the caller
    /// are <see cref="DropHandshake.DecideInsert"/>, <see cref="DropHandshake.StatusFor"/>
    /// and <see cref="DropHandshake.CallerFinishesTheDrop"/> — pure, and pinned
    /// arm by arm. They were statements here, in a member no test can reach without
    /// a window and a real mouse, and F-D measured what that cost: before the lift,
    /// forcing every `return false` in this method to `true` passed the whole suite,
    /// and so did flipping the empty-plan early return to `false` — which would have
    /// stopped every dropped markdown file from opening, on both surfaces. What is
    /// left here is the fetching of the bytes and the acting-out.
    /// </remarks>
    private async Task<bool> InsertDroppedPicturesAsync(
        DropPlan plan, Func<int, Task<byte[]>> bytesOf,
        (string? Path, string Clean, DropTarget Target, long View, long Generation) then)
    {
        // Asked before the bytes are fetched — and, in DecideInsert, before the
        // empty-plan arm: a drop that wants no pictures can still open a document,
        // and that open belongs to the drop the user has already replaced. This is
        // the case AbandonDroppedRead cannot reach — a path-route drop starting
        // after CompleteDroppedFileRead has cleared the waiter but before this
        // continuation runs.
        var currentAtEntry = DropIsStillCurrent(then);
        var fragments = new List<string>(plan.Insert.Count);
        Exception? readFailure = null;
        // Nothing is fetched for a drop that has already lost the window, or for a
        // plan that chose no pictures; both are decided below on these same values.
        if (currentAtEntry && plan.Insert.Count > 0)
        {
            try
            {
                foreach (var picture in plan.Insert)
                {
                    var name = plan.Files[picture.Index].Name;
                    var bytes = await bytesOf(picture.Index);
                    // What goes into the document is what actually arrived, so that is
                    // what is measured — the plan's ceiling and format came from a head
                    // and a size that are a sniff or a round trip old. The case the drop
                    // widened its sharing mode for (a screenshot tool still flushing the
                    // PNG you dragged in) is the very case where the file changes in
                    // between, and a truncated picture embeds as a data URI no viewer can
                    // decode, silently. The size the plan routed on is passed in because
                    // the signature alone cannot see a truncation BELOW it: eight bytes
                    // of PNG magic still sniff as a PNG. Both routes state a size — a
                    // path from the handle it read the head through, the editor from
                    // File.size — and -1 (neither said) skips only the length half.
                    if (!DropRouting.PictureSurvivedTheRead(bytes, picture.Mime, plan.Files[picture.Index].Size))
                        throw new IOException($"{name} changed while it was being read — nothing was inserted.");
                    fragments.Add(DropRouting.PictureMarkdown(name, picture.Mime, bytes));
                }
            }
            // One file that could not be read, not a document that moved. None of
            // the pictures go in — the all-or-nothing a failed path read has always
            // had — and the drop itself is still live.
            catch (Exception ex) { readFailure = ex; }
        }

        // The two gates that belong AFTER the read are evaluated here whatever
        // happened above, and DecideInsert consults them only on the arms they
        // belong to: both are plain reads of window state with nothing to undo, and
        // every earlier arm is reached without either of them mattering.
        //
        // DropIsStillCurrent is asked a SECOND time because fetching the bytes is an
        // await, and for the path route that is the ask that matters: its
        // DropFiles.ReadAllAsync has no waiter a newer drop can complete, and two
        // drops about the same unchanged document match on path, baseline, target
        // and view, so only the generation separates them. DropStillApplies is the
        // last word on the document itself — including one that has since CLOSED,
        // which nothing downstream tests for.
        var outcome = DropHandshake.DecideInsert(
            currentAtEntry, plan.Insert.Count, readFailure is not null,
            DropIsStillCurrent(then), DropStillApplies(then));

        if (DropHandshake.StatusFor(outcome) is { } notice) FlashStatus(notice);
        if (outcome == DropInsertOutcome.ReadFailed)
            MessageBox.Show(this, $"Couldn't read the image:\n{readFailure!.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        if (outcome == DropInsertOutcome.Insert) InsertMarkdownFragment(DropRouting.Markdown(fragments));
        return DropHandshake.CallerFinishesTheDrop(outcome);
    }

    /// <summary>
    /// A drop on the formatted view, which arrives as content: web content never
    /// sees a dropped file's path, so the editor has to read the bytes.
    ///
    /// In two phases, and the second one is the point. The editor posts only each
    /// file's name, size and first bytes; this routes on that and then asks for the
    /// full bytes of the one or two files the plan chose. Reading everything up
    /// front — which is what this did — turned an accidentally-dropped 200 MB video
    /// into a base64 copy of itself in the editor, another in the web message, and a
    /// third decoded here, before anything had decided it was not even a picture.
    ///
    /// Pictures then embed through the same path as Window_Drop's; a markdown file
    /// opens as an untitled document named after the file — one per drop, since
    /// there is no path to hand a new window (the plan names any others).
    /// </summary>
    private async void HandleDroppedFiles(DropMessage message)
    {
        // The high-water mark, before anything else. A fileDrop message does NOT
        // necessarily arrive in drop order: reading 32 bytes of each of 20 files
        // takes longer than reading one, so a 20-file drop 1 can post after a 1-file
        // drop 2. Handling drop 1 then cancelled drop 2's read — the newest drop, the
        // one the user is looking at — and waited on an editor whose counter had
        // already moved past drop 1, which answers all-null. (DropHandshake.)
        //
        // And it is said, not swallowed. This drop is the one being discarded, and
        // HELP promises that dropping again says the earlier drop was replaced — a
        // promise the OTHER ordering kept (AbandonDroppedRead below) and this one
        // did not: the user saw a drop land on the page and absolutely nothing
        // happen. The wording is right either way round; "this one" is whichever
        // drop is being dropped.
        if (DropHandshake.IsSuperseded(_newestDrop, message.Drop))
        {
            FlashStatus(DropHandshake.SupersededNotice);
            return;
        }
        _newestDrop = message.Drop;

        // This drop owns the window now, so whatever read the last one left
        // outstanding ends HERE — before the plan, and before knowing whether this
        // drop wants any bytes at all. A drop that wants nothing (a .zip after a
        // photo) used to leave the photo's read live, to be answered all-null and
        // reported as a failure of a drop the user had already replaced. Said now,
        // synchronously, rather than from the abandoned await: a drop that finishes
        // without a round trip would otherwise have its own status line overwritten
        // by this one.
        if (AbandonDroppedRead()) FlashStatus(DropHandshake.SupersededNotice);

        var files = message.Files;
        // Pinned before the request goes out and checked after it comes back. The
        // await below is bounded by ReadTimeout, not by anything the user is stopped
        // from doing in the meantime: there is no busy overlay, so File ▸ Open,
        // File ▸ New, File ▸ Close, Edit ▸ Read Only, Ctrl+E and a drop on the
        // toolbar are all reachable for however long it lasts — 10 s at the floor,
        // 90 s for ten pictures at the ceiling, and the ten-minute clamp for about
        // 74 of them (the ceiling is per picture; nothing caps the count) or for a
        // stated size no honest drop produces.
        // After the supersede check above, so an out-of-order fileDrop that is NOT
        // the newest drop does not claim the window on its way out.
        var then = DropPin(++_dropGeneration);
        var plan = DropRouting.Plan(files, then.Target, oneDocument: true);
        // The chosen pictures AND the one document that opens: this route has no
        // path, so the document's bytes have to come from the editor too.
        var wanted = plan.Insert.Select(p => p.Index).Concat(plan.Open.Take(1)).ToList();

        var reply = await RequestDroppedBytesAsync(message.Drop, files, wanted);
        // A newer drop landed while this one was reading; that drop owns the window,
        // and it has already said so.
        if (reply.Outcome == DropReply.Discard) return;

        // The document the plan was made for is not necessarily the one on screen
        // now. Checked before ANY of the outcomes are acted on, because none of them
        // is about this document any more: a picture would go into a file that never
        // received the drop (or into a closed one, which nothing downstream tests
        // for), a dropped .md would replace a document the user has since opened, and
        // "Couldn't read a.png" would be said about a drop that is no longer live.
        if (!DropStillApplies(then)) { FlashStatus(DropHandshake.DocumentChangedNotice); return; }

        if (reply.Outcome == DropReply.Apply)
        {
            // Every index asked for has bytes here (that is what Apply means); the
            // TryGetValue is the belt-and-braces that keeps the all-or-nothing a
            // failed path read has always had.
            // Same answer as Window_Drop's: false means the document moved under the
            // insertion and the notice is already on screen, so neither the open
            // below nor plan.Notice() may speak over it.
            if (!await InsertDroppedPicturesAsync(plan, i => reply.Bytes.TryGetValue(i, out var b)
                ? Task.FromResult(b)
                : Task.FromException<byte[]>(new IOException($"{files[i].Name} could not be read.")), then)) return;

            if (plan.Open.Count > 0 && reply.Bytes.TryGetValue(plan.Open[0], out var content))
                await HandleDroppedContentAsync(files[plan.Open[0]].Name, content);
        }

        if (plan.Notice() is { } notice) FlashStatus(notice);
        // Last, so it is what stays on screen: the notice is about files that were
        // never going to be taken, these are about ones that should have been.
        if (reply.Outcome == DropReply.Refuse)
            FlashStatus(DropHandshake.UnreadableNotice([.. reply.Missing.Select(i => files[i].Name)]));
        else if (reply.Outcome == DropReply.TimedOut)
            FlashStatus(DropHandshake.TimedOutNotice);
    }

    // Phase two of a formatted-view drop. One read is in flight at a time — a drop
    // is a single user gesture — and the editor's drop counter says which drop an
    // answer is about, so a reply that arrives after a newer drop has started is
    // discarded rather than inserted into the wrong document. DropHandshake holds
    // both decisions and is where they are tested; what is here is the state they
    // are made from.
    private TaskCompletionSource<DropReplyDecision>? _droppedBytes;
    private long _droppedBytesDrop;
    private IReadOnlyList<int> _droppedBytesIndices = [];

    /// <summary>
    /// The highest drop number handled so far, so a fileDrop message that overtook a
    /// newer one is ignored rather than made to win.
    ///
    /// Reset to 0 when the page loads, because the counter it tracks restarts there:
    /// dropSeq is a module variable in main.js, so any reload of the editor (a
    /// re-Navigate, a WebView2 process failure and recovery) numbers the next drop 1
    /// again. A mark left where the previous page finished would make that drop — and
    /// every drop after it, for the life of the window — look older than one already
    /// handled, and every one of them would be discarded in silence. Today the page
    /// is navigated once and nothing handles ProcessFailed, so the reset is unreachable
    /// and cheap; it is here so that adding either does not quietly kill drag and
    /// drop. (A read left outstanding by the vanished page is a separate thing, and it
    /// already ends itself: the wait in RequestDroppedBytesAsync is bounded.)
    /// </summary>
    private long _newestDrop;

    /// <summary>
    /// End whatever read is outstanding, without waiting for it: its drop is no
    /// longer the one on screen, and its continuation would insert into a document
    /// the new drop is about to change. Returns true when there was one, so the
    /// caller can say so.
    /// </summary>
    private bool AbandonDroppedRead()
    {
        var pending = _droppedBytes;
        _droppedBytes = null;
        // 0 is "no read outstanding", which is also what makes a late answer to the
        // read just abandoned — or a second answer to one already taken — match
        // nothing in DropHandshake.Decide.
        _droppedBytesDrop = 0;
        _droppedBytesIndices = [];
        if (pending is null) return false;
        // A result, not a cancellation: the waiter's job is to return without
        // touching the window, which is a decision rather than an exception.
        pending.TrySetResult(DropHandshake.Discarded);
        return true;
    }

    /// <summary>
    /// Ask the editor for the full bytes of <paramref name="indices"/> from drop
    /// <paramref name="drop"/>, and wait — for at most
    /// <see cref="DropHandshake.ReadTimeout"/> — for the answer.
    /// </summary>
    private async Task<DropReplyDecision> RequestDroppedBytesAsync(
        long drop, IReadOnlyList<DroppedFile> files, IReadOnlyList<int> indices)
    {
        // Nothing to fetch: don't round-trip, and don't leave a waiter behind for an
        // answer that will never come. (The previous read is already over — the
        // caller ended it before the plan was even made.)
        if (indices.Count == 0) return DropHandshake.NothingToRead;

        // A drop number this editor never issued (0 — the message did not say). Its
        // answer would carry the same 0, which matches no outstanding read, so waiting
        // for it could only ever end in the timeout. Refuse it at once instead.
        if (!DropHandshake.CanBeAnswered(drop)) return DropHandshake.Unreadable(indices);

        // No editor, no answer — say so now rather than wait forever for a message
        // nothing will send. (A fileDrop can only have come FROM the editor, so this
        // is belt and braces.)
        if (Web.CoreWebView2 is null) return DropHandshake.Unreadable(indices);

        var pending = new TaskCompletionSource<DropReplyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _droppedBytes = pending;
        _droppedBytesDrop = drop;
        _droppedBytesIndices = indices;

        // Set before the request goes out, so an answer that comes back faster than
        // ExecuteScriptAsync returns still finds its waiter.
        await RunEditorAsync($"window.MDM.readDroppedFiles({drop}, {JsonSerializer.Serialize(indices)})");

        // Bounded. The editor answers on every path it can see, and posts a small
        // failure message when its own post of the bytes is refused — but if the
        // bridge itself is gone, neither post arrives and there is nothing on the
        // editor side left to tell us. This is the only thing that ends that wait.
        var timeout = DropHandshake.ReadTimeout(DropHandshake.BytesRequested(files, indices));
        if (await Task.WhenAny(pending.Task, Task.Delay(timeout)) == pending.Task) return await pending.Task;
        // The delay won the race; if the answer landed in the same turn anyway, take
        // it rather than throw away a good read over a tie.
        if (pending.Task.IsCompleted) return await pending.Task;

        // Give the window back. The read is abandoned so a late answer finds no
        // waiter and is discarded, rather than inserted into whatever is on screen by
        // then. (Only if it is still OURS: a newer drop would already have ended it,
        // and that path completes pending.Task above rather than reaching here.)
        if (ReferenceEquals(_droppedBytes, pending)) AbandonDroppedRead();
        return DropHandshake.TimedOut(indices);
    }

    /// <summary>The editor's answer to <see cref="RequestDroppedBytesAsync"/>. What
    /// it amounts to is <see cref="DropHandshake.Decide"/>'s to say; an answer about
    /// any drop but the one being waited on leaves the waiter alone.</summary>
    private void CompleteDroppedFileRead(DropBytesMessage answer)
    {
        if (_droppedBytes is not { } pending) return;
        var decision = DropHandshake.Decide(_droppedBytesDrop, answer.Drop, _droppedBytesIndices, answer);
        if (decision.Outcome == DropReply.Discard) return;
        _droppedBytes = null;
        _droppedBytesDrop = 0;
        _droppedBytesIndices = [];
        pending.TrySetResult(decision);
    }

    /// <summary>
    /// Opens a file dropped onto the formatted view. Web content can't see the file
    /// path, so this loads the dropped bytes as an untitled document named after the
    /// file (Save will prompt for a location).
    /// </summary>
    private async Task HandleDroppedContentAsync(string name, byte[] bytes)
    {
        // The container is sniffed by content, as OpenPathAsync sniffs it — but the
        // password prompt lives on the path route, and this route has no path to
        // reopen from, so this isn't a prompt, it's a redirect.
        if (Secure.SecureUi.IsEncryptedPath(name) || Secure.SecureMarkdownFormat.LooksLikeContainer(bytes))
        {
            MessageBox.Show(this,
                "Encrypted documents can't be opened by dropping them here — use " +
                "File \u25b8 Open or double-click the file instead.",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!await ConfirmDiscardAsync()) return;
        ShowBusy($"Opening {name}…");
        try
        {
            StopWatching();
            _suppressDirty = true;
            // Same reason as LoadDocumentAsync: the document being replaced was just
            // saved or explicitly discarded, so its crash copy goes with it. This path
            // builds the document by hand instead of going through LoadDocumentAsync,
            // so it needs saying twice.
            DiscardBackup();
            _backupDirty = false;
            await ApplyDocBaseAsync(null); // dropped content has no folder context
            // The editor hands the file's bytes over, so its line ending and
            // byte-order mark are detected exactly as File ▸ Open detects them, and
            // both are the convention the eventual Save As writes back.
            var dropped = DocumentText.Detect(bytes);
            await SetDocumentMarkdownAsync(dropped.Text);
            _lineEnding = dropped.Ending;
            _hadBom = dropped.HadBom;
            _currentPath = null;
            ReleaseDocumentClaim();   // the file this window showed is no longer open here
            _displayName = name;
            _suppressDirty = false;
            // Dropped content exists nowhere but in this window — there is no file to
            // compare it against and nothing to reopen if it's lost. Treat it as
            // unsaved from the outset: closing prompts, and the crash copy actually
            // covers it. Baselining it as "clean" made both of those silently skip it.
            _cleanMarkdown = string.Empty;
            _diskBaseline = string.Empty;   // no file, so no disk baseline either
            _dirty = true;
            _backupDirty = true;
            UpdateTitle();
            SetClosed(false);
            await FocusDocumentAsync();
        }
        // _suppressDirty here too: a throw partway would otherwise leave it stuck,
        // freezing dirty tracking and backups for the rest of the session.
        finally { HideBusy(); _suppressDirty = false; }
    }

    /// <summary>Returns whether the new process actually started — WhatsNew_Click
    /// needs to know before it records the changelog as seen.</summary>
    private static bool OpenInNewInstance(string path, bool readOnly = false, bool helpWindow = false)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var flags = helpWindow ? " --help-window" : (readOnly ? " --readonly" : "");
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"\"{path}\"{flags}") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open a new window:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    // ===== Read-only mode =====

    private void ReadOnly_Click(object sender, RoutedEventArgs e)
    {
        if (MenuReadOnly.IsChecked) { SetReadOnly(true); return; }
        // Turning it OFF. Read-only the already-open fallback imposed is not lifted
        // by the checkbox: this window holds no claim on the document, so it claims
        // it first, and only a claim that is now this window's (or nobody's) may
        // edit - otherwise the un-check would hand out an unguarded editor, and a
        // double-click on the file a second one. Still held by a live window
        // elsewhere: the checkbox goes back, and that window is brought forward
        // if it can be (the fallback exists because that focus failed once already).
        if (!_imposedReadOnly.Imposed) { SetReadOnly(false); return; }
        var claim = _currentPath is null
            ? new Instances.OpenGuardDecision(Instances.OpenVerdict.Proceed, 0, 0)   // the held document is already gone from here
            : ClaimDocument(_currentPath);
        switch (_imposedReadOnly.Lift(claim))
        {
            case Instances.ReadOnlyLift.Edit:
                SetReadOnly(false);
                break;
            case Instances.ReadOnlyLift.StayReadOnly:
                SetReadOnly(true);   // the checkbox goes back
                Instances.OpenGuard.TryFocusWindow(claim.HolderHwnd);
                FlashStatus("Still open in another window");
                break;
        }
    }

    private void SetReadOnly(bool on)
    {
        _readOnly = on;
        MenuReadOnly.IsChecked = on;
        SourceBox.IsReadOnly = on;
        _findDialog?.SetReadOnly(on);
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

    // ===== Help & What's New (open a bundled doc read-only in a new instance) =====

    /// <summary>
    /// Refresh an embedded doc onto disk and open it read-only in a new instance.
    /// Shared by Help and What's New — same shape, different resource. Returns
    /// whether the reader window actually launched, which is what tells
    /// <see cref="OpenWhatsNew"/> whether marking the changelog "seen" would be
    /// honest.
    /// </summary>
    private static bool OpenEmbeddedReaderDoc(string resourceName, string fileName)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkdownMidget", fileName);

            // Always restore the canonical text from the embedded copy, then mark the
            // file read-only so it's harder to overwrite by accident.
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            var asm = Assembly.GetExecutingAssembly();
            using (var stream = asm.GetManifestResourceStream(resourceName))
            using (var file = File.Create(path))
                stream!.CopyTo(file);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            return OpenInNewInstance(path, helpWindow: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {fileName}:\n{ex.Message}", "Markdown Midget",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        // Guards F1 as well as the menu item — F1 is a raw keybinding (see
        // RegisterShortcuts) with no IsEnabled to disable, so MenuViewHelp.IsEnabled
        // = false alone does not stop it. Without this, F1 pressed inside an
        // already-open Help or What's New window spawns another reader window —
        // exactly the "no help-of-help" case OpenWhatsNew already guards against.
        if (_isHelpWindow) return;
        OpenEmbeddedReaderDoc("HELP.md", "HELP.md");
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) => OpenWhatsNew();

    // MouseLeftButtonUp rather than Click — the mascot is an Image, not a button.
    private void Mascot_Click(object sender, MouseButtonEventArgs e) => OpenWhatsNew();

    private void OpenWhatsNew()
    {
        // The mascot is always clickable — unlike MenuWhatsNew, WPF doesn't disable
        // it for a help/changelog window, so the "no help-of-help" rule has to be
        // enforced here or clicking the mascot INSIDE the changelog viewer spawns
        // another one of itself.
        if (_isHelpWindow) return;

        // Gated on the reader window actually coming up. A failed launch — disk full,
        // the folder locked, whatever OpenEmbeddedReaderDoc already put in a message
        // box — must not silently clear a badge for something the user never got to
        // see; that would be the same completion-marker mistake as writing a "done"
        // stamp before the work it describes actually finished.
        if (OpenEmbeddedReaderDoc("CHANGELOG.md", "CHANGELOG.md"))
            MarkChangelogSeen();
    }

    private void MarkChangelogSeen()
    {
        _lastSeenChangelogVersion = AppVersion;
        // SavePersistentField, not SaveSettings — the same reason SetThemeKey uses
        // it: an unrelated toggle later in a DIFFERENT window, still holding this
        // window's PRE-open value in memory, must not republish it and bring the
        // badge back for a changelog the user genuinely already read.
        SavePersistentField(s => s.LastSeenChangelogVersion = AppVersion);
        WhatsNewBadge.Visibility = Visibility.Collapsed;
    }

    /// <summary>Show or hide the mascot's unseen-changelog badge for THIS window.
    /// Other already-open windows don't update live — the same limitation the theme
    /// and settings menus already have, and for the same reason: each window is its
    /// own in-memory snapshot of settings.json, refreshed only at its own launch.</summary>
    private void UpdateWhatsNewBadge() => WhatsNewBadge.Visibility =
        Updates.WhatsNewState.HasUnseenChangelog(AppVersion, _lastSeenChangelogVersion)
            ? Visibility.Visible : Visibility.Collapsed;

    private void About_Click(object sender, RoutedEventArgs e)
    {
        // Hand over this window's place so an update started from the dialog can
        // reopen the same document in the same view after its restart (the same
        // arguments as Apply-update: RelaunchArguments) - and the claim on the
        // document goes with it, or the restart would find this window still
        // holding the file (see StartHandingOffDocument).
        new AboutDialog(RelaunchArguments(_currentPath, _readOnly, _imposedReadOnly, _sourceMode),
                        hasApplyMenu: !_isHelpWindow, restart: StartHandingOffDocument) { Owner = this }.ShowDialog();
    }

    // ===== Help ▸ Apply vX.Y.Z update =====
    //
    // The situation: another window updated the installed copy, so the exe at the
    // canonical install path is newer than the one THIS process is executing (the
    // running image was renamed to .old by the swap; GetModuleFileName keeps
    // reporting the original path — the load-bearing Win32 asymmetry the 0.6.4 work
    // established). The fix is not an update at all: just relaunch from the same
    // path, which now yields the new version.
    //
    // Detection is a file-version read on Help-menu open — no timer, no registry.
    // The menu is the one place the user goes looking for exactly this, and reading
    // one FileVersionInfo on submenu-open costs nothing perceptible. Installed mode
    // only, and not because portable was forgotten: a portable instance's own path
    // still holds its own old exe, so this comparison is always-false there. The
    // portable signal (a marker written by the updater) is designed in ROADMAP.md —
    // "Decided 2026-08-13" — and lands separately.
    private void HelpMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuApplyUpdate.Visibility = Visibility.Collapsed;
        if (_isHelpWindow) return;                    // a transient viewer relaunching itself is clutter
        try
        {
            if (!Updates.UpdateService.IsInstalled()) return;
            var onDisk = Updates.UpdateService.VersionOnDisk();
            var running = Updates.UpdateVersion.Parse(AppVersion);
            if (!Updates.UpdateOffer.NeedsRestartNotUpdate(onDisk, wanted: null, running)) return;

            // No literal-underscore risk: version strings are digits, dots and
            // dashes, so no Replace("_","__") is needed here.
            MenuApplyUpdate.Header = $"Apply v{onDisk} _Update";
            MenuApplyUpdate.ToolTip =
                $"v{onDisk} is already installed (another window updated). " +
                "Reopens this window using the new version — nothing is downloaded.";
            MenuApplyUpdate.Visibility = Visibility.Visible;
        }
        catch { /* a failed version read just means no menu item this time */ }
    }

    private async void ApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        // Re-derive at click time: the menu text was computed when the menu opened,
        // and the disk is not frozen between then and now.
        Updates.UpdateVersion? onDisk;
        try
        {
            onDisk = Updates.UpdateService.VersionOnDisk();
            var running = Updates.UpdateVersion.Parse(AppVersion);
            if (!Updates.UpdateOffer.NeedsRestartNotUpdate(onDisk, wanted: null, running))
            {
                FlashStatus("Nothing to apply — this window is already on the newest installed version.");
                MenuApplyUpdate.Visibility = Visibility.Collapsed;
                return;
            }
        }
        catch (Exception ex)
        {
            FlashStatus("Couldn't read the installed version: " + ex.Message);
            return;
        }

        // Same contract as closing the window: save / discard / cancel. Asked FIRST,
        // because the new instance must not start until the user has committed to
        // this window going away — a cancel after Process.Start would leave two
        // windows showing the same document.
        if (!await ConfirmDiscardAsync()) return;

        var exe = Updates.UpdateService.CurrentExePath;   // canonical path → the NEW exe
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // Reopen what this window holds, the way this window holds it: the document
        // path and the view flags (RelaunchArguments) - read-only only if it is the
        // user's own, not what the already-open fallback imposed.
        foreach (var arg in RelaunchArguments(_currentPath, _readOnly, _imposedReadOnly, _sourceMode))
            psi.ArgumentList.Add(arg);

        try
        {
            StartHandingOffDocument(() => Process.Start(psi));
        }
        catch (Exception ex)
        {
            // Start failed, so this window stays — with _dirty untouched. Clearing
            // it before this point would leave a window holding an unsaved buffer
            // behind a clean flag: the next close would skip the prompt and the
            // work would be gone. The user's "discard" answer applied to a restart
            // that never happened, so the question is honestly re-askable.
            MessageBox.Show(this, $"Couldn't start v{onDisk}:\n{ex.Message}",
                "Markdown Midget", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Only now, with the replacement window genuinely started: asked and
        // answered, so MainWindow_Closing must not re-prompt.
        _dirty = false;
        Close();
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

    /// <summary>
    /// Is <paramref name="current"/> — what the visible surface holds — still the
    /// document as last opened or saved? In the formatted view that is the editor's
    /// own serialisation and nothing else. In the source view the box may be showing
    /// the FILE's spelling instead (see <see cref="SourceText"/>), which is the same
    /// saved state in different words, so it counts as unmodified too; without this
    /// the title gains a `*`, a crash snapshot is written and the close prompt fires
    /// the moment Ctrl+E is pressed on an untouched file the editor normalises.
    /// </summary>
    private bool IsUnmodifiedText(string current) =>
        _sourceMode
            ? SourceText.IsUnmodified(current, _cleanMarkdown, _diskBaseline)
            : string.Equals(current, _cleanMarkdown, StringComparison.Ordinal);

    private async Task UpdateDirtyAsync()
    {
        if (_suppressDirty) return;
        // A failed read must not be mistaken for an empty document: that would clear
        // the modified flag, and an unmodified document is one the app throws away
        // without asking — taking the crash copy with it. Keep the last known state.
        var current = await TryGetDocumentMarkdownAsync();
        if (current is null) return;
        var dirty = !IsUnmodifiedText(current);
        if (dirty != _dirty)
        {
            _dirty = dirty;
            UpdateTitle();
        }
        // Flag it; the snapshot itself happens on its own timer so typing never waits
        // on the disk. Set whenever content differs from the file, not only on the
        // transition, so a snapshot that failed gets retried.
        if (dirty) _backupDirty = true;
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
        // Keep the previous baseline if the editor can't be asked. Adopting "" would
        // make every later comparison read as "there are unsaved changes" — the safe
        // direction, but it also means an unnecessary prompt and an unnecessary
        // backup on every document that follows.
        //
        // Still a FRESH instance either way, with one exception: HandleExternalChangeAsync
        // detects a document swap underneath its awaits by reference-comparing this
        // field, and a drop's pin does the same, so "reassigned but identical text"
        // has to be distinguishable from "not reassigned at all". Leaving the old
        // reference in place would make a reload of the same path invisible to both.
        //
        // The exception is an EMPTY document. "" is interned: the editor's answer
        // comes back through JsonSerializer.Deserialize<string>, and the fallback
        // below is new string("".AsSpan()) — both hand back the one string.Empty, so
        // a reassignment here is invisible to a reference check. Neither caller is
        // left unguarded by it: the external-change pass still has its path half and
        // re-reads the disk before it reloads, and a drop still compares the path,
        // what the window can take, and which view it is in.
        // (AnEmptyDocumentsBaselineMovesWithoutTheReferencePinSeeingIt pins this.)
        var markdown = await TryGetDocumentMarkdownAsync();
        _cleanMarkdown = markdown ?? new string(_cleanMarkdown.AsSpan());
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
        var encrypted = _docEncrypted ? "  \U0001F512 [Encrypted]" : "";
        var readOnly = _readOnly ? "  [Read Only]" : "";
        Title = $"{(_dirty ? "*" : "")}{name}{encrypted}{readOnly}  |  {ProductDesc}";
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
