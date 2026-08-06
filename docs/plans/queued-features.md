# Queued features: evaluation and sizing

Companion to [themes.md](themes.md), which is the one item planned in full because
it's the active request. This covers everything else currently queued in
[ROADMAP.md](../../ROADMAP.md): what it costs, what it depends on, and what order
makes the work cheapest.

Sizes are rough day-counts for someone who knows this codebase, not estimates for a
schedule. "Blocked on" means genuinely blocked, not merely nicer afterwards.

---

## The dependency that reorders everything

Three separate queued items all need the same missing piece — **instances knowing
about each other**:

- *Updating while several windows are open* (Next) — must tell siblings to restart
- *Make multiple instances behave like one application* (Someday) — is that piece
- *Real installer* — needs "is another copy running?" before replacing files

Built once, it unblocks all three. Built three times, it's three protocols that
disagree. **The cross-instance registry is the single highest-leverage thing in the
queue**, even though nothing lists it as a user-facing feature.

It is smaller than its Someday bucket implies, but **not as small as "a file with
a pid in it"** — the naive version fails all three use cases, so the design is worth
writing down before the estimate is:

- **Two files per instance, not one.** `BackupStore` is the precedent and it
  deliberately splits them: an empty `{id}.lock` held `FileShare.None` +
  `DeleteOnClose` (`BackupStore.cs:85`) for liveness, and separate readable files
  for the payload. Collapsing them defeats the point — a registry entry held
  `FileShare.None` cannot be *read* by the siblings that need it, which is exactly
  what window-list, focus-existing-window and duplicate-open detection all do.
  Liveness from the lock, `{pid, document path, window handle, version}` from a
  sibling file opened `FileShare.Read`.
- **Discovery is not messaging.** The Next item needs to *tell* siblings to restart;
  a directory of files only lets them be found. That needs a signal — a
  `FileSystemWatcher` on a command file per instance is the cheapest option that
  matches machinery already in this codebase (the external-change watcher), with a
  named event as the alternative. Whatever it is, each sibling has to react
  mid-session with unsaved work in flight, which is the actual work.
- **Cross-version protocol.** Entries carry `version` precisely because update
  orchestration is the mixed-version case: an older instance will read a newer
  instance's entry. Define now what it does with fields it doesn't understand
  (ignore and keep the entry) rather than discovering it during an update.
- **The installer case is only partly served.** "Is another copy running?" for a
  per-user install works. An *elevated* or per-machine installer runs as a different
  user and cannot see `%LocalAppData%\MarkdownMidget\instances\` for the invoking
  user, so that path needs a machine-visible signal (a global mutex) instead. Worth
  knowing before the installer work leans on this.

**Sizing.** Liveness + readable shared state: 2-3 days with tests. Adding the
messaging channel and restart orchestration with the unsaved-work interlock:
another 2-3, and that second half **is** the restart orchestration the Next item
needs — don't budget it twice. Call it a week for the piece that unblocks all three,
not the 2-3 days a bare discovery registry would suggest: 0.6.3 proved *liveness
detection*, which is the part already solved, not identity sharing or messaging.

**Recommended order:** registry -> update-with-multiple-windows -> everything else.

---

## Next

### Updating while several windows are open
**Size:** 1 day once the registry has messaging. The cheap half is done (0.6.4).
**Blocked on:** the registry, for the "tell the siblings" half — all that remains.

The failure is fully diagnosed in the ROADMAP entry — including the counter-intuitive
part, that `Environment.ProcessPath` does not follow the rename. Two halves:

- ~~*The bad error message*~~ — **shipped in 0.6.4.** One correction from doing it:
  comparing the disk against the *offered* version is not enough. The real
  precondition is that the disk has moved past what we are **running**, which also
  catches a window left open across two releases (disk 0.6.4, us on 0.6.3, 0.6.5
  offered) — that case still produced the original error under the narrower rule.
- *Siblings updating themselves* needs the registry, and gets its unsaved-work
  handling free from the 0.6.3 backup store (snapshot → relaunch → adopt is already
  built; a deliberate restart is the easy case of what it does for a crash).

Both of the side issues that rode along here — the leaked staging copy and the
portable flow's differently-worded failure — shipped in 0.6.4 too, as did stepping
around an older window's parked copy rather than colliding with it.

---

## Later

### Find & Replace
**Size:** 2–3 days. **Blocked on:** nothing. **Highest user value in the queue.**

Find already carries the hard parts: `FindEngine` with 4 modes (Normal / Extended /
Wildcards / Regex), the match-index over both surfaces, F3/Shift+F3, "match m of n".
Replace adds a second tab, Replace / Replace All, and scope-to-selection.

The real work isn't the UI, it's that **replace has to mutate two different
surfaces**: the WPF `TextBox` in source view, and ProseMirror in WYSIWYG, where a
replacement is a transaction against positions that shift as you go. The spell-check
work already solved exactly this shape. **`MDM.replaceRange(from, to, text, expected)`
(`main.js:528`) is the Find & Replace primitive already sitting in the tree** — it
re-verifies the range against the expected text before dispatching, which is
precisely what Replace needs, and `MainWindow.Spell.cs:263` already drives it.
`SpellTextMap` exists because character offsets and PM positions diverge across
inline leaves. Reuse both rather than rediscovering them. Replace All in WYSIWYG
must apply as one transaction (one undo step), working backwards through the
document so earlier positions stay valid.

Ship this next if the goal is "what will users notice".

### Spell-check follow-ups
**Size:** language selection 1–2 days; CUSTOM.DIC import ~1 day. **Blocked on:** nothing.

Language selection is mostly plumbing — the Windows `ISpellChecker` factory already
takes a language tag; the work is enumerating installed languages, a Settings entry,
persistence, and re-running the check on change. The dictionary is currently a
single `dictionary.txt`; per-language files are the obvious shape.

The CUSTOM.DIC import is small and one-way by design. Keep the "import only, never
write back" rule prominent in the code, not just the roadmap — it's the sort of
constraint a later change erodes by accident.

### Editor bundle lazy-load
**Size:** 1–2 days. **Blocked on:** nothing. **Do it before the .NET 8 matrix.**

Mermaid took the bundle from ~560 KB to 3.9 MB and the exe from 2.9 to ~6.5 MB.
Code-splitting it means esbuild emits ESM chunks. `ExtractEmbeddedEditor`
(`MainWindow.xaml.cs:258`) already globs every manifest resource under `wwwroot/`, so
it needs no change — the three fixed names are in the csproj's `EmbeddedResource`
item (`MarkdownMidget.csproj:47`), and that is what a chunked build has to become,
along with the `index.html` cache-busting hash.

Ordering note: this shrinks the artifact that the .NET 8 / self-contained matrix
would otherwise multiply. Doing it first makes every downstream build smaller.

### .NET 8 build + portable self-contained
**Size:** 1–2 days. **Blocked on:** nothing, but see ordering above.

Genuinely mostly configuration — the code already compiles for net8. csproj
multi-target, extra publish profiles, release-workflow matrix, and **more release
assets**, which is the part with hidden cost: `ReleaseFeed.AssetSuffix` currently
matches exactly one asset name (`-win-x64-net10.exe`). Multiple assets per release
means the updater must choose the right one for the running configuration, or it
will offer users the wrong build. That is an update-path change, so it wants the
same care the 0.6.x update work got.

### Editor round-trip test harness
**Size:** 3–5 days, and open-ended. **Blocked on:** nothing. **Lowest urgency, highest insurance.**

The only queued item that buys no user-visible behaviour, and the one that would
have caught the most bugs. Load → edit → serialize through real Milkdown/ProseMirror
in jsdom is an integration harness, not a smoke test: every plugin has to mount, and
jsdom's gaps around layout and selection are where this kind of effort usually
stalls.

Worth it *before* any change to the parser/serializer or a Milkdown major upgrade,
and hard to justify otherwise. If it's attempted, timebox the "does jsdom host the
full editor at all" spike first — that question decides whether the rest is worth
starting.

---

## Someday / Big

### Make multiple instances behave like one application
**Size:** ~a week for the registry (see the top of this document — liveness is the
part 0.6.3 already proved; readable shared state, messaging and restart
orchestration are not). The features on top are small individually.

See the top of this document — this is the unblocker, and it should be pulled
forward out of "Someday". The registry alone is modest; what's genuinely "Big" is
the set of features it enables (window list, focus-existing-window-on-open,
duplicate-open detection), and those can land one at a time afterwards.

### Real installer / uninstaller
**Size:** 1–2 weeks including the spike. **Blocked on:** nothing, but wants the registry.

Unchanged from the ROADMAP assessment: MSIX vs WiX is a real fork that deserves its
own de-risk spike (WebView2 + file associations + "Open with default" UX), and both
artifacts have to be Authenticode-signed. The hard requirements — appwiz.cpl entry,
portable staying first-class — are settled; the delivery mechanism isn't.

The interaction with the in-app updater is the part to design first, not last: an
installed copy should service through the installer's channel while portable keeps
the in-app swap, which means "how was I installed?" needs a reliable answer.
`RegistrationService.IsRunningFromAppDataInstall()` answers today's version of that
question and will need extending rather than replacing.

---

## How these should land

**One user-visible feature per release. Not one per commit, and not one per stage.**

The evidence for it is this project's own history: 0.6.2 mixed three unrelated
additions and took six adversarial rounds to get clean, and 0.6.3's crash recovery
took seven. In both cases nearly every defect came from a *fix* to a previous
finding, not from the original work. When a release carries one concern, a
regression report points at one thing, `git bisect` lands somewhere useful, and the
release notes say something a user can act on.

Three refinements, because "one feature per release" taken literally goes wrong in
places:

- **Infrastructure rides with the feature it enables.** The theme work's stage 1 is
  a pure no-op CSS refactor; the cross-instance registry has no user-visible surface
  at all. Neither can headline a release — "this version changes nothing you can
  see" is not a release note. They land on master as their own commits, reviewed on
  their own, and ship with the first feature that needs them. Separate *commits*,
  shared *tag*.
- **Anything that touches every rendered pixel gets a beta first.** The layer-order
  change in theme stage 2 restyles the entire editing surface, and the parts most
  likely to break — vendor `!important` interactions, the mermaid frame, table cell
  padding — are exactly the ones that don't fail loudly. 0.6.3-beta1 worked well for
  this: it caught nothing, but it cost a day and made the stable tag a formality
  rather than a hope.
- **Small independent fixes shouldn't queue behind big features.** The update error
  message is a few hours and touches nothing else. Holding it for the release that
  finally fixes multi-window updating means users keep hitting a raw Win32 message
  for weeks longer than necessary. Ship it as a patch when it's done.

Applied to what's queued, that gives roughly:

| Release | Carries |
|---|---|
| ~~0.6.4~~ | ~~Update error message~~ — shipped |
| 0.7.0-beta1 → 0.7.0 | **Themes** — stages 1-6, including the CSS refactor and layer order |
| 0.8.0 | **Multi-window updating** — carrying the cross-instance registry that enables it |
| 0.9.0 | **Find & Replace** |
| then | bundle lazy-load → .NET 8 matrix (paired: the first shrinks what the second multiplies), spell-check language selection, installer spike |

Version numbers are illustrative — the point is the grouping, not the digits.

---

## Suggested order

1. ~~**Update error message**~~ — shipped in 0.6.4
2. **Themes** — the active request; see [themes.md](themes.md)
3. **Cross-instance registry** — unblocks three queued items; budget a week, not
   the 2–3 days a bare discovery registry suggests (see above)
4. **Update with multiple windows** — nearly free once (3) exists
5. **Find & Replace** — most user-visible value remaining
6. **Bundle lazy-load** → **.NET 8 / self-contained matrix**, in that order
7. **Spell-check language selection**
8. **Installer spike** (decide MSIX vs WiX before committing)
9. **Round-trip harness** — when a parser/serializer change or a Milkdown upgrade makes it necessary
