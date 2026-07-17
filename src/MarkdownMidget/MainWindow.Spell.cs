using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownMidget.Spelling;

namespace MarkdownMidget;

/// <summary>
/// The app-owned spell-check stack. The engine is the Windows Spell Checking API
/// with a dictionary PRIVATE to Markdown Midget (see <see cref="SpellService"/>);
/// both views render squiggles from host-computed ranges, native spell check
/// stays off everywhere, and code is exempted structurally when the
/// "Skip Spell Check in Code" setting is on — in BOTH views.
/// </summary>
public partial class MainWindow
{
    private readonly SpellService _spellService = new();
    private readonly DispatcherTimer _spellTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _spellRunning;
    private bool _spellQueued;
    // Bumped whenever the document is replaced (open, new, auto-reload). An
    // in-flight check that started against generation N must not deliver results
    // to generation N+1 — that's how squiggles from one file land on another.
    private int _spellGeneration;
    private SquiggleAdorner? _squiggles;

    private sealed class SpellTextPayload
    {
        public string Plain { get; set; } = string.Empty;
        public List<SegDto> Segs { get; set; } = new();
    }

    private sealed class SegDto
    {
        public int PlainStart { get; set; }
        public int PmPos { get; set; }
        public int Len { get; set; }
    }

    private void InitSpell()
    {
        _spellTimer.Tick += async (_, _) => { _spellTimer.Stop(); await RunSpellCheckAsync(); };
        SourceBox.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,
            new RoutedEventHandler((_, _) => _squiggles?.InvalidateVisual()));
        SourceBox.ContextMenuOpening += SourceBox_ContextMenuOpening;
        // Our stack replaces WPF's native checking entirely.
        SourceBox.SpellCheck.IsEnabled = false;
    }

    /// <summary>Debounced entry point — safe to call on every edit.</summary>
    private void RequestSpellCheckSoon()
    {
        _spellTimer.Stop();
        _spellTimer.Start();
    }

    private async Task RunSpellCheckAsync()
    {
        if (_spellRunning) { _spellQueued = true; return; }
        _spellRunning = true;
        try
        {
            do
            {
                _spellQueued = false;
                await RunSpellCheckOnceAsync();
            } while (_spellQueued);
        }
        finally { _spellRunning = false; }
    }

    private async Task RunSpellCheckOnceAsync()
    {
        if (!_spellCheck || _closed)
        {
            ClearSquiggles();
            return;
        }
        var gen = _spellGeneration;

        if (_sourceMode)
        {
            var text = SourceBox.Text;
            var results = await _spellService.CheckAsync(text);
            if (!ReferenceEquals(SourceBox.Text, text) && SourceBox.Text != text)
                return; // text changed while checking; the pending re-run handles it
            if (_skipCodeSpell && results.Count > 0)
            {
                var code = MarkdownCodeRanges.Find(text);
                if (code.Count > 0)
                    results = results.Where(r => !code.Any(c => r.Start >= c.Start && r.Start < c.End)).ToList();
            }
            if (gen != _spellGeneration) return;   // a different document loaded meanwhile
            EnsureSquiggleAdorner();
            _squiggles?.SetRanges(results);
            return;
        }

        // WYSIWYG: extract → check → map back → decorate. getSpellText() starts the
        // edit-recording itself, so a slow round-trip gets rebased instead of
        // landing stale.
        if (!_editorReady) return;
        var json = await RunEditorAsync(
            $"window.MDM.getSpellText({(!_skipCodeSpell ? "true" : "false")})");
        if (string.IsNullOrEmpty(json)) return;

        SpellTextPayload? payload;
        try { payload = JsonSerializer.Deserialize<SpellTextPayload>(json, AnchorJson); }
        catch { return; }
        if (payload is null) return;

        var hits = await _spellService.CheckAsync(payload.Plain);
        var segments = payload.Segs
            .Select(s => new SpellSegment(s.PlainStart, s.PmPos, s.Len)).ToList();
        var ranges = SpellTextMap.MapRanges(segments, hits.Select(h => (h.Start, h.Length)));
        if (gen != _spellGeneration) return;   // a different document loaded meanwhile
        var body = string.Join(",", ranges.Select(r => $"{{\"from\":{r.From},\"to\":{r.To}}}"));
        await RunEditorAsync($"window.MDM.setSpellRanges([{body}])");
    }

    private void ClearSquiggles()
    {
        _squiggles?.SetRanges(Array.Empty<(int, int)>());
        if (_editorReady) _ = RunEditorAsync("window.MDM.setSpellRanges([])");
    }

    private void EnsureSquiggleAdorner()
    {
        if (_squiggles is not null) return;
        var layer = AdornerLayer.GetAdornerLayer(SourceBox);
        if (layer is null) return;
        _squiggles = new SquiggleAdorner(SourceBox);
        layer.Add(_squiggles);
    }

    // ---- suggestion menus ----

    /// <summary>Right-click in the source view: suggestions when over a squiggle,
    /// plus the standard edit items.</summary>
    private async void SourceBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        var pos = Mouse.GetPosition(SourceBox);
        var idx = SourceBox.GetCharacterIndexFromPoint(pos, snapToText: true);

        (int Start, int Length)? hit = null;
        var text = SourceBox.Text;
        if (idx >= 0 && _spellCheck && !_readOnly)
        {
            // Identify the word under the cursor and confirm it's misspelled — the
            // adorner's ranges may be a beat stale, so ask the words themselves.
            var (ws, we) = WordAt(text, idx);
            if (we > ws)
            {
                var word = text[ws..we];
                if (!_spellService.IsKnown(word) &&
                    (await _spellService.CheckAsync(word)).Count > 0)
                    hit = (ws, we - ws);
            }
        }

        var menu = new ContextMenu();
        if (hit is { } h)
        {
            var word = text.Substring(h.Start, h.Length);
            var suggestions = await _spellService.SuggestAsync(word);
            AddSpellItems(menu, suggestions,
                replace: s =>
                {
                    // The buffer may have changed between right-click and this click
                    // (typing, auto-reload) — replace only if the word is still here.
                    var now = SourceBox.Text;
                    if (h.Start + h.Length > now.Length ||
                        now.Substring(h.Start, h.Length) != word) { RequestSpellCheckSoon(); return; }
                    SourceBox.Select(h.Start, h.Length);
                    SourceBox.SelectedText = s;   // through the undo stack
                    RequestSpellCheckSoon();
                },
                word);
        }
        menu.Items.Add(MakeItem("Cu_t", () => SourceBox.Cut()));
        menu.Items.Add(MakeItem("_Copy", () => SourceBox.Copy()));
        menu.Items.Add(MakeItem("_Paste", () => SourceBox.Paste()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Select _All", () => SourceBox.SelectAll()));

        menu.PlacementTarget = SourceBox;
        menu.IsOpen = true;
    }

    /// <summary>WYSIWYG right-click on a misspelled word (info arrives with the
    /// contextmenu message): dynamic menu with suggestions + the standard items.</summary>
    private async Task ShowSpellContextMenuAsync(double x, double y, int from, int to, string word)
    {
        var suggestions = await _spellService.SuggestAsync(word);

        var menu = new ContextMenu();
        AddSpellItems(menu, suggestions,
            replace: s =>
            {
                // from/to are the positions from the right-click message; the doc may
                // have changed since, so replaceRange only acts if that range still
                // holds `word` (else it no-ops and the next re-check refreshes).
                _ = RunEditorAsync($"window.MDM.replaceRange({from}, {to}, {JsLiteral(s)}, {JsLiteral(word)})");
                RequestSpellCheckSoon();
            },
            word);
        menu.Items.Add(MakeItem("Cu_t", () => Cut_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MakeItem("_Copy", () => Copy_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MakeItem("_Paste", () => Paste_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Select _All", () => SelectAll_Click(this, new RoutedEventArgs())));

        ShowMenuOverEditor(menu, x, y);
    }

    private void AddSpellItems(ContextMenu menu, List<string> suggestions, Action<string> replace, string word)
    {
        if (suggestions.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
        }
        else
        {
            foreach (var s in suggestions)
            {
                var item = new MenuItem { Header = s.Replace("_", "__"), FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => replace(s);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        var add = MakeItem("A_dd to Dictionary", () =>
        {
            _spellService.AddToDictionary(word);
            RequestSpellCheckSoon();
        });
        add.ToolTip = "Stored privately in Markdown Midget's own dictionary — the Windows dictionary is not modified.";
        menu.Items.Add(add);
        menu.Items.Add(MakeItem("_Ignore All", () =>
        {
            _spellService.IgnoreAll(word);
            RequestSpellCheckSoon();
        }));
        menu.Items.Add(new Separator());
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Word boundaries around a character index (letters/digits/'/-).</summary>
    internal static (int Start, int End) WordAt(string text, int index)
    {
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';
        if (text.Length == 0) return (0, 0);
        index = Math.Clamp(index, 0, text.Length - 1);
        if (!IsWordChar(text[index]))
        {
            if (index > 0 && IsWordChar(text[index - 1])) index--;
            else return (index, index);
        }
        var start = index;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = index + 1;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return (start, end);
    }
}
