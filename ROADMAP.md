# Roadmap / Wishlist

Not-yet-committed ideas and deferred work for **Markdown Midget**. This is a
living document — items move up, down, or off as priorities shift. Shipped work
lives in [CHANGELOG.md](CHANGELOG.md).

Rough buckets: **Next** (likely soon), **Later** (wanted, not scheduled),
**Someday / Big** (real projects), **Won't unless asked** (known limits we've
deliberately parked).

---

## Next

_Nothing queued right now._

Recently shipped from here: autosave / crash recovery (see CHANGELOG), which was
the last of the usability gaps raised in the 0.6.x review.

---

## Someday / Big

### Real installer / uninstaller

A proper signed installer that behaves like software users expect on Windows —
**not** the current one-off "Register as .md editor" AppData-copy flow.

**Hard requirements (from user feedback):**

1. **Shows up in Add/Remove Programs** (`appwiz.cpl` / Settings ▸ Apps &
   Features) with a real entry — name, publisher (Funcular Labs, Inc.), version,
   size, icon — and a working **Uninstall** that cleans up the exe, shortcuts,
   the `.md` file association/ProgID, and (with confirmation) user data. The
   custom register/unregister dialogs don't satisfy this; users expect the OS
   uninstall UI. This is the ARP `Uninstall` registry key
   (`HKCU\…\Uninstall\MarkdownMidget` for per-user, or `HKLM` for machine-wide)
   that MSI/MSIX populate automatically.
2. **Portable stays a first-class option.** The plain single-file exe must remain
   downloadable from the GitHub releases page and fully usable with **no
   installer** — run-from-anywhere, no ARP entry, no registry footprint beyond
   what the user opts into via *Register as .md editor*. We ship BOTH: an
   installer artifact AND the bare exe. Nobody is forced through the installer.

**Approach candidates:** WiX (MSI — full control over ARP, associations,
per-user vs per-machine, upgrade codes) vs MSIX (store-style servicing + clean
uninstall, but packaging constraints around WebView2 and the file-association /
"Open with default" UX are worth a spike before committing). Either way the
release workflow gains a second artifact alongside the portable exe, and both
must be Authenticode-signed.

**Interplay with the in-app updater (Help ▸ About, 0.6.0):** the updater is the
bridge until this lands, and afterward the two must not fight. An installed
(MSI/MSIX) copy should update through the installer's own servicing channel (or
the updater should detect the MSI/MSIX install and hand off / no-op), while the
portable copy keeps using the in-app updater's swap. Detecting "how was I
installed?" is part of this work. Share the swap/registration logic rather than
duplicating it.

Not started — parked deliberately until the update flow has proven itself in the
wild. Likely wants its own de-risk spike (MSIX + WebView2 + file associations)
before scoping.

### Selectable CSS templates / themes

One hard-coded stylesheet drives the editing surface today (`editor-src/styles/editor.css`,
built into the bundle), plus a separate `@media print` block. Let a user pick a
look instead — and, further out, supply their own.

Shape to think through when it's picked up:

- **Two surfaces, not one.** The editing view and the print/PDF output are styled
  independently today. Decide whether a template covers both (probably yes, with a
  print section) or whether they stay separate choices.
- **Built-ins first**, e.g. the current Nord-ish default, a light/paper theme, a
  high-contrast one, and something book-like for long prose. Shipping several
  proves the seams are in the right place before any user-supplied CSS is allowed.
- **User templates** would live beside the other user data
  (`%LocalAppData%/MarkdownMidget/themes/*.css`) and appear in the picker
  automatically. Loading arbitrary CSS into the WebView is far less dangerous than
  arbitrary HTML/JS, but it is still injection into the editor's document — decide
  whether to sanitise (e.g. strip `@import`, `url()` pointing off-disk, and
  `behavior`/`expression` legacy vectors) or to accept it as "your own machine,
  your own CSS" with a clear warning. The existing DOMPurify posture for raw HTML
  is the precedent to be consistent with.
- **Interaction with what already styles things**: the Document Width modes
  (portrait/landscape/full), the print prefs (header/footer, colour code blocks),
  the spell-check squiggle class, and the mermaid/Prism rules all live in that same
  stylesheet. A template must not be able to break the squiggles or the formatting
  marks — so split the sheet into "chrome the app depends on" and "the themeable
  part" before exposing it.
- Persist the choice next to the other settings, and expose it in the new
  **File ▸ Settings…** dialog rather than adding another menu.

### Multi-document tabs

Tabbed editing with `Ctrl+Tab` / `Ctrl+Shift+Tab` and `Ctrl+PgUp` / `Ctrl+PgDn`
to switch (a reflex several testers have). Currently one document per window.

---

## Later

- **Spell check follow-ups** (the 0.5.0 stack shipped en-US only, app-private
  dictionary): language selection, and an optional one-way "import words from
  Word's CUSTOM.DIC" — import only, never write back. Sharing the OS dictionary
  was considered and deliberately rejected as too risky.

- **Find & Replace.** Find is done (4 modes, F3/Shift+F3); add the Replace tab —
  Replace / Replace All, scoped to selection, honoring the current search mode.
- **.NET 8 build + portable self-contained build.** The multi-target plan (net8 /
  net10 / portable ~63 MB) is scoped and the code already compiles for net8; just
  needs the csproj multi-target + extra publish profiles + release-workflow matrix.
- **Editor round-trip test harness.** The HTML sanitize policy and the C#
  image-serving boundary now have unit tests, but the markdown **load → edit →
  serialize round-trip** (Milkdown/ProseMirror) is still only covered by manual
  dogfooding. A proper test needs the full editor mounted in jsdom (ProseMirror +
  all Milkdown plugins) — more an integration harness than a smoke test. Worth
  building before relying on it for regression safety on parser/serializer changes.
- **Editor bundle lazy-load.** Mermaid pulled the bundle from ~560 KB to ~3.9 MB
  (exe 2.9 → 6.4 MB). Code-split Mermaid so it loads only when a `mermaid` block
  is present — switches esbuild to ESM chunks + adapts the HTML/extraction.

---

## Won't unless asked (known limits, parked deliberately)

