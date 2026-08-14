# Roadmap / Wishlist

Not-yet-committed ideas and deferred work for **Markdown Midget**. This is a
living document — items move up, down, or off as priorities shift. Shipped work
lives in [CHANGELOG.md](CHANGELOG.md); anything planned in detail lives in
[docs/plans/](docs/plans/).

Rough buckets: **Next** (likely soon), **Later** (wanted, not scheduled),
**Someday / Big** (real projects), **Won't unless asked** (known limits we've
deliberately parked).

---

## Next

### Themes — shipped in 0.7.0

Delivered: **View ▸ Theme**, seven built-in palettes, user CSS in `themes\custom`,
and invalid files listed-but-disabled with the first error in the tooltip. The
user-facing account is in [CHANGELOG.md](CHANGELOG.md); the design record — the
custom-property extraction, the cascade-layer order, the validator's deliberate
over-rejection and the network rule moving to the request layer — is kept in
[docs/plans/themes.md](docs/plans/themes.md).

Left here only as the open tail: **Midget Solarized is the one built-in carrying a
non-`:root` rule** (a screen-only body font-size). If a second theme ever wants
typography as well as colour, that is the point to decide whether size becomes part
of the theme contract rather than an ordinary rule each theme repeats.

### Updating while several windows are open

**The failure itself is fixed in 0.6.4; the ergonomics are still open.** A window
that has nothing to do now says so and tells you to restart it — worked out before
downloading anything — and a window that genuinely does need the update steps
around an older window's parked copy rather than colliding with it, re-deciding
the name if it loses the race for one. What remains needs the cross-instance
registry: siblings still don't update themselves, and a window that needs
restarting still has to be restarted by hand.

Original report: update from one instance, forget the others are open, hit Update
in a second one, and it fails with *"Cannot create a file when that file already
exists."*

**The cause is understood**, so this doesn't need rediscovering. It is kept here as
diagnosis — the code below is how `ApplyInstalledAndRestart` looked *before* 0.6.4,
which renamed the running exe out of the way like this:

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
  signal. **Which signal is now settled — see [the 2026-08-13 decision](#decided-2026-08-13--how-a-portable-sibling-learns-it-is-superseded);
  the two candidates floated here, a versioned sibling present on disk or one
  already running, were both considered and rejected there.**
- **The failure leaked the staging copy** into the install directory, because the
  throw happened before any cleanup. Fixed in 0.6.4, along with a startup sweep for
  the ones earlier versions left behind.

What it should do instead:

- ~~**Notice, and say something useful.**~~ **Done in 0.6.4** — with one correction
  worth keeping: "is the exe on disk already the version being offered" is not
  enough. The real precondition is that the disk has moved past what we are
  *running*, which also catches a window left open across two releases.
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

#### Three pieces that do NOT need the registry (raised 2026-08-13; shipped 0.8.0-beta1 except where noted)

Most of the value above is reachable without knowing which instances exist, because
a sibling can **poll** instead of being **told**. Proven right: all three shipped in
0.8.0-beta1 with no registry, leaving it an optimisation. What remains open from
them: the **unsaved-changes handoff** (the update flow still prompts rather than
snapshotting through the crash-recovery store) and the **portable marker** (the
"Decided 2026-08-13" design below — installed mode ships first, and the menu item
simply doesn't appear for portable copies until the marker lands).

- **Reopen the document across an update-restart.** Today both restart paths call
  `Process.Start(exe)` with **no arguments** (`UpdateService.cs`, in
  `ApplyInstalledAndRestart` and `ApplyPortableAndRestart`), so the open file is
  simply dropped and the new instance lands on the splash. The mechanism already
  exists — `OpenInNewInstance` passes a quoted path, and startup already honours a
  file argument via `_pendingOpenPath`. Pass the current document's path through the
  relaunch. Unsaved changes are the harder half and are covered above: snapshot via
  the crash-recovery store first, then relaunch, so the buffer comes back still
  unsaved rather than prompting mid-update.

- **"Help ▸ Apply vX.Y.Z update" in the siblings.** When another instance has
  already swapped the binary, an old window should offer a one-click relaunch into
  it — no download, no signature check, no update flow, because the new file is
  already there and was verified by whoever installed it. **The detection is already
  written and shipping**: `UpdateService.AlreadyUpdatedOnDisk` /
  `UpdateOffer.NeedsRestartNotUpdate` compare the on-disk version against the
  running one, and the About box already renders the conclusion as *"Already at
  {onDisk} on disk — restart this window to pick it up."* What is missing is only
  that it fires **once, inside a manual update check**, rather than being surfaced
  proactively. Poll it on a timer (or on window activation) and light up the menu
  item; the action is the same relaunch-with-document as above.

  **Caveat that will bite if unnoticed: this works for installed copies, not
  portable ones.** An installed update swaps the file at one canonical path, so a
  stale instance reading `VersionOnDisk()` off its own `CurrentExePath` sees the new
  version — that is exactly why the detection works today. A *portable* update
  writes the new exe under a **different filename** beside the old one and leaves the
  old one in place, so the stale instance's path still reports its own old version
  and the check is silently always-false. Portable needs its own signal, or the
  feature ships working for half the users with nothing to indicate which half.

#### Decided 2026-08-13 — how a portable sibling learns it is superseded

Marker as the trigger, signature check at apply time, no scanning. Recorded with
the reasoning, because the rejected option is the one that looks cheaper.

*Why not scan the folder for a newer `MarkdownMidget-v*.exe`.* It is perfectly
feasible — `ApplyPortableAndRestart` names the new exe after the release asset, so
the pattern is predictable — and that is the problem. A portable folder is very
often **Downloads**. Anything landing there called
`MarkdownMidget-v9.9.9-win-x64-net10.exe` would be picked up and offered by the app
itself as a one-click "Apply v9.9.9 update": a social-engineering path into running
an arbitrary binary, inside the one feature whose premise is that nothing runs
without a valid Funcular Labs signature. Scanning *plus* verifying each candidate
would be safe, but then `WinVerifyTrust` builds a trust chain per file, repeatedly,
on a timer, against a directory that may be huge and on a USB stick or a network
share. Its only unique coverage is "the user manually dropped a newer exe in" —
where they can simply double-click it. Worst risk-and-cost for the least gain.

*Why not "a sibling process already running a newer version"* — the other candidate
floated earlier in this section. It is appealing because it carries no new state and
cannot go stale. It loses on two counts. It is **ephemeral**: it only holds while
that newer instance is still open, so the ordinary sequence — update, keep working
in the new window, close it, come back to the old one — leaves nothing to detect,
which is precisely the case the feature exists for. And it is **no more trustworthy
than a file**: any process can be named `MarkdownMidget`, so it would still need the
same apply-time signature check, while adding process enumeration that can be denied
outright for processes owned by another user or running elevated.

*The marker is a **fact**, not a task.* Not "an update is pending" — that is a task,
which goes stale, needs cleaning up, and eventually has something skip work on the
strength of it. Instead: *"the newest exe I installed is at path P, version V."* A
sibling compares V against its own running version; once V is no longer newer the
marker is simply irrelevant. No cleanup, no staleness bug, and nothing ever skips
work because of it — it only ever *offers* work, so a stale marker degrades to "the
menu item doesn't appear" or "the apply-time check refuses", never to data loss.

| | |
|---|---|
| Lives in | `%LocalAppData%\MarkdownMidget\` — **not** beside the exe; the portable dir may be read-only, as `ThemeStore.ResolveRoot` already has to handle. Precedent: `install-info.json` |
| Contains | absolute path of the new exe, its version, timestamp |
| Written | inside `ApplyPortableAndRestart`, after the copy block — **including when the copy was skipped** because a sibling had already written identical bytes, since the fact is still true |
| Scoped | a sibling acts only if the recorded path is in **its own** directory, so two portable copies in different folders don't cross-offer |
| Apply-time gate | re-run the existing `UpdateService.VerifySignature(path, out signer)` before `Process.Start` — **non-negotiable** |

The apply-time check is the actual security boundary, and it is why the marker does
not have to be trusted: the marker says *where to look*, the signature says *whether
to run it*. Once per click, so the cost that ruled out scan-as-trigger does not
apply. `VerifySignature` already exists and already does the full Authenticode plus
Organization check.

**Do not later "unify" installed mode onto the marker.** Installed keeps its
existing `VersionOnDisk()` check, which is strictly stronger — it reads the actual
file that will run, so it cannot go stale and needs no cleanup. The asymmetry is
deliberate: each mode uses the strongest signal available to it. Tidying the two
into one path regresses the better half.

If the marker is lost (profile cleared, a different user account), portable simply
never offers the menu item and the user does what they do today. Acceptable
degradation, and stated so nobody treats its absence as a bug.

- **Show installed vs running in the About box when they differ.** Currently it shows
  the running version only (`Version 0.7.0  (installed)`), and the on-disk version
  appears solely as transient status text during a check. When the two differ, both
  belong on screen permanently — that is the state where a user is most likely to be
  confused about what they are actually running, and it is the same comparison the
  menu item above keys off.

Beyond those three, the rest — pushing an update to siblings rather than having them
notice it — depends on the same cross-instance registry as
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

All sized and ordered in
[docs/plans/queued-features.md](docs/plans/queued-features.md), which also explains
why the cross-instance registry is worth pulling forward — three of these wait on
the same missing piece — and how the work should land: one user-visible feature per
release, with infrastructure riding along with whatever needs it.

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

