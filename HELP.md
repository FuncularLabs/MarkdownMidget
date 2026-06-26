# Markdown Midget — Help

A WYSIWYG markdown editor. You edit a formatted document, but the file on disk is
always plain **Markdown**. You're reading this in **read-only mode** (a separate
window) — close it whenever you like and your work is untouched.

## The two views

- **WYSIWYG** (default) — edit formatted text directly, like a word processor.
- **Markdown source** — edit the raw markdown. Toggle with the `{ }` / document
  button on the toolbar or **View ▸ Edit Markdown Source** (**Ctrl+E**).

Both views drive the same document; switching syncs your edits across.

## Toolbar

From left to right:

- **New / Open / Save / Save As** — file operations. **Save As** uses the
  double-disk icon and stays available even in read-only mode.
- **Undo / Redo** — enabled only when there is something to undo/redo.
- **Style** dropdown — the block style at the cursor (Paragraph, Heading 1–5, or a
  Code block by language). It follows your cursor and lets you switch style.
- **Bold / Italic / Underline / Strikethrough**.
- **Inline code `</>`** with a chevron dropdown for inserting a fenced **code
  block** in a chosen language (Plain, C#, JavaScript, TypeScript, HTML, CSS).
- **Bulleted list / Numbered list / Block quote / Horizontal rule**.
- **Insert link / Insert picture / Insert table**.
- **Source toggle** and the **¶** formatting-marks toggle.

Toolbar buttons never take keyboard focus, so clicking them won't move your cursor
or drop your selection.

## Keyboard shortcuts

| Action | Shortcut |
| --- | --- |
| New / Open / Save / Save As | Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| Bold / Italic / Underline | Ctrl+B / Ctrl+I / Ctrl+U |
| Paragraph / Heading 1–5 | Ctrl+0 / Ctrl+1 … Ctrl+5 |
| Focus the Style box | Ctrl+Shift+H |
| Insert link | Ctrl+K |
| Exit a code block (new paragraph below) | Ctrl+Enter |
| Toggle markdown source | Ctrl+E |

## Editing behaviors

- **Headings:** pressing **Enter** at the end of a heading starts a plain
  paragraph (like Word). Splitting in the middle keeps both halves as headings.
- **Tab:** inserts a tab character; **Shift+Tab** removes a preceding one. Inside a
  list, Tab/Shift+Tab indent/outdent the item instead.
- **Code blocks:** get out of one with **Ctrl+Enter** (paragraph below), by
  arrowing/clicking into the paragraph that always follows the last block, or — if
  the code block is the very first thing in the document — by pressing **↑** on its
  first line to insert a paragraph above.
- **Links** show styled, with the URL as a hover tooltip.
- **Pictures** are embedded into the markdown as base64 data URIs, so they render
  in the editor and travel with the file. **Right-click a picture ▸ Resize…** to
  scale it (the aspect ratio stays locked to the original); a resized picture is
  stored as inline HTML `<img …>` so the size persists.
- **Underline** has no markdown equivalent, so it is stored as inline `<u>…</u>`.

## Tables

Insert a table from **Insert ▸ Table…** (or the grid toolbar button): choose
columns (default 3), rows (default 4), and whether to include a header row (GFM
tables always keep a header row, so this controls whether your row count is in
addition to it).

**Right-click inside a table** for the only structure edits available in the
WYSIWYG view:

- **Insert** column left/right, row above/below
- **Delete** column / row / table
- **Select** column / row / table

With cells selected, **Backspace/Delete clears their contents** and **typing
replaces them**. Anything more elaborate (alignment, merging, etc.) is done by
editing the markdown source directly.

## Formatting marks (¶)

The **¶** toolbar toggle shows Word-style marks in light gray: **¶** at paragraph
and heading ends, **↵** at manual line breaks, and **→** for tabs.

## Spell check

**View ▸ Spell Check** toggles the red squiggles in both views. On by default.

## Read-only mode

**Edit ▸ Read Only** locks the document against changes (this Help window uses it).
All document-modifying controls — the formatting toolbar, the Format/Style/Insert
menus, Undo/Redo, and plain **Save** — gray out, while **Open / New / Save As** and
the view toggles stay available (use **Save As** to keep a copy elsewhere). The
title bar shows **`[Read Only]`**. You can also start read-only from the command
line with `--readonly`.

## Modified state, undo, and saving

- The title shows a leading `*` when the document differs from the last
  opened/saved version. Undoing back to that state clears the `*` automatically.
- **Opening or starting a new document clears the undo history** — you cannot undo
  past the just-opened state.
- **Saving keeps the undo history** — you can still undo past a save, and undoing
  back to the saved content clears the `*`.

## Files & windows

- Native format is Markdown (`.md`). Plain text is also fine.
- **File ▸ Open Recent** lists the last 5 files you opened or saved, with a
  **Clear Recent** option.
- **Drag a file onto the window:** it opens in place if the current document is
  untitled and unmodified; otherwise it opens in a **new window**. You can also
  pass a file path (and optional `--readonly`) on the command line.

## Distribution

Markdown Midget ships as a single `.exe`. A self-contained build needs nothing
installed; a smaller framework-dependent build needs the .NET 10 Desktop runtime.
Either way, the Microsoft Edge **WebView2 runtime** (already on Windows 11) is
required.
