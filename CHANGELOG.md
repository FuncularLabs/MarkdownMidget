# Changelog

All notable changes to **Markdown Midget** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/). While
under active alpha development (0.1.x), the minor version may carry breaking
changes between alpha tags.

## [Unreleased]

### Added
- **Regression tests for the 0.4.0 image/HTML path.** C# unit tests for the
  document asset-serving boundary — path-traversal containment (including
  percent-encoded `..` and sibling folders sharing a name prefix), rejection of
  absolute/rooted references, and MIME mapping — plus JS tests for the raw-HTML
  sanitize policy (script / event-handler / `javascript:` / `iframe` / `base`
  stripped; presentational tags like a centered `<img>` logo preserved) running on
  Node's test runner against real DOMPurify in jsdom. Both suites run in CI and in
  the release workflow.

## [0.4.0-beta1] – 2026-07-10

Minor bump to **0.4.0**. Two user-visible wins — documents that reference images
now render them fully, and the recurring "editor surface couldn't load"
(`ERR_ACCESS_DENIED`) crash after a hard exit is fixed — plus safe rendering of
embedded raw HTML. Shipped as a beta for a weekend of dogfooding before the
prerelease flag is dropped for 0.4.0 stable.

### Added
- **Crash-resilient WebView2 profile.** Each run now uses its own per-process
  WebView2 data folder instead of one shared folder. A hard crash or force-kill
  used to orphan WebView2 child processes that kept the shared folder locked,
  breaking the *next* launch with `ERR_ACCESS_DENIED`; per-process folders can't
  collide, so the following launch is always clean (stale folders from prior runs
  are swept on startup, skipping any still in use). If the editor surface still
  fails to load, the app offers a one-click restart into a fresh profile rather
  than stranding the user on a cryptic Edge error page. Documents and settings are
  untouched.
- **Raw HTML now renders** (sanitized). Embedded HTML — a centered logo
  (`<p align="center"><img …>`), `<br>`, `<sub>`/`<sup>`, small tables, etc. —
  renders instead of showing as escaped text. The HTML is sanitized with
  **DOMPurify** (scripts, event handlers like `onerror`, `iframe`/`object`, and
  `javascript:` URLs are stripped) before display, and the original markup is
  kept in the model so **saving round-trips it unchanged**. Relative image paths
  inside the HTML resolve against the document folder like everything else.

### Fixed
- **Relative image paths in opened files now render.** Images referenced
  relative to the document (e.g. `docs/logo.png`) resolve against the file's
  folder — the way Markdown Monster and GitHub do. A `<base href>` points at a
  dedicated host whose files the app serves from the document's folder via
  `WebResourceRequested` (a second virtual-host mapping won't serve cross-origin
  to the editor host, and image bytes weren't delivered). Only URL resolution
  changes; the markdown keeps the original relative paths, so **saving is
  unaffected**. Serving is restricted to the document's own folder subtree;
  untitled / dropped content clears the base. (Images inside raw HTML — e.g. a
  centered logo — render too; see "Raw HTML now renders" above.)

## [0.3.0-beta1] – 2026-07-09

First 0.3.0 release (the alpha1 work was dogfooded internally, never published,
and is rolled up here).

### Added
- **Richer Register / Unregister dialogs** for Windows integration, each with
  **minimalist diagrams** so non-technical users can see what every option does.
  - Register (all on by default): **Move** the download into the app folder
    (vs. copy), **Add to Start menu**, **Add a Desktop shortcut**, plus the
    existing **Set as default**. The original download location is remembered.
  - Unregister (all optional): remove the Open With registration, **restore a
    copy to where it was downloaded**, remove the Start-menu entry, remove the
    Desktop shortcut, and remove the installed app-folder copy.
  - **Move** installs to the app folder then relaunches from there and deletes
    the original download (via a `--finish-move` handoff, guarded by the
    unsaved-changes prompt so a restart can't drop edits).
- **Skip Spell Check in Code** (View menu, WYSIWYG) — leaves code blocks and
  inline code un-checked while still spell-checking prose, so identifiers,
  keywords and snippets don't get flagged. On by default; remembered between
  sessions. (Source view remains all-or-nothing — a WPF `TextBox` limitation.)
- **Spell-check on/off is now remembered** between sessions.
- **[ROADMAP.md](ROADMAP.md)** — a living wishlist/roadmap. First entry: the
  de-risk spike for owning the spell-check stack (custom dictionaries +
  "Add to dictionary" via the Windows Spell Checking API), since WebView2
  exposes no dictionary API.

### Changed
- **Denser table styling.** Square corners, a gray header row (darker than the
  stripes), tighter cell padding (`1px 6px`), 12px text, and the first data row
  is light (stripes start on row 2). Also fixed a latent bug where Nord's
  `!important` logical-padding rules were silently overriding our cell padding —
  tables now render at the intended density.

### Fixed
- **Win+arrow window management now behaves like Notepad / File Explorer.** We
  were intercepting Win+arrow and hand-rolling snap math, which was worse than
  the OS (no snap-assist, poor multi-monitor/DPI handling). Removed the handler
  entirely; the window is a standard resizable window, so native Windows Snap
  just works.

## [0.2.0-beta2] – 2026-07-01

### Added
- **Authenticode-signed releases via Azure Artifact Signing.** The release
  workflow now signs the published exe using a service principal + the
  `funcular-labs-public-trust` certificate profile, then verifies the
  signature before uploading to the release. Windows SmartScreen will build
  reputation quickly for signed installers rather than warning on every run.

### Changed
- **Exe metadata is populated.** Details tab now shows Company (Funcular Labs),
  Copyright (`© Funcular Labs 2026, MIT`), Product / FileDescription
  (`Markdown Midget` with the space), and the `+git-sha` suffix that MSBuild
  was appending to ProductVersion is suppressed.
- **`AppVersion` is now derived from the assembly's
  `AssemblyInformationalVersionAttribute`** at runtime. CI passes
  `-p:InformationalVersion=<tag-version>` at publish, so the title-bar version
  automatically reflects whatever tag drove the release — no more manual const
  bumps between the code, the csproj, and the tag.

## [0.2.0-beta1] – 2026-07-01

Beta milestone. Everything from the 0.1.x alpha series is baked in and the
release engineering (CI, tag-driven publishing, unit tests, embedded HELP.md,
Windows integration) is proven. This is the first `-beta` — targeted at
hands-on testing before dropping the prerelease flag for 0.2.0 stable.

### Changed
- **README image references** now point at
  `raw.githubusercontent.com/…/master/art/…` instead of relative paths, so the
  file renders identically on GitHub and stays functional when the README is
  copied anywhere (the absolute URL always resolves, without falling into the
  GitHub-strips-data-URIs trap).
- **Refreshed screenshot** — updated to reflect the current 0.2 feature set
  (formatting marks toggled on, live table editing, syntax-highlighted code
  block, ¶ tab-arrow marks, custom spell-check icon on the View toolbar,
  in-document mascot).

## [0.1.8-alpha3] – 2026-07-01

### Added
- **Unit tests.** New `tests/MarkdownMidget.Tests/` xUnit project covering
  `FindEngine` (four search modes, escapes, wildcards, whole word, case, regex
  edge cases) — 32 tests, ~40 ms locally.
- **GitHub Actions CI**: `.github/workflows/ci.yml` runs on pushes to `master`
  and every PR — builds the editor bundle (npm) + solution + runs tests on
  `windows-latest` with .NET 10.
- **GitHub Actions release publishing**: `.github/workflows/release.yml` fires
  on `v*` tag pushes. Builds the editor bundle, runs tests, publishes the
  framework-dependent exe with tag-derived `Version` / `InformationalVersion`,
  extracts the matching CHANGELOG section for release notes, appends the
  standard Download / Requirements / Notes boilerplate, and creates the
  GitHub release (prerelease flag inferred from `-alpha`/`-beta`/`-rc` in
  the tag). **This release is the first published by CI.**
- **HELP.md** now embeds its mascot header as a base64 data URI. The help
  file ships inside the exe (extracted to `%LocalAppData%\MarkdownMidget\
  HELP.md` at runtime), so linked images would orphan if the exe moved;
  inlining keeps the help view portable. README stays on relative-path
  images because GitHub strips data URIs from `<img>` tags but happily
  renders relative paths from the repo.

## [0.1.8-alpha2] – 2026-06-27

### Fixed
- **Registration dedupe now covers the per-user Explorer ProgID MRU.** The
  0.1.8-alpha1 dedupe cleaned `HKCU\Classes\.md\OpenWithProgids` and the
  `OpenWithList` MRU but missed the parallel per-user
  `Explorer\FileExts\.md\OpenWithProgids` layer — that's what was letting a
  stale "Markdown Midget" reference to an older version survive re-registration.
  Also handles legacy `Applications\MarkdownMidget…` / `Applications\mkm…`
  ProgID references in the same MRU and clears an outdated `UserChoice` if it
  points at one of ours.
- **The "Set as default" prompt now walks the user through Settings.** Windows
  10/11 protect the default-app UserChoice hash so apps can't set defaults
  programmatically; the registration confirmation now spells out exactly what
  to click in the Settings pane that gets opened.
- **Registration success message** notes that Explorer's Open With submenu
  aggressively caches and may still show an old entry until sign-out.

### Added
- **F1 opens the Help window** (in addition to Help ▸ View Help). The menu item
  displays the shortcut.

## [0.1.8-alpha1] – 2026-06-27

### Added
- **File ▸ Windows Integration ▸ Register as .md editor…** — a per-user, no-admin
  workflow to add Markdown Midget to the Windows "Open with" menu for `.md`
  files. Uses a stable ProgID (`MarkdownMidget.Document`), so re-registering the
  current version overwrites the same registry key and can't accidentally create
  duplicates. On register we also **dedupe stale references** to Markdown Midget
  (previous "Choose another app" pickings under `FileExts\.md\OpenWithList`,
  old `Applications\` entries under different exe filenames like `mkm.exe`).
- **Optional AppData install + Start-menu entry.** Checkbox in the register
  dialog copies the current build to
  `%LocalAppData%\Programs\MarkdownMidget\MarkdownMidget.exe` and creates a
  Start-menu shortcut — a portable-app style install with no MSI. Recommended
  because it keeps the Open With entry stable across future releases (just
  re-register after each upgrade).
- **Optional "Set as default"** — Windows 10/11 protect the UserChoice hash so
  apps can't set defaults programmatically; the checkbox opens the Default Apps
  Settings page filtered to `.md` for the user to confirm with one click.
- **File ▸ Windows Integration ▸ Unregister as .md editor** — removes the
  ProgID + Applications entry + `.md\OpenWithProgids` link and does the same
  dedupe pass. If an AppData install exists (and isn't the currently-running
  copy), asks whether to also remove that folder and the Start-menu shortcut.

## [0.1.7-alpha2] – 2026-06-27

### Fixed
- **Find Next / Find Previous now actually advance the cursor.** The WYSIWYG
  dispatcher was calling `findReset()` on every navigation, which reset the match
  pointer to -1 — so F3 always landed back at the first match. The reset is now
  only performed when the pattern or its options change (and is invalidated on
  any subsequent document edit).
- **Find no longer "lands nowhere"** when a match falls inside a hidden mermaid
  source block. The text-node walker now rejects any node whose ancestor has
  `display: none` or `visibility: hidden` (which covers the hidden mermaid
  `<pre>`, collapsed details, draft regions, etc.).

### Added
- **Standard Windows window-management shortcuts**, intercepted at the WPF
  Window so they work even when the WebView2 child has keyboard focus:
  - **Win+Up** — maximize (restore-from-minimized if minimized)
  - **Win+Down** — minimize (restore-from-maximized if maximized)
  - **Win+Shift+Up** — fill the working-area height at the current width
  - **Win+Left** / **Win+Right** — snap to the left / right half of the work area

## [0.1.7-alpha1] – 2026-06-27

### Added
- **Find** (Edit ▸ Find… / **Ctrl+F**) — modeless dialog with four search modes
  (Normal, Extended, Wildcards, Regular expression), **Match case** / **Match
  whole word only** / **Wrap around** toggles, and a `Match m of n` status line.
  **F3** jumps to the next match, **Shift+F3** to the previous. Find works in
  both the WYSIWYG view (text-node scan with browser-selection highlight) and
  the Markdown source view (TextBox selection). Tooltips on the Extended /
  Wildcards / Regex radios describe the syntax. HELP.md has the full escape
  tables. Replace is not yet in; this iteration is read-only Find.
- **Spinner overlay** when opening a file — a small busy card shows over the
  editing area during the read + editor load, useful for large documents with
  embedded base64 images. Fires on File ▸ Open, Open Recent, and editor-area
  file drops.

## [0.1.6-alpha1] – 2026-06-27

### Added
- **Mermaid diagrams.** Fenced code blocks tagged `mermaid` now render as live
  diagrams in the WYSIWYG view (flowcharts, sequence diagrams, class diagrams,
  etc.). The diagram appears below the block, the source itself is hidden while
  the cursor is outside the block, and revealed for inline editing when the
  cursor moves into it. Markdown round-trips unchanged (the on-disk file remains
  a normal ``` ```mermaid ``` ``` fence). Print and PDF export render the
  diagram (not the source), with page-break-inside avoided.

### Known limitations
- Mermaid pulls a large dependency tree (D3, dagre, cytoscape) — the editor
  bundle grows from ~560 KB to ~3.9 MB. The extracted bundle is one-time-per-run
  and cached by WebView2 thereafter; the app's startup time is still well under
  a second on a typical machine, but the on-disk `.exe` size grows by about
  0.5 MB after compression. Bundle splitting / lazy-load is a possible later
  optimization if this becomes a concern.

## [0.1.5-alpha2] – 2026-06-27

### Changed
- **Landing state is now the "No document open" splash.** A fresh session does not
  pre-create an Untitled document; the gray placeholder shows immediately, ready
  to accept a dropped file, **Open**, or **New**.
- The placeholder's prompt text now exposes **Open** and **New** as clickable
  hyperlinks (the Ctrl+O / Ctrl+N shortcuts still work the same).
- **Default Document Width** for new installs is now **Landscape** (was Portrait).
  Existing users keep whatever they had persisted in `settings.json`.

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

[Unreleased]: https://github.com/FuncularLabs/MarkdownMidget/compare/v0.4.0-beta1...HEAD
[0.4.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.4.0-beta1
[0.3.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.3.0-beta1
[0.2.0-beta2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.2.0-beta2
[0.2.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.2.0-beta1
[0.1.8-alpha3]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.8-alpha3
[0.1.8-alpha2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.8-alpha2
[0.1.8-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.8-alpha1
[0.1.7-alpha2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.7-alpha2
[0.1.7-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.7-alpha1
[0.1.6-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.6-alpha1
[0.1.5-alpha2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.5-alpha2
[0.1.5-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.5-alpha1
[0.1.4-alpha1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.4-alpha1
[0.1.3]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.3
[0.1.2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.1.2
