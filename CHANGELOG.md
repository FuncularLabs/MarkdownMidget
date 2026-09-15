# Changelog

All notable changes to **Markdown Midget** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/). A version
with a suffix, such as `-beta1` or `-rc1`, is a prerelease: it is published for
testing before the stable release of the same number.

## [Unreleased]

### Added

- **Ctrl+H now opens Replace**, as in other editors: the Find and Replace dialog opens with the cursor in *Replace with*, in either view. In a read-only document, such as Help, Replace stays greyed out and the cursor goes to *Find what*.
- **Edit ▸ Replace…** now sits under **Find…**, and does what Ctrl+H does.

### Changed

- **The formatted view and Markdown source buttons now sit in one faint box**, with a thin line between them, so it's clear you pick one or the other. The buttons and icons are the same size as before.

## [1.0.0-rc1] - 2026-09-15

The 1.0 release candidate: 1.0.0-beta1 after a round of everyday use, plus the changes below.
Saving from the formatted view now keeps the text you didn't change as written, apart from a few cases
below; the status bar shows the line and column with Go to Line and line numbers, and opening a file while this
window has a document starts a new window. A prerelease, so these changes get everyday use before
1.0.0; it includes everything listed for 1.0.0-beta1.

### Added

- **The cursor's line and column in the status bar, and Go to Line (Ctrl+G)**, in
  both views (#10), and **View ▸ Line Numbers** to show them in the margin of either
  view, together or per view. Lines are lines of the markdown; see Help, *The two views*.
  Go to Line reaches every line in the formatted view too, blank lines and fences included,
  and its margin shows every line once: each block's range of lines, and the lines between blocks.
- **A `#link` to a heading in the same document jumps there**: Ctrl+click it, or click it in a read-only window such as Help.
- **Right-click a link ▸ Copy Link** copies its address as the markdown writes it (web, `mailto:`, relative or `#`), in the formatted view and in Help.
- **Ctrl+click a web link to open it in your browser**, after a prompt that shows the full address; in a read-only window such as Help, just click. Email links don't open.
- **A document over 512 KB opens with line numbers and spell check off**, for that document and in both views, and the status bar says so once: on a large file a spell check takes seconds and the formatted view's margin numbers slow every switch. Turn either back on from **View ▸ Line Numbers ▸ Show Line Numbers** or **View ▸ Spell Check**; that holds for the document until you open another or close it, and your saved settings don't change. A document whose opening or view switch takes more than 5 seconds gets the same, from then on.
- **Switching from the Markdown source view back to the formatted view shows the busy spinner** when it takes more than a moment, as it does on a large file; until the switch is done the source view can't be typed in.
- **The opening progress card now names each step** under its bar: Reading file, Checking encoding, Building formatted view, Drawing page, Finishing up. A file of 250 KB or more that opens in the formatted view also suggests the Markdown source view (Ctrl+E), which opens it much faster; nothing switches on its own. While the formatted view is showing, the same text appears in the status bar.

### Changed

- **File ▸ Open, Open Recent and dropping a markdown or text file open it in a new window** when this window has a document, so the document you have is never replaced and there is no prompt about unsaved changes. A window showing "No document open", or a blank untitled document you haven't typed in, still opens the first file itself. A drop opens at most 10 new windows; the status bar names the rest. Opening the file this window already has just brings it forward.
- **The View toolbar now has a button for each view**, formatted view and Markdown source (`##`), beside the **Line numbers** button. The view you're in is highlighted; click the other to switch, as Ctrl+E does. They replace the single `{ }` toggle.
- **Spell check on a large document now finishes several times faster**: the text is checked in pieces that end at a line break, so no word is split. On a test machine a 512 KB document took about 2 seconds instead of 9. A word repeated across the break between two pieces isn't flagged.
- **Typing and opening are now faster on long documents in the formatted view**: a keystroke no longer redraws every formatting mark, and neither opening a document nor the first edit converts the whole document back to markdown to check for changes.

### Fixed

- **Saving from the formatted view keeps the text you didn't change as you wrote it**: a document saved with no edits, backed up, or switched to the Markdown source view comes back byte for byte, and after an edit the top-level blocks you changed (a paragraph, a heading, a table, or the whole list an item is in) are written in Markdown Midget's conventions, and blocks near your edit sometimes are too (for instance a list right after an edited list). Everywhere else, escapes, `---` rules, blank lines, bare URLs, link definitions and a missing final newline stay as they were; they used to be rewritten throughout on every save. A document with a link definition inside a quote, list or footnote, or a label defined twice, is still written in full once you change it, and so is a change too large to check quickly (an item in a very long list) or one whose neighbours still don't read back the same after they're rewritten.
- **A markdown file dropped on the formatted view opens as the file itself**: Save writes to it, it isn't marked modified, and it goes on Open Recent. It used to open as an unsaved copy named like the file, so Save never updated the file you dropped.
- **Right on File ▸ Open Recent or View ▸ Theme opens it at its first entry**, and Left comes back; it used to jump to the next menu.
- **A paragraph after a nested list keeps its blank line when saved from the formatted view**, inside a quote too, so it no longer joins the list's last item when the file is opened again (#11).
- **YAML front matter survives a save from the formatted view**: a `---` block at the top of a file is saved byte for byte and shows as a grey code block with its `---` lines; it used to become a rule and a heading.
- **Saving from the formatted view keeps each table's layout**: a table with its columns lined up stays lined up at the column widths it was written with, one written without padding stays unpadded, the delimiter row (`---`, `|:--|`, …) is kept as written, and a new table is lined up unless a cell is longer than 80 characters. A file with long table cells no longer grows several times over, and an empty cell is no longer saved as `<br />`.
- **An inline `<br>`, `<br/>` or `<br />` is no longer deleted when a document is opened**, in a paragraph, table cell, list item or quote, so saving from the formatted view keeps it, and a line break just before it, as written; it used to join the text either side (`line one<br>line two` saved as `line oneline two`). Broken since 0.10.0.
- **A list item that starts with a quote, code block, table, heading, rule or another list keeps it when saved from the formatted view**: `- > quote` is saved as `- > quote`. It used to be saved as `- <br />` with the quote on the next line, and on the next open the `<br />` line hid the quote, `>` and all, inside raw HTML.

## [1.0.0-beta1] - 2026-09-13

The first 1.0 prerelease. 1.0 is not feature-maximal: it is the release where the
tool does what its first paragraph promises, with every deliberate limit written
down. The 2026-09-10 readiness audit found one hole that would embarrass a 1.0 —
a save through the formatted view quietly rewrote your markdown's style, and took
the file's line endings and byte-order mark with it — plus a handful of edges that
read as unfinished. All of them are below: the conventions a save writes are
pinned and documented, line endings and the mark survive, Find has Replace, a
picture dropped or pasted is a picture rather than garbage or nothing, a file
already open in another window brings that window forward instead of opening a
second copy, and **Help ▸ View Help** gains a *Known limits* section saying what
the app deliberately does not do. 1.0 ships portable-only — there is still no
installer, and that is the next release's headline.

A prerelease for the usual reason: this is a great deal of file-handling change —
what a save writes to your file, what a drop does, how a second window on the same
file is treated — and it wants real hands on real documents before the stable
promote.

### Added
- **Find has Replace.** The Find dialog (Ctrl+F) has a *Replace with* box and
  **Replace** / **Replace All** buttons, in both views and in all four search
  modes. Replace changes the match Find is on and moves to the next one (with
  nothing found yet it is Find Next); Replace All changes every match as one
  undo step — or every match inside the selected text, when some is selected —
  and the status bar reports the count. In Regular expression mode the
  replacement can name groups — `$$`, `$&`, `$0`, `$1`…`$99` and `${name}`, with
  every other `$` form left as typed, and the same in both views; in Extended
  mode it takes the same `\n`, `\t`, `\r`, `\\` escapes the query does; in
  Normal and Wildcards mode it goes in as typed. In the formatted view a
  replacement takes the formatting of the first character it replaces, and a
  match that runs from one paragraph into the next is left alone. A pattern that
  does not compile is refused with the message Find shows, and nothing is
  changed. In the formatted view a search stops after 50000 matches — `\b` or
  `x*` matches at every position — and says so beside the count; Replace All
  then refuses rather than changing the part of the document the search reached.
  Replace is greyed while the document is read-only, and the dialog reopens with
  the last query and replacement. (#5)
- **Opening a file that is already open brings that window forward.** Every
  window is its own process, so until now a second File ▸ Open, Open Recent, drop
  or Explorer double-click on a file you already had open gave you a second copy,
  and the two saved over each other. Now the window that has the file comes to the
  front instead, and a double-click never flashes a second window. If that window
  can't be brought forward, the file opens read-only in the new one, with a note
  saying where it is open; turning Edit ▸ Read Only off again claims the file
  first, so it stays read-only while the other window still has it. Applying an
  update from a window in that state restarts it without carrying that read-only
  over: the new window asks for the file again, as any open does. (#1)
- **Pasting a picture into the Markdown source view embeds it.** Ctrl+V with an
  image on the clipboard — a screenshot, a picture copied from somewhere — used to
  do nothing in the source view, silently. It now inserts the picture at the caret
  as the same base64 data URI Insert ▸ Picture and the formatted view produce, in
  one undo step. A paste that carries text still pastes the text. (#7)
- **A picture file dropped on the editor is inserted as a picture.** Dropping a
  PNG, JPEG, GIF, WebP or BMP file on either view used to open it as a text
  document — in place with no prompt if the document was untitled and unmodified,
  in a fresh window from the toolbar otherwise, or after the discard prompt on the
  formatted view — and fill the editor with garbage. Now a dropped file is routed
  by what it is: a picture is embedded at the caret exactly as Insert ▸ Picture
  embeds it (recognised by its bytes, so a picture named `.md` is still a
  picture), a markdown or text file opens as before, and anything else is refused
  with a note in the status bar naming it — the document you have open is never
  replaced. Several pictures are inserted in the order dropped; a markdown file
  dropped with them is not opened. A markdown file dropped on the formatted view
  now also keeps its byte-order mark, as one opened with File ▸ Open does.
  Dropping again before a drop has finished replaces it rather than mixing the
  two, and if the document changes while a drop is being read — another file
  opened, this one closed or turned read-only, the view switched — nothing is
  inserted and the status bar says so. A drop on the formatted view that gets no
  answer back from the editor gives up rather than waiting, and says so in the
  status bar: the wait is 10 seconds plus a second for every 8 MB it asked for,
  18 seconds for one picture at the size limit, capped at ten minutes. A picture
  another program is still writing is read rather than refused, and the bytes that
  come back are checked before anything is inserted — a file cut short or grown
  past the size limit in between is named rather than embedded as a picture
  nothing can display. **Note:** a *text* file without a markdown or `.txt`
  extension — `notes.json`, `app.log`, `Program.cs` — used to open when dropped
  and is now refused; the drop route is the only one that insists on a markdown or
  text extension, so those files still open normally through File ▸ Open (choose
  **All files** in the dialog) and by passing the path on the command line. (#6)
- **Help gains a Known limits section.** The deliberate limits, in one place,
  each with what you run into and what to do instead: no installer and no Add or
  Remove Programs entry, the markdown conventions a save through the formatted
  view writes (and the source view as the way round them), pictures embedded
  rather than saved beside the document, US-English spell check, one document per
  window, and the rest. **Help ▸ View Help**, near the end — Distribution follows
  it. (#9)

### Changed
- **A Regular-expression pattern has to mean the same thing in both views.** The
  two views run different regex engines, so a handful of constructs one has and
  the other does not — `\A`, `\Z`, `\z`, `\G`, `(?>…)`, inline options like
  `(?i)`, `(?#comments)`, `(?'name'…)`, `\k'name'`, class subtraction
  `[a-z-[aeiou]]`, possessive quantifiers, Unicode blocks like `\p{IsGreek}`,
  and a loose `{`, `}` or `]` — are now refused outright with *Invalid pattern.*
  rather than searching one thing in the source view and another (or nothing) in
  the formatted one. So is `\1` in a pattern that writes a named group before an
  unnamed one, where the two views number the groups differently: write
  `\k<name>`, which means the same group in both. `\p{L}` and `\p{Lu}` work in
  both now; they used not to work in the formatted view at all. Help lists the
  whole set under *Find ▸ Regular expression*. (#5)
- **A pattern that matches a position rather than text now replaces in the
  formatted view too.** `^`, `$` and `(?=…)` match no characters; the formatted
  view used to skip them, so `^` with `> ` — the "prefix every line" idiom that
  has always worked in the source view — did nothing at all there. Find shows
  such a match as a caret and Replace inserts at it, in both views. (#5)
- **Bullet lists are saved with `-` bullets; they were `*`.** And where one
  list directly follows another, the second is written with `*` bullets (it was
  `-`): with the same marker, CommonMark would read the two as one list. (#2)
- **Insert ▸ Picture… and a paste into either view refuse a picture larger than
  64 MB**, as dropping a picture file does: nothing is inserted, and the status
  bar says "Too large to insert (over 64 MB): huge.png" (a pasted picture, having
  no name, is "pasted picture"). An embedded picture rides inside the document as
  text, about a third again its size in every copy and every save, so one limit
  now applies whichever way a picture comes in — dropped on either view, picked
  through **Insert ▸ Picture…**, or pasted into either view.

### Fixed
- **A document ending in anything but a paragraph or heading is no longer called
  modified the moment you click in it.** A list, a table, a code block, a
  blockquote, a thematic break: the formatted view adds an empty paragraph after
  such a document, and did it on the first thing you did — a click, or F3 — so
  the title gained its `*`, a crash copy was written and closing asked to save,
  for a document nobody had edited. That paragraph is now added as the document
  is opened, where it belongs, and Undo is unaffected. (#5)
- **The Markdown source view shows the file as it is on disk.** It was filled
  from the formatted editor, so a file written with setext headings or
  reference-style links arrived in it already rewritten into Markdown Midget's
  conventions — and a save from there wrote the rewrite, having never shown you
  your own file. That happened on Ctrl+E straight after opening, and also when a
  file was opened while the source view was already showing: File ▸ Open, Open
  Recent, a drop on the source view, a reload after the file changed on disk. The
  source view now shows the file's own text while the document is still the one
  you opened or saved, whichever way it got there, so a document edited and saved
  only there is written back as typed. Once the formatted view has changed the
  document, the source view shows the editor's version of it, because that is
  then the only copy of your work. (#2)
- **Work recovered after a crash comes back in the view you were writing in.**
  Unsaved work from the **Markdown source** view was handed back in the formatted
  view, and pressing Ctrl+E there showed the formatted editor's version of what you
  had typed — setext headings as `#`, reference links inlined — which a save from
  either view then wrote to your file. You never saw your own words again. The view
  is now recorded with each crash copy and entered before the work is loaded, so the
  source view shows the recovered text exactly as it was typed, still marked unsaved
  and still pointing at its file; the formatted view holds the same document, so
  Ctrl+E works straight away. Windows that went down together each come back in
  their own view, encrypted documents included (the password prompt comes first, and
  cancelling it leaves the window as it was). Copies left by 0.10.0 or earlier
  record no view and come back in the formatted view, as before. (#2)
- **A file's line endings and UTF-8 byte-order mark are kept as found.** A CRLF file
  with a code block in it came back with mixed endings — the formatted view
  writes LF between blocks but keeps whatever was inside fenced, indented and
  HTML blocks — and a file that began with a UTF-8 byte-order mark lost it on
  the first save. Now the file's line ending is detected when it opens (by
  majority, for a mixed file) and put back throughout on every Save, Save As,
  Save My Version As, encrypt, password change, convert and timestamped `.bak`.
  The mark goes back on every unencrypted save — Save, Save As, Save My
  Version As, convert, the `.bak` — if the file had one, and is never added if
  it didn't. An encrypted write keeps no mark: inside ciphertext it would mark
  nothing. A new document saves LF. (#3)
- **A file rewritten on disk with identical content is no longer reported as an
  external change.** A formatter with nothing to do, a sync client, or a tool
  that only changed the line endings raised the "modified by another program"
  prompt — or a silent reload — because the disk was compared against the
  editor's own serialisation of the document, which differs from the file for
  anything the editor normalises. The comparison is now against the file as it
  was last read or written, and if a program only re-encodes the file — line
  endings or the byte-order mark, text unchanged — the window quietly adopts that
  convention for its next save. (#4)
- **Saving from the formatted view keeps tight lists tight, `snake_case_word` as
  written, and emphasis that touches punctuation as emphasis.** A document that
  passed through the formatted view came back with a blank line between every
  bullet, with `snake\_case\_word`, and — for emphasis opening on an underscore,
  `a*_b*` say — with text that no longer read as emphasis the next time the file
  was opened. All three now survive a save. The one exception is emphasis of that
  kind — opening or closing on punctuation — that directly touches an emoji (or
  any other character UTF-16 stores as a pair): the text is kept whole, the
  emphasis may come back as the literal `*…*` text. The rest of what the
  formatted view does to a file's style — `1.` numbering, `#` headings, fenced
  code, inline links — is unchanged, pinned by tests, and listed in Help under
  *Modified state, undo, and saving ▸ Markdown conventions*. (#2)
- **Alt+F4 closes the window when no document is open.** With nothing open —
  after File ▸ Close (Ctrl+W) in the formatted view, say — Alt+F4 did nothing.
  Keyboard focus stayed in the editor that the gray "No document open"
  placeholder had replaced, so the key went to a window you could no longer see.
  The placeholder now takes focus when it appears, and Alt+F4 closes the window.
  The close box and File ▸ Exit always worked. With a document open nothing
  changes: Alt+F4 still asks to save a modified document first.

## [0.10.0] - 2026-09-10

First stable release on the 0.10 line — the beta's content with the prerelease
flag dropped, after dogfooding. Everything new on this line:

- **The Markdown source view is syntax-highlighted and follows the theme.**
  Ctrl+E now shows coloured markdown — headings, emphasis, code, links, quotes,
  list markers and rules — from the same palette the formatted view uses, on the
  same page background. Underneath, an AvalonEdit editor replaced the plain text
  box; editing, spell check, find, word wrap and the caret behave as before.
- **The source view can have its own theme.** **View ▸ Theme ▸ Same Theme for
  Both Views** is on by default. Turn it off and View ▸ Theme changes only the
  view you're in, so the source view can run dark under a light document. The
  menu ticks the theme of whichever view is showing; both choices are remembered.
- **Fixed:** Alt menu shortcuts and an Alt tap work with a document open, and
  AltGr on international keyboards types its character without opening the menu.

See the beta notes below for the full detail.

## [0.10.0-beta1] - 2026-09-10

The source view gets colour — and, if you want it, a theme of its own — plus the
Alt menu shortcuts working with a document open. A prerelease for the usual
reason: the control underneath the source view changed (an AvalonEdit editor
replaces the plain text box), and that wants real hands on real documents before
the stable promote.

### Added
- **The Markdown source view is syntax-highlighted, and it follows the theme.**
  Ctrl+E now shows coloured markdown — headings, bold/italic, inline and fenced
  code, links, block quotes, list markers and rules — drawn from the same
  palette the formatted view uses, on the same page background. The point is to
  match the theme's vibe, not the code-block colours: a light theme keeps a light
  source view. Where a theme's accent would be too faint to read as small text, that
  token falls back to the body text colour, so the source view is never less legible
  than the theme's own prose. Under the hood the source view moved from a plain text
  box to an AvalonEdit editor; editing, spell check, find, word-wrap and the caret
  behave as before.
- **The source view can have its own theme.** **View ▸ Theme ▸ Same Theme for Both
  Views** is on by default and works as before. Turn it off and View ▸ Theme changes
  only the view you're in: pick Dracula while in the source view and the formatted
  document stays on Midget Solarized. One list of themes, one menu; it ticks the
  theme of whichever view is showing, both choices are remembered, and turning the
  link back on snaps the source view to the document's theme. The second theme is
  resolved without ever being applied to the page.

### Fixed
- **Alt menu shortcuts work once a document is open.** Alt+F (and the rest) and an
  Alt tap worked on the empty splash and went dead as soon as a file was open,
  because the editor surface swallowed them. They now reach the menu from the editor
  too. AltGr on international keyboards still types its character, and releasing it
  does not open the menu.

## [0.9.0] - 2026-09-08

First stable release on the 0.9 line — the betas' content with the prerelease
flag dropped, after dogfooding. Everything new on this line:

- **Secure Markdown (.mdenc): password-protected encrypted documents.**
  AES-256-GCM, with the key derived from your password by Argon2id. Once open,
  an encrypted document edits like any other; the decrypted text lives only in
  memory, and the crash-protection copy is encrypted too. **File ▸ Encrypt
  Document…**, **Convert to Unencrypted…** and **Change Password…**; Save As
  offers *Secure Markdown* as a file type. **There is no password recovery** —
  the dialog says so before you set one. A Settings checkbox adds .mdenc to
  the File ▸ Open filter (off by default).
- **The file-dialog crash is fixed, and covered if it recurs.** Windows' Open
  and Save dialogs now run in a throwaway helper process, so a faulty Explorer
  add-on can no longer take the editor down. If the helper itself crashes, a
  **built-in file picker** (address bar, places, folder tree, sortable list,
  New Folder) takes over on the spot and stays on; **Edit ▸ Settings ▸ Always
  use the built-in file picker** switches it either way.
- **The toolbar follows the cursor**, the way Word's does: Bold, Italic,
  Underline, Strikethrough and Code light up for the text under the caret, the
  Format menu shows matching checkmarks, and the Style dropdown no longer goes
  stale in code blocks of unlisted languages.
- **Fixed:** "Add to Dictionary" and "Ignore All" work in read-only windows;
  closing with "Don't Save" no longer trips an error dialog (latent since the
  first release); disabled toolbar buttons are unmistakably disabled; Open and
  Save start in the current document's folder.

> **Updating from 0.8.2 or earlier:** re-run **File ▸ Windows Integration ▸
> Register as .md editor…** once if you want .mdenc files to open by
> double-click. Encrypted files always open via File ▸ Open (with the filter
> checkbox on), Open Recent, or a typed name.

See the beta notes below for the full detail.

## [0.9.0-beta4] - 2026-08-31

The file-dialog crash fix, in full — and a switch so the new picker can be
tried without owning a broken machine.

### Added
- **Windows' file dialog now runs in a separate process.** That dialog loads
  Explorer add-ons (preview and thumbnail handlers) into whatever program
  shows it, and a faulty one crashes that program outright — the failure Eric
  reported in 0.8.x, and the same one Markdown Monster has hit for years.
  Open and Save now hand the job to a short-lived helper process, so a bad
  add-on takes only the helper with it. The editor and your document don't
  notice.
- **A built-in file picker, for when that happens.** Isolation alone would
  save the app but still leave you unable to open anything, so the helper
  crashing hands straight over to Markdown Midget's own picker: address bar,
  shortcuts to Desktop/Documents/Downloads/drives/recent folders, a folder
  tree, a sortable file list, type-ahead, New Folder and overwrite
  confirmation when saving. It loads no Explorer add-ons at all — that's the
  whole point — so it deliberately has no thumbnails, preview pane or shell
  context menus. After a crash it also switches itself on permanently, tells
  you it did, and says where to switch back.
- **Edit ▸ Settings ▸ "Always use the built-in file picker"** turns it on
  whenever you like — which is how to try it without a broken shell.

### Fixed
- Open and Save now start in the current document's folder rather than
  wherever Windows last left the dialog. The built-in picker also offers
  recently used folders in its shortcut rail.

## [0.9.0-beta3] - 2026-08-30

### Fixed
- **Closing with "Don't Save" no longer trips an error dialog.** Latent since
  the very first release: answering Don't Save re-closed the window while the first close
  was still in progress, which throws. For years the resulting crash silently
  killed the app - indistinguishable from a successful close - until 0.8.2's
  crash handler caught it in the act (its first real-world catch) and beta
  dogfooding read the log it wrote. The re-close now waits its turn.

## [0.9.0-beta2] - 2026-08-30

Two beta-1 dogfooding reports, same day.

### Fixed
- **Cursor tracking now covers code.** The inline-code toolbar button lights
  when the caret sits in an inline code span (and the Format menu item shows a
  checkmark), like the other mark buttons. And the Style dropdown no longer
  goes stale inside a code block whose language is not in its fixed list
  (mermaid, python, ...) - it falls back to the generic code entry instead of
  silently keeping the previous answer. That staleness predates beta 1; the
  new cursor tracking just made it visible.
- **Disabled toolbar buttons are unmistakably disabled.** WPF barely dims glyph
  text, so enabled Undo and disabled Redo looked nearly identical; disabled
  toolbar buttons now fade hard enough to read at a glance.

## [0.9.0-beta1] - 2026-08-30

**Secure Markdown** — password-protected encrypted documents — plus the
toolbar finally following the cursor. A prerelease for the usual reason: the
crypto core is exhaustively tested, but the dialogs and day-two flows want
real hands before the stable promote.

### Added
- **Secure Markdown (.mdenc): password-protected documents.** For passwords,
  account numbers, anything you'd rather not leave readable on disk — in a
  synced folder, a backup, or a lost laptop. AES-256-GCM under the hood, with
  the key derived from your password by Argon2id (the current best practice
  for resisting password-cracking hardware). Editing is unchanged: once open,
  an encrypted document is ordinary markdown — themes, spell check, printing,
  find all work; the title bar shows 🔒 [Encrypted]. The decrypted content
  lives only in memory.
  - **File ▸ Encrypt Document…** converts the open document: the encrypted
    file is written and verified first, and only then is the readable
    original removed. **File ▸ Convert to Unencrypted…** goes the other way,
    with a warning. **File ▸ Change Password…** re-keys on the spot. Save As
    offers *Secure Markdown* as a file type — picking it is the same as
    encrypting.
  - **There is no password recovery. None.** The dialog says so before you
    set one; it's true; write the password down somewhere safe.
  - **Crash protection keeps working — encrypted.** The unsaved-changes copy
    of an encrypted document is itself encrypted; the app never writes its
    plaintext anywhere. Recovery after a crash asks for the password.
  - Every save is transactional: write beside the file, prove the bytes
    decrypt to exactly what was meant, then swap atomically. A failed
    verification leaves the existing file untouched.
  - File ▸ Open lists only regular markdown by default; a Settings checkbox
    adds *.mdenc to the filter. Encrypted files always open via Open Recent
    or a typed name — and by double-click once you re-run **File ▸ Windows
    Integration ▸ Register as .md editor…**, which now also registers the
    .mdenc type. A renamed encrypted file is recognised by content, not
    extension.
  - Honest limits, stated plainly: removing the original is deletion plus a
    best-effort scrub — on an SSD, forensic recovery of the OLD file's blocks
    can't be ruled out ("no accessible copy" is the promise, not forensic
    erasure). And anything that can read this process's memory while the
    document is open can see the text — that's inherent to editing it.
- **Bold/Italic/Underline/Strikethrough buttons now follow the cursor**, the
  way Word's do: click into bold text and B lights up; at a bare caret they
  show what typing would produce, so Ctrl+B lights the button before any text
  exists; across a selection they're on only when ALL of it carries the mark.
  The Format menu shows checkmarks to match. (The Style dropdown always did
  this; the mark buttons never had it.)


### Fixed
- **"Add to Dictionary" and "Ignore All" now work in read-only windows.** Read-only
  guards the document, but it was also suppressing the entire spelling section of
  the right-click menu - in the Help viewer and any read-only file, a squiggled
  word offered no spelling actions at all. The dictionary actions change editor
  state, not the document, so they now stay live; suggestions still show (useful
  while reading) but are greyed out, since applying one would edit the document.

## [0.8.2] - 2026-08-26

Two field reports from the same user, same day. One is fixed; the other is now
diagnosable instead of silent.

### Fixed
- **Markdown Midget now appears in Windows' "Default apps" chooser.** Registering
  as the .md editor wrote everything "Open with" needs — which is why that always
  worked — but never the two registry entries the Windows 11 Default-apps page
  builds its list from (`RegisteredApplications` plus a `Capabilities` key). So
  the register flow would open that very page, tell you to pick Markdown Midget,
  and the list wouldn't contain it. It does now; re-run **File ▸ Windows
  Integration ▸ Register as .md editor…** once after updating to add the missing
  entries. Unregister removes them.

### Added
- **Crashes now leave evidence, and many no longer kill the app.** There were no
  global exception handlers at all: any unexpected error — including one queued up
  behind the scenes and set off by the nested message pump a file dialog runs,
  which is why a crash can appear "when the file explorer dialog comes up" — ended
  the process silently. Unexpected UI-thread errors are now logged to
  `%LocalAppData%\MarkdownMidget\crash.log` and, within limits, survived: you get
  a dialog naming the log file, the document stays put, and the unsaved-work backup
  keeps running. After three survived errors in one session the next one is allowed
  to end the app (still logged) rather than limping forever. Background-thread and
  un-awaited task failures are logged too. If the app has ever "just crashed" on
  you: update, reproduce, and send crash.log with the report.

## [0.8.1] - 2026-08-18

### Fixed
- **Tables now print the way they look on screen.** Printing strips background
  colours by default, and nothing pinned table colours on paper — so Midget
  Solarized's dark header with reversed text printed as plain black-on-white (or
  worse, near-invisible, depending on how the engine rescues light text), and no
  theme's row shading survived at all. The header row and the alternating stripe
  now print as the theme draws them, forced onto paper per-element with
  `print-color-adjust: exact`. Three new theme variables carry it
  (`--mdm-print-th-bg`, `--mdm-print-th-text`, `--mdm-print-row-alt-bg`), because
  printed body text is always dark, so dark themes need a **light** row stripe on
  paper even though their screen stripe is dark — their header keeps its dark
  screen look, which is one row per table and prints legibly. Both constraints
  are test-enforced, and the fix was verified against real PDF output under the
  PDF-export path's settings, differentially against the 0.8.0 bundle (the print
  dialog's checkbox path is covered by the same per-element rule). The rest
  of the page still prints light whatever theme is active. Custom themes that
  predate the new variables print with the Default palette's table colours.

## [0.8.0] - 2026-08-18

First stable release on the 0.8 line — the beta's content with the prerelease
flag dropped, after dogfooding. Everything new on this line:

- **Updates keep your place** — the restart carries your document and view mode.
- **Help ▸ Apply vX.Y.Z Update** in windows another window has already updated
  past: a one-click switch, nothing downloaded.
- **The About box shows installed vs running** whenever they differ.
- **Import your Word vocabulary** (Edit ▸ Settings…), one-way from CUSTOM.DIC.
- **Fixed:** dictionary words no longer vanish when another window writes the
  dictionary file (a bug dating to 0.5.0).

> Updating **from 0.7.0 or earlier** still uses the old updater, so that one
> update won't carry your document — from then on they all do. Updating from
> 0.8.0-beta1 exercises the new path for the first time.

See the beta notes below for the full detail.

## [0.8.0-beta1] - 2026-08-14

Quality-of-life around updates and spelling. A prerelease for the usual reason:
the headline paths (a two-window update, the Word-dictionary import through the
file picker) are reviewed and unit-tested but want real dogfooding before the
stable promote.

> **The two update features take effect from the *next* update onward.** An
> update is carried out by the version you are updating *from* — so the
> 0.7.0 updater, which predates them, ran the update that delivered this
> release, and one more "reopened with no document" is expected on the way
> in. From 0.8.0-beta1 on, updates keep your place.

### Added
- **One-click catch-up after an update.** Updating from one window replaces the
  installed program on disk, but other open windows keep running the old version
  until they restart. They now notice: **Help ▸ Apply vX.Y.Z Update** appears in
  each stale window and reopens it — same document, same view mode — using the
  version already installed. Nothing is downloaded, no update runs; it's a restart
  that keeps your place, asking about unsaved changes first exactly as closing
  does. Installed copies only: a portable exe never changes underneath a running
  window, so the item never appears there (the portable equivalent is designed in
  ROADMAP.md and lands separately).
- **The About box shows both versions when they differ.** "Version 0.7.0
  (installed)" was the running version only; when the disk has moved ahead, a
  second line now says so permanently — *Installed on disk: X — this window is
  still running Y* — instead of only as transient status text during a manual
  update check. The "already updated by another window" message now points at the
  new menu item instead of telling you to close and reopen by hand.
- **Import your Word vocabulary.** **Edit ▸ Settings… ▸ Import words from Word's
  custom dictionary** copies the words from CUSTOM.DIC into Markdown Midget's own
  private dictionary — years of "Add to Dictionary" clicks carried over in one go,
  with a count of what was new and what was already known. Strictly one-way:
  Word's file is only ever read, and the importer has no code path that could
  write it. Handles the encodings real CUSTOM.DIC files come in (UTF-16 with and
  without BOM, UTF-8, plain ASCII), and refuses mojibake rather than importing
  words that would silently bless misspellings forever.

### Fixed
- **Added dictionary words no longer vanish when another window writes the
  dictionary.** Since 0.5.0, every window held its own copy of the dictionary and
  wrote the whole file back on "Add to Dictionary" — so a word added in one window
  could be silently erased by an older window adding a different word later. Writes
  now merge with what is on disk (and windows pick up each other's words as they
  do), which matters rather more now that an import can add hundreds at once.

## [0.7.0] - 2026-08-13

First stable release on the 0.7 line — the beta's content with the prerelease flag
dropped, after dogfooding, plus one theme added since. Everything new on this line:

- **Themes**, with seven built in and support for your own. The full account is in
  the beta notes below: the palettes, the `custom\` folder, what a theme may and may
  not do, and why the validator over-rejects on purpose.
- **File ▸ New** opens a new window, **Settings** moved to the **Edit** menu, and a
  **What's New** changelog viewer (with the mascot flagging unread entries).
- **A seventh theme, Midget Solarized** — see below.
- Security: **DOMPurify 3.4.13** and **nanoid 3.3.18**, both detailed in the beta notes.

### Added
- **A seventh theme: Midget Solarized.** Solarized's warmth and family of hues, tuned
  for legibility rather than for low glare. Solarized Light deliberately runs *below*
  WCAG AA — body text at 4.13:1 — which suits some readers and loses others; this one
  spends that difference on contrast instead: body text **13.19:1** (near-black rather
  than grey-teal), headings **6.58:1** (a deeper, calmer blue in place of the vivid
  azure), a lighter ivory page with less sepia cast, alternating table rows you can
  actually follow down a column, and a dark table header with its text reversed out.
  Body prose also sits one step down at 15.04px — headings, table cells and code
  blocks keep their usual sizes, and printing is unaffected. The accents are *quieter*
  than Solarized's while the page contrast is *wider* — the two are not in tension.

See the beta notes below for the full detail.

## [0.7.0-beta1] - 2026-08-08

Themes, plus three smaller things that landed alongside them: File ▸ New opens a
new window, Settings moved to the Edit menu, and a What's New viewer. The minor
bump marks Themes — the largest single addition since the in-app updater, and the
first feature to bring its own security model (a CSS validator with a dedicated
safety scan, and a rewrite of the WebView2 network sandbox to enforce the network
half at the request layer rather than by pattern-matching text).

### Added
- **Themes.** **View ▸ Theme** recolours the editing surface, instantly, and
  remembers your choice. Six ship with the app — the original **Default**, plus
  **Dracula**, **GitHub Dark Dimmed**, **GitHub Light**, **One Light** and
  **Solarized Light**, all MIT with attribution. The markdown source view follows
  along, and so do mermaid diagrams; the menu bar, toolbar and status bar do not,
  because those are Windows' own furniture. **Printing ignores the theme entirely** —
  paper stays light whatever you pick.
- **Write your own.** **View ▸ Theme ▸ Open Themes Folder** opens a folder holding a
  commented `sample.css`: copy it, rename it, change the colours, and the new name
  appears in the menu without restarting. Anything in `custom\` is yours and is never
  written over; the six built-ins live one folder up and are refreshed when you
  update, so a fix to one reaches you — which also means edits to *them* are lost.

  A theme is checked before it is used, and one that can't be is greyed out with the
  line number and the reason in its tooltip. Three things it may not do: **reference
  anything off your machine** (`url(https://…)` is refused — a stylesheet that
  fetches is a stylesheet that reports; `url(data:…)` is fine), **match on what an
  attribute contains** (`[disabled]` is fine, `a[href^="https://"]` is not, and
  neither is `:has()` — selecting on document content is how a stylesheet reads a
  document back out), and **run script**, in any of the spellings that ever worked.

  The middle one refuses some perfectly harmless stylesheets too, and that is
  deliberate: the harmless shape and the harmful one are indistinguishable, and
  over-rejecting a theme costs a tooltip.

  A theme also can't switch off the app's own furniture — spelling squiggles,
  formatting marks and the table resize handle survive whatever it says about them.
- **What's New.** **Help ▸ What's New** opens this file, read-only, newest entry
  first — including this one. The mascot in the top-right corner opens the same
  thing when clicked, and carries a small gold asterisk whenever there's an entry
  for your current version you haven't opened yet. Opening it, from either place,
  clears the asterisk for that window.
- **File ▸ New** (Ctrl+N) now opens a **new window** with a blank document instead
  of replacing what's in the current one — matching Word rather than Notepad. There
  is no longer a discard prompt on New, because nothing about the window you're in
  changes.

### Changed
- **Settings…** moved from the **File** menu to the **Edit** menu.

### Security
- **DOMPurify 3.4.12 → 3.4.13** ([GHSA-55q2-fjhq-7xh7](https://github.com/advisories/GHSA-55q2-fjhq-7xh7),
  moderate) — a detached subtree could survive `IN_PLACE` hook removal still
  executable, i.e. a sanitizer bypass. DOMPurify is what filters raw HTML embedded
  in a markdown file, so this is squarely in the path untrusted documents take.
  **Not reachable as configured** — the app sets neither `IN_PLACE` nor any hook —
  but a patch to the thing standing between a stranger's file and the editor is
  worth taking on its own, without waiting to be certain it was reachable.
- **nanoid 3.3.16 → 3.3.18** ([GHSA-2v37-7h3g-55p8](https://github.com/advisories/GHSA-2v37-7h3g-55p8),
  high) — a custom generator could loop forever at size zero. Build-time only: it
  arrives under `postcss`, which never ships in the exe.
- **mermaid 11.16.0 → 11.16.1** — five advisories
  ([GHSA-c4c3-pg64-4m4v](https://github.com/advisories/GHSA-c4c3-pg64-4m4v),
  [GHSA-6x64-9x62-f2gx](https://github.com/advisories/GHSA-6x64-9x62-f2gx),
  [GHSA-3rrr-jr9j-h3q3](https://github.com/advisories/GHSA-3rrr-jr9j-h3q3),
  [GHSA-2v8p-3f2j-5mp7](https://github.com/advisories/GHSA-2v8p-3f2j-5mp7),
  [GHSA-rhh3-jpg6-66xh](https://github.com/advisories/GHSA-rhh3-jpg6-66xh)).
  Unlike the last two rounds of these, **three were genuinely reachable**: the
  contents of a `` ```mermaid `` block are rendered as written, so a markdown file
  from a stranger could inject CSS into the editor canvas, or hang the window with
  a two-line radar or chart diagram. Nothing could run script or reach
  outside the editor — diagrams are already rendered at mermaid's `strict` security
  level, and the menus, dialogs and toolbar are native Windows, not web content —
  but a hung renderer is a lost window, and that is worth a patch release on its
  own terms.

## [0.6.4] - 2026-08-06

### Fixed
- **Updating with several windows open now explains itself.** Update from one
  window, forget the others are open, press Update in a second, and it failed with
  a raw Windows message — *"Cannot create a file when that file already exists."*
  Nothing was wrong except that the update had already happened: that window was
  still running the older copy. It now says so, and tells you to reopen the window,
  before downloading anything. And where the update genuinely is needed but an
  older window still has the previous copy open, it steps around that file instead
  of colliding with it, retrying with a different name if a second window claims
  the one it picked. If it still can't get out of its own way it says which windows
  to close, instead of the Windows error. And if a program file appears at the
  install path while this window is mid-swap, it is compared against what was
  actually downloaded and signature-checked: identical means the update is simply
  done, however it got there; anything else is left strictly alone and reported,
  rather than being started as though it were the update. And an update that simply
  couldn't be applied now says so in a sentence — which file, that nothing has
  changed, and that a virus scanner is the usual reason and passes — instead of
  handing you Windows' own *"the process cannot access the file because it is being
  used by another process"*, which names no file at all. Nor is a successful update
  reported as a failure any more when the restart itself is what didn't work: it
  says the new version is installed and to start it from your shortcut.
- **The portable build hit the same wall differently** — *"The process cannot access
  the file because it is being used by another process"* — when the new version was
  already sitting next to the old one. If the file that's there is already exactly
  what we were about to write, it's simply started; if it's something else that's in
  use, you're told another window is running it rather than shown the raw error.
- **An update that fails partway no longer leaves you with nothing to launch and no
  idea what to do about it.**
  For a moment during the swap there is no program file in the folder, and if
  putting the old one back failed too — a virus scanner holding a just-renamed
  6.5 MB binary is the usual reason — that is where it stayed. It now waits and
  retries, then falls back to installing the new version instead, and only if
  neither will go does it give up: and then it tells you which file to rename to
  what, rather than reporting a handle it cannot do anything about.
- A failed update no longer leaves a ~6.5 MB staging copy behind in the install
  folder — and one left there by an earlier version is cleared out on startup. That
  cleanup stands down entirely when there is no program file where one is expected,
  since the files it would tidy away are the only ones left to recover from.

## [0.6.3] - 2026-08-02

First stable release on the 0.6.3 line: the beta's content, with the prerelease
flag dropped after the crash-recovery path was exercised for real — killed
mid-edit, relaunched, unsaved document handed back. Everything new on this line:

- **Unsaved work survives a crash.** While a document has unsaved changes a copy
  is kept in `%LocalAppData%\MarkdownMidget\backup`, and the next launch hands it
  back — still marked unsaved, still pointing at the file it came from, and
  nothing written to your file until you say so. Documents that were never saved
  anywhere are kept too. The copy goes the moment you save or close. Switch it off
  in **File ▸ Settings…** if you'd rather not have copies on disk.
- **Content dropped onto the editor now counts as unsaved**, because it is — it
  exists only in that window. Closing asks before discarding it, and the crash
  copy covers it.
- **Help ▸ About no longer offers a prerelease that a release has overtaken**, so
  a long-superseded beta stops presenting itself as the newer, bolder build. A
  release whose tag says beta can no longer be mistaken for a stable one either.

See the beta notes below for the full detail.

### Security
- **dompurify 3.4.11 → 3.4.12** ([GHSA-c2j3-45gr-mqc4](https://github.com/advisories/GHSA-c2j3-45gr-mqc4)) and **postcss → 8.5.25**
  ([GHSA-r28c-9q8g-f849](https://github.com/advisories/GHSA-r28c-9q8g-f849)). Neither is exploitable as this app is configured — the
  DOMPurify issue needs an `afterSanitizeElements` hook and custom-element
  allowlist that Markdown Midget doesn't use, and postcss is a build-time
  transitive dependency that never runs and isn't in the shipped bundle. Taken
  regardless, because DOMPurify is what stands between embedded raw HTML and the
  editor document.

## [0.6.3-beta1] - 2026-08-02

### Added
- **Unsaved work survives a crash.** While a document has unsaved changes, a copy
  is written to `%LocalAppData%\MarkdownMidget\backup` every few seconds. If the
  app or the machine goes down, the next launch hands the work back - still marked
  unsaved, still pointing at the file it came from, and nothing is written to your
  file until you say so. Documents that were never saved anywhere are kept too.
  The copy is deleted the moment you save or close, so anything left behind means
  a session that ended badly. Turn it off in **File ▸ Settings…** if you'd rather
  not have copies on disk.

### Changed
- **Content dropped onto the editor now counts as unsaved**, because it is - it
  exists only in that window. Closing asks before discarding it, and the crash
  copy above covers it. Previously it was treated as already-saved, so both
  silently skipped it.

### Fixed
- **Help ▸ About no longer offers a prerelease that a stable release has already
  overtaken.** GitHub keeps reporting the newest prerelease forever, so the box
  went on advertising `v0.6.0-beta2` long after 0.6.2 shipped - reading as the
  newer, bolder build when it was in fact older code. A prerelease is now shown
  only while it leads both the version you're running and the newest stable, and
  the line disappears entirely when it doesn't.
- **A release whose tag says beta can no longer be mistaken for a stable one.**
  The prerelease flag is set by hand when publishing and has been wrong before; a
  version tail like `-beta2` is now enough on its own.

## [0.6.2] - 2026-08-01

### Added
- **The window remembers where you left it.** Size, position and maximized state
  are restored on launch instead of resetting to a centred 1120x720 every time.
  The saved rectangle is checked against the monitors that actually exist first,
  so a window last used on a monitor that has since been unplugged comes back
  where you can reach it rather than off-screen - and it's the title bar that has
  to be reachable, not just any part of the window. Mixed-DPI desks are handled:
  the position is remembered in real pixels, so a window on a 150%-scaled display
  comes back the size you left it. The window also gains a sensible minimum size.
- **Word and character count** in the status bar, updating as you type. Markdown
  syntax a writer doesn't think of as words - `##`, `-`, `>`, `|`, `---` - is not
  added to the total.
- **File ▸ Settings…**, holding the settings that don't suit a menu:
  - **How many files to keep in Open Recent** (1-50, was fixed at 5; now 10 by
    default). Lowering it shortens the menu straight away but doesn't discard the
    history, so raising it again brings the older entries back.
  - **What to open on startup**: a new blank document with the cursor already in
    it, or the no-document placeholder (the existing behaviour, still default).
  Toggles you flip while working - spell check, word wrap, auto-reload, document
  width - stay on the View menu where they're one click away.

### Fixed
- **Opening a document now puts the cursor in it too.** 0.6.1 fixed this for File ▸
  New; the same first-keystroke loss applied to File ▸ Open, Open Recent, the
  splash's Open link, a file dropped on the window, and a document passed on the
  command line. Auto-reload deliberately still does *not* take focus — it happens
  in the background and shouldn't pull you out of whatever you were doing.

## [0.6.1] - 2026-07-30

### Fixed
- **A new document now has the cursor in it.** File > New (and Ctrl+N, and the
  New link on the empty-document splash) left keyboard focus on whatever you
  used to invoke it, so the first thing you typed went nowhere and you had to
  click into the blank document first. The caret is now placed in the document
  itself, in both the formatted and source views. The focus is applied after the
  menu finishes closing, because a closing menu hands focus back to its owner and
  would otherwise undo it.

## [0.6.0] – 2026-07-30

First stable release on the 0.6 line — the two betas' content with the
prerelease flag dropped, after dogfooding. Everything new on this line:

- **In-app updates** with a real About box (Help ▸ About Markdown Midget):
  copyright and licence links, the running version and its install shape, the
  newest **release** and **prerelease** listed separately, and one-click updates
  that verify the download's Funcular Labs signature before installing anything.
  Installed copies update in place and restart; portable copies stay portable.
- **Spelling menu fixes** — the actions now key off the range the checker
  actually flagged, so a squiggled word always offers something useful: words the
  checker can't correct still offer **Add to Dictionary**, hyphenated and quoted
  words no longer lose their neighbours when a suggestion is applied, misspellings
  inside table cells get a **Spelling** submenu, and a repeated word offers
  **Delete Repeated Word** instead of corrections that break the sentence.

See the beta notes below for the full detail.

## [0.6.0-beta2] – 2026-07-29

### Fixed
- **The spelling menu could offer no actions at all on a word it had squiggled** —
  no suggestions, no **Add to Dictionary**, no **Ignore All**. In the source view
  the menu re-checked the clicked word *on its own* before offering anything, but
  squiggles come from checking the whole document, and errors that only exist in
  context — a repeated word, `the the` — simply aren't errors when the word is
  examined alone. The check disagreed with the squiggle, so the entire spell block
  was dropped. The menu now uses the range the checker actually flagged.
- **Applying a suggestion could eat neighbouring text**, and **Add to Dictionary
  could silently fail to clear a squiggle.** The source view re-derived the word
  under the cursor with its own rules, which treated `-` and `'` as part of a word
  while the checker does not. Correcting `state-of-the-artz` replaced the whole
  hyphenated phrase instead of `artz`, and adding `'wurdxqz'` to the dictionary
  stored it with the quotation marks — a token the checker never matches, so the
  squiggle stayed put forever. Both now use the checker's own word boundaries.
- **A misspelling inside a table cell offered no spelling actions.** Right-clicking
  a squiggled word in a table surfaced the table's insert/delete/select commands
  and nothing else, because the table menu was chosen before the misspelling was
  ever looked up. The table menu now grows a **Spelling** submenu when the click
  landed on a flagged word, so both sets of commands stay available.
- **A repeated word offered corrections that broke the sentence.** The checker
  flags the second word in `the the`, but that word is spelled perfectly — so its
  "corrections" were *them*, *then*, *they*, and accepting one silently rewrote the
  sentence. **Add to Dictionary** was worse: it would have permanently added a
  common word to your dictionary and suppressed every later warning about it. A
  repeated word is now recognised as such and offers the one thing that helps,
  **Delete Repeated Word**.

### Changed
- Context menus opened over the editor now pre-highlight the first item you can
  actually use, instead of the first item outright — menus that lead with a
  disabled entry (the "(no suggestions)" placeholder) opened with nothing
  highlighted and needed an extra key press to get moving.

## [0.6.0-beta1] – 2026-07-17

### Added
- **In-app updates** (Help ▸ About Markdown Midget). The About box now shows the
  running version plus the newest available **release** and **prerelease** —
  listed separately so choosing an early build is always an informed choice —
  with one-click **Update** buttons when something newer exists.
  - **Installed** copies (the *Register as .md editor* AppData install) update in
    place: the exe is swapped, shortcuts and the Open-with registration are
    refreshed, and the app restarts.
  - **Portable** copies stay portable: the new version downloads into the same
    folder the current exe runs from and starts; nothing else on the machine is
    touched, and the old exe remains as a file you can delete.
  - **Every download is signature-checked** (full Authenticode verification, and
    the signer must be Funcular Labs) before it is started or installed — a
    corrupted or tampered download is refused outright.
  - A quiet **"Update available"** status-bar note appears at startup when a
    newer version exists (prereleases only suggested to prerelease users).
- The About box also gains the identity it always should have had: **© Funcular
  Labs, Inc.** (linking to the company GitHub), an **MIT License** link, and the
  current version with its install shape (installed vs portable).

### Notes
- The ROADMAP gains a **real installer/uninstaller** entry (MSI/MSIX, Someday) —
  the in-app updater is the bridge until that lands.

## [0.5.1] – 2026-07-17

Stable promotion of **0.5.0-beta1** after dogfooding — same content, prerelease
flag dropped. See the 0.5.0-beta1 notes below for everything new on the 0.5
line: the app-owned spell-check stack (private dictionary, both views, code
exempted, right-click suggestions) and auto-reload of externally-changed files
with topic-anchored position restore.

## [0.5.0-beta1] – 2026-07-17

Markdown Midget now owns its spell-check stack. The minor bump marks the
replacement of both views' native spell checking with the app's own engine and
private dictionary — plus the auto-reload feature for externally-rewritten files.

### Added
- **The app's own spell checker, in both views.** Checking is done by the
  Windows spelling engine (`ISpellChecker`) driven by the app, so both the
  WYSIWYG view and the raw source view get the same squiggles, the same
  suggestions, and the same dictionary — the source view previously had
  all-or-nothing native checking with no way to exempt code.
  - **Right-click a squiggled word** for suggestions, **Add to Dictionary**, and
    **Ignore All** — in both views. The WYSIWYG view previously had *no* spelling
    suggestions at all (its context menu was app-drawn).
  - **The dictionary is private to Markdown Midget**
    (`%LocalAppData%\MarkdownMidget\dictionary.txt`). Adding a word never writes
    to the Windows or Office custom dictionaries — integrating with the OS
    dictionary was deliberately rejected as too invasive.
  - **Skip Spell Check in Code now works in the source view too**, exempting
    fenced blocks (including unclosed ones being typed) and inline code via real
    parsing — and in the WYSIWYG view it's exact by construction, driven by the
    document's node structure.
  - Engineering notes: squiggle positions round-trip through a plain-text ↔
    ProseMirror segment map (verified across headings, lists, tables, links, and
    mark-split words); late check results are rebased through an edit `Mapping`
    so a slow check can't squiggle the wrong text; and only the viewport's worth
    of decorations is live at a time (a whole-document decoration pass on a 50k
    doc costs ~14ms per keystroke; a viewport's worth costs ~1ms).
- **`--source` startup switch** — open a document showing the raw markdown.
- **Auto-reload changed files** (View ▸ Auto-reload changed files, on by default).
  When another program rewrites the open document — an AI tool regenerating it, a
  build step, a `git pull` — Markdown Midget now reloads it silently and **keeps
  your place by topic**, instead of interrupting with a dialog. A brief status note
  says what happened. Previously *every* external change wrote a timestamped `.bak`
  and demanded a click, even when you had nothing unsaved and the backup was a
  byte-for-byte copy of the file it was "protecting".
  - **Your place is remembered as a topic, not a line number**, because a
    regenerated document shifts every line. It re-finds the heading you were under
    (disambiguating repeated headings), refines to the exact line when that line
    survived, and falls back to a proportional position only when nothing
    recognizable remains. Works in both the WYSIWYG and source views.
  - **Unsaved changes are never silently replaced.** The auto-reload only ever
    happens when nothing is unsaved; if you have edits, you still get the backup and
    the prompt exactly as before. No setting can override that.
  - Turn the setting off to get the old always-prompt behavior back.
- **Word-wrap toolbar button** for the source view (View toolbar), disabled and
  showing "off" in the WYSIWYG view where wrapping doesn't apply.

### Changed
- The source → formatted toggle tooltip now reads **"Switch to formatted / WYSIWYG
  view"**, so the destination is unambiguous.

### Fixed
- **An externally changed file could be missed entirely** if the writing program
  still held the file when we tried to read it: the read failed and we relied on
  another filesystem event that might never arrive. It now retries.

## [0.4.1] – 2026-07-15

Small feature release on top of 0.4.0, and the first to drop the prerelease flag
on the 0.4 line. Adds source-view word wrap and a clearer source ↔ formatted
toggle icon, and hardens the 0.4.0 image/HTML work with a regression-test net.
Dogfooded before release.

### Added
- **Word wrap in the markdown source view** (View ▸ Word Wrap). Long lines wrap to
  the pane width instead of scrolling off to the right; off by default and
  remembered between sessions. The WYSIWYG view always reflows, so this only
  affects the raw-source (Ctrl+E) view.
- **Regression tests for the 0.4.0 image/HTML path.** C# unit tests for the
  document asset-serving boundary — path-traversal containment (including
  percent-encoded separators and sibling folders sharing a name prefix), rejection
  of absolute/rooted references, and MIME mapping — plus JS tests for the raw-HTML
  sanitize policy (script / event-handler / `javascript:` / `iframe` / `base`
  stripped; presentational tags like a centered `<img>` logo preserved) running on
  Node's test runner against real DOMPurify in jsdom. Both suites run in CI and in
  the release workflow.

### Changed
- **Clearer source → formatted toggle icon.** The toolbar button that returns from
  the markdown-source view to the WYSIWYG view now shows a rendered-content glyph
  instead of a plain document page, so it reads as "formatted view" at a glance.

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

[Unreleased]: https://github.com/FuncularLabs/MarkdownMidget/compare/v1.0.0-rc1...HEAD
[1.0.0-rc1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v1.0.0-rc1
[1.0.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v1.0.0-beta1
[0.10.0]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.10.0
[0.10.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.10.0-beta1
[0.9.0]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.9.0
[0.9.0-beta4]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.9.0-beta4
[0.9.0-beta3]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.9.0-beta3
[0.9.0-beta2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.9.0-beta2
[0.9.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.9.0-beta1
[0.8.2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.8.2
[0.8.1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.8.1
[0.8.0]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.8.0
[0.8.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.8.0-beta1
[0.7.0]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.7.0
[0.7.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.7.0-beta1
[0.6.4]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.4
[0.6.3]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.3
[0.6.3-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.3-beta1
[0.6.2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.2
[0.6.1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.1
[0.6.0]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.0
[0.6.0-beta2]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.0-beta2
[0.6.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.6.0-beta1
[0.5.1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.5.1
[0.5.0-beta1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.5.0-beta1
[0.4.1]: https://github.com/FuncularLabs/MarkdownMidget/releases/tag/v0.4.1
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
