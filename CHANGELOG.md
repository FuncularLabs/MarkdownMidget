# Changelog

All notable changes to **Markdown Midget** are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/). While
under active alpha development (0.1.x), the minor version may carry breaking
changes between alpha tags.

## [Unreleased]

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
  wherever Windows last left the dialog.

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

[Unreleased]: https://github.com/FuncularLabs/MarkdownMidget/compare/v0.6.4...HEAD
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
