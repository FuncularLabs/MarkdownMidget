# Changelog

All notable changes to **Markdown Midget** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/). While
under active alpha development (0.1.x), the minor version may carry breaking
changes between alpha tags.

## [0.1.5-alpha1] – 2026-06-27

### Added
- **File ▸ Close** (Ctrl+W) closes the current document without exiting the app
  and shows a gray placeholder with a "drop a file here / Ctrl+O / Ctrl+N" prompt.
  The whole window remains a drop target.
- **External change detection.** A `FileSystemWatcher` watches the currently open
  file; when an external program modifies it, Markdown Midget writes a timestamped
  `name.yyyyMMdd-HHmmss.ext.bak` (capturing the in-memory version including unsaved
  edits) and presents a dialog with three actions: **Reload Disk Version**,
  **Save My Version As…** (with a follow-up "switch to it or stay" prompt), or
  **Keep Current** (your next Save will overwrite the disk version).
- **Print (Ctrl+P) and Export to PDF** under File ▸ Print:
  - A `@media print` stylesheet renders white paper with light-themed code blocks
    (GitHub-ish syntax palette), no chrome/shadow/marks/blockquote tint, and
    page-break hygiene on tables, code blocks, and headings.
  - Two persisted prefs in the Print submenu — **Include header and footer (PDF
    export)** and **Color code blocks** — are remembered **separately for each
    Document Width view** (Portrait / Landscape / Full).
  - Prints whatever view is current: WYSIWYG renders the document; source view
    prints the raw markdown as monospaced text.

### Changed
- Tightened table preview CSS: cell padding shrunk to `3px 8px`, line-height 1.35,
  table margin tightened, and cell-internal `<p>` margins zeroed.

### Known limitations
- The browser-style print preview's own toggles (printer, copies, "Headers and
  footers") are inherently not readable by the host. Our persisted **Include
  header and footer** preference therefore applies to **PDF Export** only; the
  Print preview's checkbox is whatever the user sets there. The **Color code
  blocks** preference works for both pathways.

## [0.1.4-alpha1] – 2026-06-27

### Changed
- Spell-check toggle button joins the View toolbar group (no leading separator).

## [0.1.3] – 2026-06-27

### Added
- **Spell-check toggle button** at the right of the View toolbar — a custom
  "abc with red squiggle" icon, two-way bound to **View ▸ Spell Check**.

## [0.1.2] – 2026-06-26

### Added
- **Initial public release.** WordPad-style, markdown-native WYSIWYG editor for
  Windows on .NET 10 / WPF / WebView2 / Milkdown.
- WYSIWYG editing with a Ctrl+E toggle to the raw markdown source.
- Headings (1–5), bold/italic/underline (HTML `<u>`)/strikethrough, inline code,
  bulleted & numbered lists, block quotes, horizontal rules.
- **GFM tables** with an insert dialog and native context-menu edits (insert /
  delete / select column, row, table); Markdown-Monster-style theming.
- **Pictures** embedded as base64 data URIs with an aspect-locked Resize dialog
  (round-trips as inline `<img width height>`).
- **Links** rendered like a browser with hover URL tooltips.
- **Fenced code blocks** with Prism syntax highlighting (C#, JavaScript,
  TypeScript, HTML, CSS).
- **Document Width** modes (Portrait / Landscape / Full), persisted between
  sessions, with a status-bar **zoom indicator** (Ctrl + mouse wheel).
- **Recent files** (MRU 5), drag-and-drop, **read-only mode** (and `--readonly`
  CLI switch), bundled HELP.md launched read-only from Help ▸ View Help.
- **Formatting marks** toggle (¶ / ↵ / →).
- Single-file `.exe` distribution.

[0.1.5-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.5-alpha1
[0.1.4-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.4-alpha1
[0.1.3]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.3
[0.1.2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.2
