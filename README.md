# Markdown Midget

<img src="art/midget-256.png" alt="Markdown Midget mascot" width="120" align="right" />

A WYSIWYG markdown editor for Windows, modeled on WordPad's behaviors, menus,
toolbar, and keyboard shortcuts — but with **markdown as the native (and only)
format**. The default surface is a Word-like WYSIWYG editor; a toggle switches to
raw-markdown source editing.

Built on **.NET 10 / WPF** hosting a **WebView2** control. The editing surface is
[Milkdown](https://milkdown.dev/) (a ProseMirror-based WYSIWYG markdown editor),
so markdown is the literal document model rather than a lossy import/export.

## Status

First iteration (MVP). Windows-only for now; the editor core is web-based so a
cross-platform shell (MAUI/Avalonia) is a realistic future step.

## Layout

```
MarkdownMidget.sln
src/MarkdownMidget/         WPF app (net10.0-windows)
  MainWindow.xaml(.cs)      Menu, toolbar, WebView2 host, source toggle, file I/O
  wwwroot/                  Built editor bundle (served to WebView2) — generated
editor-src/                 npm/esbuild project that bundles Milkdown -> wwwroot
  src/main.js               Editor setup + the window.MDM host bridge
  build.mjs                 esbuild bundler
```

### Host ↔ editor bridge (`window.MDM`)

- `create(initialMarkdown)` — mount the editor with a document
- `getMarkdown()` / `setMarkdown(md)` — used for file I/O and the source toggle
- `cmd(name)` — run a formatting command (`bold`, `italic`, `underline`,
  `strike`, `code`, `h1`..`h6`, `paragraph`, `bullet`, `ordered`, `quote`, `hr`)

The editor posts `loaded` / `ready` / `change` messages back to the WPF host.

The editor bundle is **embedded in the assembly** and extracted to
`%LocalAppData%\MarkdownMidget\editor` at startup, so a self-contained publish is
a single `.exe` rather than an exe plus a loose `wwwroot` folder.

### Underline

Markdown has no underline, so it round-trips as inline HTML `<u>…</u>`
([editor-src/src/underline.js](editor-src/src/underline.js)): a custom Milkdown
mark serializes to `<u>`, and a remark transform collapses the `<u> … </u>`
inline-HTML pair back into the mark on load.

## Build & run

The editor bundle is checked in, so the app builds directly:

```sh
dotnet run --project src/MarkdownMidget
```

After changing anything under `editor-src/`, rebuild the bundle:

```sh
cd editor-src
npm install      # first time only
npm run build    # writes src/MarkdownMidget/wwwroot/editor.bundle.{js,css}
```

`npm run watch` rebuilds on change during development.

## Distribution (self-contained build)

`Release` is configured to publish a **single self-contained `.exe`** for alpha
testers — the .NET runtime is bundled, so no SDK/runtime install is needed
(testers do need the Edge **WebView2 runtime**, which ships with Windows 11):

```sh
dotnet publish src/MarkdownMidget -p:PublishProfile=win-x64
# -> src/MarkdownMidget/bin/Release/net10.0-windows/win-x64/publish/MarkdownMidget.exe
```

Debug/`dotnet run` stay framework-dependent and fast; only this profile produces
the bundled binary.

## Icon / mascot

The mascot (`art/midget.svg`) is the canonical Markdown `M▼` badge with googly
eyes and stubby feet, in the editor's Nord palette. `art/` holds rendered PNGs
(16–256 px) and a multi-resolution `midget.ico` used as the app/taskbar icon
(`<ApplicationIcon>`), the title-bar icon, and the toolbar mark. Regenerate with
ImageMagick:

```sh
cd art
for s in 16 24 32 48 64 128 256; do magick -background none -density 512 midget.svg -resize ${s}x${s} midget-${s}.png; done
magick -background none -density 512 midget.svg -define icon:auto-resize=256,128,64,48,32,24,16 midget.ico
```

## Keyboard shortcuts (WordPad-aligned)

| Action            | Shortcut       |
| ----------------- | -------------- |
| New               | Ctrl+N         |
| Open              | Ctrl+O         |
| Save              | Ctrl+S         |
| Save As           | Ctrl+Shift+S   |
| Bold / Italic / Underline | Ctrl+B / Ctrl+I / Ctrl+U (in the editor) |
| Toggle source     | Ctrl+E         |

## MVP scope notes

Per the project rule, anything from WordPad that isn't strictly easy to implement
is deferred from this first iteration. Notable deferrals / divergences:

- **Ribbon → menu + toolbar.** WPF has no trivial Office ribbon, so the MVP uses
  a classic menu bar + toolbar with the same commands and shortcuts.
- **Font size box → paragraph Style dropdown.** Markdown styles blocks
  (Paragraph / Heading 1–3), not point sizes — the "Styles, not size" divergence.
- **Underline → inline HTML.** Markdown has no underline; it round-trips as
  `<u>…</u>` (see above).
- **Toolbar glyphs.** Old-school flat icon buttons (Segoe Fluent Icons). The
  `</>` mark is reserved for inline code; the source/WYSIWYG toggle uses braces
  (`{}`, → markdown source) and a document glyph (→ formatted view).
- Deferred: print, page setup, find/replace, images/object insertion, color.
