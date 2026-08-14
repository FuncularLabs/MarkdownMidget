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

    /// <summary>
    /// The Settings dialog's "Import words from Word's custom dictionary" action.
    /// One-way by design: CUSTOM.DIC is opened for READ and never written — the
    /// app's dictionary stays private, and Word's stays Word's (see
    /// <see cref="CustomDicImport"/>, which has no write method on purpose).
    /// Returns the message to show, or null when the user cancelled the picker.
    /// </summary>
    private string? ImportCustomDic(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import words from a Word custom dictionary",
            Filter = "Word custom dictionary (*.dic)|*.dic|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        // Land the picker on Word's default CUSTOM.DIC when it exists, so the
        // common case is picker → Open. A user with a differently-located or
        // renamed dictionary still has the full picker.
        try
        {
            if (File.Exists(CustomDicImport.DefaultPath))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(CustomDicImport.DefaultPath);
                dlg.FileName = Path.GetFileName(CustomDicImport.DefaultPath);
            }
        }
        catch { /* picker just opens at its default location */ }

        if (dlg.ShowDialog(owner) != true) return null;

        try
        {
            var words = CustomDicImport.ParseFile(dlg.FileName);
            if (words.Count == 0)
                return "No words found in that file — is it a Word custom dictionary?";

            var (added, known) = _spellService.ImportWords(words);
            if (added > 0)
            {
                // New words can clear existing squiggles; re-check both views now
                // rather than waiting for the next edit.
                RequestSpellCheckSoon();
                return known > 0
                    ? $"Imported {added} new word{(added == 1 ? "" : "s")} ({known} already known)."
                    : $"Imported {added} new word{(added == 1 ? "" : "s")}.";
            }
            return $"Nothing new — all {known} words were already in the dictionary.";
        }
        catch (Exception ex)
        {
            return $"Couldn't read that file: {ex.Message}";
        }
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
        if (idx >= 0 && _spellCheck && !_readOnly && _squiggles is not null)
        {
            // Take the range the ENGINE flagged, exactly as drawn. The previous
            // approach — re-tokenize with WordAt, then re-check that word alone —
            // was wrong twice over:
            //   * context-only errors (a repeated "the the") are not errors when the
            //     word is checked in isolation, so the whole spell block vanished
            //     from the menu for words that were visibly squiggled;
            //   * hand tokenization disagreed with the engine's around '-' and '\''
            //     (the engine's boundaries are context-dependent, not a fixed
            //     character class), so a squiggle on "artz" produced the target
            //     "state-of-the-artz": applying a suggestion ate the adjacent text,
            //     and Add to Dictionary stored a token the checker never matches, so
            //     the squiggle never cleared.
            hit = SpellHitTest.RangeAt(_squiggles.Ranges, idx, text.Length);
        }

        var menu = new ContextMenu();
        if (hit is { } h)
        {
            var word = text.Substring(h.Start, h.Length);
            var click = new SpellClick(h.Start, h.Start + h.Length, word, text[..h.Start]);
            foreach (var it in await BuildSpellItemsAsync(click, SourceReplace(click)))
                menu.Items.Add(it);
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
    /// contextmenu message): dynamic menu with the spelling actions + standard items.</summary>
    private async Task ShowSpellContextMenuAsync(double x, double y, SpellClick click)
    {
        var menu = new ContextMenu();
        foreach (var it in await BuildSpellItemsAsync(click, WysiwygReplace(click)))
            menu.Items.Add(it);
        menu.Items.Add(MakeItem("Cu_t", () => Cut_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MakeItem("_Copy", () => Copy_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MakeItem("_Paste", () => Paste_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("Select _All", () => SelectAll_Click(this, new RoutedEventArgs())));

        ShowMenuOverEditor(menu, x, y);
    }

    /// <summary>A right-click that landed on a flagged word. <paramref name="Before"/>
    /// is the text leading up to it, used to recognise a repeated word.</summary>
    internal sealed record SpellClick(int From, int To, string Word, string Before);

    /// <summary>
    /// Characters the checker treats as a word break. Whitespace plus the
    /// zero-width characters that routinely arrive in text pasted from Word or the
    /// web — the checker flags a repeat across those too, and missing them left the
    /// menu offering nothing actionable on a word it had squiggled.
    /// </summary>
    internal static bool IsSeparator(char c) =>
        char.IsWhiteSpace(c) || c is '\u00AD' or '\u200B' or '\u200C' or '\u200D' or '\uFEFF';

    /// <summary>
    /// The separator run between a repeated word and the one before it. Deleting a
    /// fixed single space is wrong: the checker flags repeats across a non-breaking
    /// space or several spaces, so assuming <c>' '</c> either leaves stray
    /// whitespace behind or misses the separator entirely.
    /// </summary>
    internal static string TrailingWhitespace(string before)
    {
        var end = before.Length;
        while (end > 0 && IsSeparator(before[end - 1])) end--;
        return before[end..];
    }

    /// <summary>Replace the flagged range in the source view; when
    /// <c>dropSeparator</c> is set it also swallows the whitespace before it.</summary>
    private Action<string, bool> SourceReplace(SpellClick click) => (replacement, dropSeparator) =>
    {
        // The buffer may have changed between right-click and this click (typing,
        // auto-reload) — only act if the flagged word is still exactly there.
        var now = SourceBox.Text;
        var len = click.To - click.From;
        if (click.To > now.Length || now.Substring(click.From, len) != click.Word)
        {
            RequestSpellCheckSoon();
            return;
        }
        var gap = dropSeparator ? TrailingWhitespace(click.Before).Length : 0;
        var start = Math.Max(0, click.From - gap);
        SourceBox.Select(start, click.To - start);
        SourceBox.SelectedText = replacement;   // through the undo stack
        RequestSpellCheckSoon();
    };

    /// <summary>The same for the WYSIWYG view. replaceRange re-verifies the range
    /// still holds what the menu was built for and no-ops otherwise.</summary>
    private Action<string, bool> WysiwygReplace(SpellClick click) => (replacement, dropSeparator) =>
    {
        // Deleting the separator is a POSITION operation, so the editor does it:
        // an inline leaf (image, inline HTML) takes a position without contributing
        // text, so a character count taken from `Before` drifts from the real
        // positions and the deletion would silently refuse to apply.
        _ = RunEditorAsync(dropSeparator
            ? $"window.MDM.deleteRepeated({click.From}, {click.To}, {JsLiteral(click.Word)})"
            : $"window.MDM.replaceRange({click.From}, {click.To}, {JsLiteral(replacement)}, {JsLiteral(click.Word)})");
        RequestSpellCheckSoon();
    };

    /// <summary>
    /// Build the spelling block for a flagged word.
    ///
    /// Not every flag is a misspelling: the engine also reports CONTEXT-ONLY errors,
    /// above all a repeated word. Those words are spelled perfectly, so the ordinary
    /// actions are actively harmful — the engine cheerfully suggests "them/then/they"
    /// for the second "the" in "the the" (accepting one corrupts the sentence), and
    /// Add to Dictionary would permanently whitelist a stopword, silently suppressing
    /// every later error on that word. So detect the case and offer the action that
    /// actually helps: delete the duplicate.
    /// </summary>
    private async Task<List<object>> BuildSpellItemsAsync(SpellClick click,
        Action<string, bool> replace, bool trailingSeparator = true)
    {
        // A word that is fine on its own but flagged in context is a context error.
        // NB: this decides WHICH actions to offer — it is not a gate on showing the
        // block at all. Using it as a gate is the bug this release fixes.
        var contextOnly = (await _spellService.CheckAsync(click.Word)).Count == 0;
        var repeated = contextOnly && EndsWithRepeatOf(click.Before, click.Word);
        var items = new List<object>();

        if (repeated)
        {
            items.Add(new MenuItem { Header = $"Repeated word: {Escape(click.Word)}", IsEnabled = false });
            items.Add(new Separator());
            var del = MakeItem("_Delete Repeated Word", () => replace(string.Empty, true));
            del.FontWeight = FontWeights.SemiBold;
            items.Add(del);
            if (trailingSeparator) items.Add(new Separator());
            return items;
        }

        if (contextOnly)
        {
            // Flagged for some other contextual reason; the word itself is fine, so
            // the dictionary actions would be meaningless. Say so rather than mislead.
            items.Add(new MenuItem { Header = "(check the surrounding wording)", IsEnabled = false });
            if (trailingSeparator) items.Add(new Separator());
            return items;
        }

        var suggestions = await _spellService.SuggestAsync(click.Word);
        if (suggestions.Count == 0)
        {
            items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
        }
        else
        {
            foreach (var s in suggestions)
            {
                var item = new MenuItem { Header = Escape(s), FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => replace(s, false);
                items.Add(item);
            }
        }
        items.Add(new Separator());
        var add = MakeItem("A_dd to Dictionary", () =>
        {
            _spellService.AddToDictionary(click.Word);
            RequestSpellCheckSoon();
        });
        add.ToolTip = "Stored privately in Markdown Midget's own dictionary — the Windows dictionary is not modified.";
        items.Add(add);
        items.Add(MakeItem("_Ignore All", () =>
        {
            _spellService.IgnoreAll(click.Word);
            RequestSpellCheckSoon();
        }));
        if (trailingSeparator) items.Add(new Separator());
        return items;
    }

    /// <summary>WPF menu headers eat a single underscore as a mnemonic marker.</summary>
    private static string Escape(string header) => header.Replace("_", "__");

    /// <summary>True when the text before the flagged word ends with that same word —
    /// i.e. this occurrence is a duplicate of the one immediately before it.</summary>
    internal static bool EndsWithRepeatOf(string before, string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        var gap = TrailingWhitespace(before);
        if (gap.Length == 0) return false;                          // nothing separating them
        var trimmed = before[..^gap.Length];
        if (trimmed.Length < word.Length) return false;
        // Ordinal: the boundary arithmetic below assumes the matched suffix is exactly
        // word.Length long, which culture-sensitive matching does not guarantee.
        if (!trimmed.EndsWith(word, StringComparison.OrdinalIgnoreCase)) return false;
        // Whole word only, so "bathe the" isn't read as a repeat of "the".
        var head = trimmed.Length - word.Length;
        return head == 0 || !char.IsLetterOrDigit(trimmed[head - 1]);
    }

    private static MenuItem MakeItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

}
