# Roadmap / Wishlist

Not-yet-committed ideas and deferred work for **Markdown Midget**. This is a
living document — items move up, down, or off as priorities shift. Shipped work
lives in [CHANGELOG.md](CHANGELOG.md).

Rough buckets: **Next** (likely soon), **Later** (wanted, not scheduled),
**Someday / Big** (real projects), **Won't unless asked** (known limits we've
deliberately parked).

---

## Someday / Big

### Own the spell-check stack (custom dictionaries + "Add to dictionary")

**Why it's big:** the WYSIWYG view uses WebView2's built-in spell checker, which
exposes **no API** to read a custom dictionary, add a word, or fetch suggestions
(verified against SDK 1.0.3179 — `CoreWebView2` and `ContextMenuTarget` have zero
spell/dictionary members). The WPF source view (`TextBox`) *does* have hooks
(`SpellCheck.CustomDictionaries`, `GetSpellingError().Suggestions`), but that's the
secondary view. So anything meaningful for the primary view means **replacing the
native spell checker in both views with our own**, built on the Windows Spell
Checking API (`ISpellChecker`).

**What it unlocks:**
- Shares the **OS custom dictionary** (same one Notepad-on-Win11 / WinUI use).
- Real **"Add to dictionary"** in both views.
- Optional **"Import from Word"** (Word/Outlook keep their own `CUSTOM.DIC` at
  `%AppData%\Microsoft\UProof\` — a plain line-per-word text file, easy to read).

**De-risk spike (do this first):** prove that `ISpellChecker` results can be mapped
back to editor positions and rendered as squiggles on the *actual* editor content —
- WYSIWYG: host sends misspelled ranges → a ProseMirror decoration draws underlines.
- Source: custom adorners on the `TextBox`.
Confirm performance on a large doc (debounce, incremental re-check) before committing
to the full build (suggestion menu, add-to-dictionary, dictionary management UI).

**Effort:** multi-day for the full feature; ~half a day for the spike.
**Risk:** position mapping + perf on large docs; owning what the platform gave free.

### Multi-document tabs

Tabbed editing with `Ctrl+Tab` / `Ctrl+Shift+Tab` and `Ctrl+PgUp` / `Ctrl+PgDn`
to switch (a reflex several testers have). Currently one document per window.

---

## Later

- **Find & Replace.** Find is done (4 modes, F3/Shift+F3); add the Replace tab —
  Replace / Replace All, scoped to selection, honoring the current search mode.
- **.NET 8 build + portable self-contained build.** The multi-target plan (net8 /
  net10 / portable ~63 MB) is scoped and the code already compiles for net8; just
  needs the csproj multi-target + extra publish profiles + release-workflow matrix.
- **Editor bundle lazy-load.** Mermaid pulled the bundle from ~560 KB to ~3.9 MB
  (exe 2.9 → 6.4 MB). Code-split Mermaid so it loads only when a `mermaid` block
  is present — switches esbuild to ESM chunks + adapts the HTML/extraction.

---

## Won't unless asked (known limits, parked deliberately)

- **Code-aware spell check in the *source* view.** WPF `TextBox` spell check is
  all-or-nothing per control; exempting code ranges would mean a `RichTextBox`
  rewrite with custom squiggle rendering. The WYSIWYG view already skips code
  (View ▸ Skip Spell Check in Code); source view stays all-or-nothing.
- **Chromium `Custom Dictionary.txt` file-poke.** WebView2 reads a custom-dict text
  file in its user-data folder; we *could* write to it, but it's undocumented,
  needs a WebView reload to take effect, and could break any release. Rejected in
  favor of the `ISpellChecker` route above.
