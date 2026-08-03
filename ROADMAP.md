# Roadmap / Wishlist

Not-yet-committed ideas and deferred work for **Markdown Midget**. This is a
living document — items move up, down, or off as priorities shift. Shipped work
lives in [CHANGELOG.md](CHANGELOG.md).

Rough buckets: **Next** (likely soon), **Later** (wanted, not scheduled),
**Someday / Big** (real projects), **Won't unless asked** (known limits we've
deliberately parked).

---

## Next

### Updating while several windows are open

Reported from real use: update from one instance, forget the others are open, hit
Update in a second one, and it fails with a raw Win32 message along the lines of
*"Cannot create a file when that file already exists."*

**The cause is understood**, so this doesn't need rediscovering. The installed flow
(`UpdateService.ApplyInstalledAndRestart`) renames the running exe out of the way:

```
File.Copy(new, staged)
try { File.Delete(target + ".old") } catch { }   // swallowed
File.Move(target, target + ".old")               // <-- throws here
File.Move(staged, target)
```

After the first instance updates, any still-running instance was launched from the
file that has since been renamed to `.old` — so that file is its own process image
and is locked. Its `File.Delete` of `.old` therefore fails (access denied) and is
swallowed, and the very next `File.Move(target, old)` throws, because a non-
overwriting Move onto an existing name is exactly that error. The message reaches
the user through `AboutDialog.Fail` as `Update failed: <Win32 text>`, with no
explanation. Same reason `CleanupOldBinaries` can't tidy up while a sibling is
still running — "the next launch gets it" only becomes true once every
old-version instance has exited. Two instances are enough to reproduce; the second
one's delete fails against *itself*.

Three facts that are easy to get wrong, all confirmed by probe:

- **`Environment.ProcessPath` does not follow the rename.** Windows lets you
  rename a running exe and the process survives, but `Environment.ProcessPath`
  and `MainModule.FileName` — the two sources behind
  `UpdateService.CurrentExePath`, both `GetModuleFileName` underneath — keep
  reporting the *original* path, while `QueryFullProcessImageName` follows to
  `.old`. This is load-bearing and counter-intuitive: `target` resolves to the
  canonical path (now holding the NEW exe) while the process is actually
  executing the `.old` file. It is also what makes the first fix below
  implementable — reading the version off `CurrentExePath` in a stale instance
  genuinely returns the new version.
- **The portable flow has the same bug in a different shape** — same collision,
  but no rename and no `.old` involved. `ApplyPortableAndRestart` copies to a
  version-stamped name in the current directory; if a sibling is already running
  that exact file, `File.Copy` throws *"The process cannot access the file …
  because it is being used by another process."* If that sibling has since
  exited, the copy succeeds and simply starts a *second* copy of the new version
  — no error, but still not "the application updated". Note the detection below
  is phrased for the installed flow and won't fire here, because a portable
  instance's `CurrentExePath` really is the old exe; portable needs a different
  signal (a versioned sibling already present, or already running).
- **The failure leaks `.mdm-update-staged.exe`** into the install directory,
  because the throw happens before any cleanup.

What it should do instead:

- **Notice, and say something useful.** If the exe on disk is already the version
  being offered, this instance doesn't need updating — it needs restarting. Say
  that ("Markdown Midget was already updated by another window; restart this one to
  pick it up") rather than attempting a swap that can't work.
- **Have the other instances update themselves.** After a successful swap, the
  updating instance should tell its siblings; each reopens its own document under
  the new exe. The user updated the *application*, so all of its windows should end
  up on the new version without hunting them down.
- **Don't lose unsaved work doing it.** Notepad++'s implicit save is the model —
  a window with unsaved changes should come back with those changes still there and
  still unsaved, not prompt per window mid-update. **The crash-recovery store from
  0.6.3 already is this mechanism**: snapshot, relaunch, adopt. Reuse it rather than
  inventing a second way to persist unsaved buffers; the difference is only that the
  restart is deliberate rather than a crash, so the handoff can be orderly (snapshot
  every window, then relaunch them) instead of inferred from an abandoned lock.
- Notepad++ mostly sidesteps this by being single-instance; SDI means we can't. Any
  window that fails to come back must leave its snapshot on disk rather than
  vanishing — the existing recovery path then catches it on the next launch.

Depends on the same cross-instance registry as
[multiple instances behaving like one application](#make-multiple-instances-behave-like-one-application):
knowing which instances exist and what each has open is the prerequisite for both
telling them to restart and knowing whether any of them still needs to.

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

### Make multiple instances behave like one application

**Staying SDI.** One document per window is the deliberate choice, and tabs are
explicitly not wanted right now — the point is that each document is a real
window the OS can tile, snap, alt-tab and put on its own monitor. What's missing
is that the separate instances don't behave as though they belong to the same
application.

Concrete things that fall out of being N unrelated processes today:

- **Shared state is last-writer-wins.** `settings.json` is merged on write and
  the crash-backup store coordinates through lock files (0.6.3), but that's two
  bespoke solutions to the same problem. A third shared thing will want a third.
  Worth deciding whether there should be one small "shared user state" layer
  before adding one.
- **The same file can be open in two windows**, each with its own unsaved edits,
  each happily saving over the other. The external-change watcher notices *after*
  the fact and offers a merge-ish prompt; nothing notices *before*. Opening a
  file already open elsewhere should at least surface that — ideally focus the
  window that has it, the way most SDI editors do.
- **No window list.** With six documents open there's no way to get from one to
  another except the taskbar. A Window menu listing the open documents (and
  focusing one) is the SDI answer to tabs, and needs the same cross-instance
  registry as the point above.
- **Crash recovery restores into new windows** (0.6.3) but has no notion of the
  arrangement they were in — sizes and positions are per-window and the last one
  to close wins the saved rectangle.

The shared thread is that every one of these needs instances to know about each
other. A named mutex or pipe plus a small registry of {pid, document path,
window handle} would cover all four; that's the piece to design, not the
individual symptoms.

If tabs ever do arrive they should be a *view* over that same registry — never
the reason to collapse back to a single process.

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

