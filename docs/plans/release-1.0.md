# 1.0 — the release plan

Written 2026-09-10 from the v1.0 readiness audit, against master at `ad0f55e`
(v0.10.0-beta1). Test-first per the house rule: every acceptance criterion names the
test that proves it; each stage is one user-visible change with its infrastructure
riding along.

## What 1.0 means here

Not feature-maximal. **The tool does what its first paragraph promises**, with no
edge a newcomer hits in the first hour, and every deliberate limit written down.
The README's promise is "markdown as the native format, not a lossy import/export";
the audit's one measured finding is that this is true in content and false in form.
1.0 makes it true, or says exactly where it isn't.

## The audit's findings, corrected

| # | Finding | Status |
|---|---|---|
| 1 | Saving rewrites the user's markdown conventions, invisibly | **Confirmed, measured** (below) |
| 2 | Line endings are mangled; a UTF-8 BOM is dropped | **Measured, and worse than first stated:** structural line endings become LF, but endings *inside* code and HTML blocks stay as written, so a CRLF file with one code block saves with mixed endings. The BOM loss is by reading the save path (a default `StreamWriter`) |
| 3 | Find has no Replace | Confirmed |
| 4a | Pasting an image into the formatted view does nothing | **Wrong.** It works. Dogfood-verified, not code-verified: our code has no paste handler; ProseMirror's native paste capture turns the bitmap Chromium pastes into an image node |
| 4b | Dropping an image *file* on the editor opens it as text | Confirmed: `HandleDroppedContent` opens any dropped file as a document, after the discard prompt when there are unsaved changes |
| 4c | Pasting an image into the *source* view does nothing | Confirmed (AvalonEdit is text-only); see decision below |
| 5 | The same file open in two windows silently overwrites | Confirmed (roadmap already describes it) |
| 6 | No installer / Add-Remove entry | Confirmed; decision point below |

### The measured round-trip (shipped bundle, 39-line README-style sample)

Thirty lines changed with no edit made. Hard-wrapped paragraphs, `__bold__`,
`_italic_`, `***` and `\*` survived. Everything else normalised:

| Written | Saved |
|---|---|
| Setext heading (`===`) | `# Heading` |
| `## Heading ##` | trailing hashes stripped |
| `+` bullets, then a `*` list | `*` bullets, then `-` (alternates adjacent lists) |
| `1)` ordered | `1.` |
| `[text][ref]` + definition line | inlined `[text](url "title")`; definition deleted |
| Indented code block | fenced |
| Two trailing spaces (hard break) | `\` |
| `snake_case_word` | `snake\_case\_word` |
| `\|:-----\|------:\|` | reformatted; padding follows the widest cell |
| Tight list (`- one` / `- two`) | loose: a blank line between the items |
| CRLF line endings | LF outside code and HTML blocks, CRLF kept inside them |

The sample is committed as `editor-src/test/fixtures/roundtrip-audit.md` so the
figure is reproducible. "Thirty" is a positional count: 30 of the 39 lines from a
`split('\n')` of input and output differ at the same index. An alignment-aware
diff counts 18 lines altered and 4 inserted; either way it is most of the file.

Why it is invisible: in the formatted view, `SetCleanBaselineAsync` takes the clean
baseline from the editor's re-serialisation, not the file, so a freshly opened
document shows no asterisk. The rewrite lands on the first save after any edit. (A
document opened and saved entirely in the source view is written back as typed: the
source view holds the raw text and never passes through the serialiser.) A second
consequence:
`HandleExternalChangeAsync` compares the disk text to that normalised baseline, so a
tool that rewrites the file with identical bytes reads as an external change.

## Stages

**Decided 2026-09-10:** everything below lands on ONE line, 0.11 (unreleased
while the work is in progress), and ships together as 1.0.0-beta1, then rc1, then
1.0.0 after dogfooding — no per-stage tags. The stage numbers that follow are WORK
ORDER, kept so the issues' "Release" lines still map; they are not release numbers.
The order is the one the risk deserves, except the already-open guard, pulled to
the front because it is felt daily, stands alone, and is the first piece of the
cross-instance registry the roadmap wants anyway.

### 0.10.0 — promote the current beta

Done 2026-09-10 (v0.10.0). Same shape as the 0.9.0 promote. Nothing new.

### Stage 1 (was 0.11) — the already-open guard (#1)

Today every open — File ▸ Open, Open Recent, a drop, a double-click in Explorer —
lands in `OpenPathAsync`, and a double-click or File ▸ New is a fresh process. If the
file is already open in another window, nothing notices: two windows, two sets of
unsaved edits, each saving over the other, and the external-change watcher only
speaks up afterwards.

| AC | Test |
|---|---|
| W1. Opening a file already open in another window focuses that window instead of opening a second copy; the launching process hands over foreground | `OpenGuardTests` (pure: the per-path lock decision; the focus hand-off is dogfood-verified) |
| W2. If the other window cannot be focused (it is gone, or refuses), the file opens read-only with a message saying where it is open | `OpenGuardTests.FocusFailureOpensReadOnly` |
| W3. The guard is an exclusively opened lock file per normalised path, the backup store's proven pattern: a dead holder's lock opens, and is ignored | `OpenGuardTests.StaleLockIsIgnored` |
| W4. Paths are normalised before comparison: casing, relative segments, 8.3 short names and a trailing separator all identify the same file | `OpenGuardTests.PathsNormalise` |
| W5. A window re-opening its own file (reload, external-change Keep) is never blocked by its own lock | `OpenGuardTests.OwnLockIsNotABlock` |
| W6. The lock is released on close, Save As to a different path re-keys it, and Convert to Unencrypted / Encrypt (which change the path) re-key it | `OpenGuardTests.RekeysOnPathChange` |

Where it lives: a new `Instances/OpenGuard.cs`, called from `OpenPathAsync` before
anything is read, plus an early check in `App.OnStartup` so a double-click on an
already-open file focuses the existing window without ever flashing a new one.
The lock directory is `%LocalAppData%\MarkdownMidget\open\`, one file per path
hash, holding the owner's process id and window handle. This is the minimal form
of the roadmap's cross-instance registry: the same file, read by a Window menu
later, lists every open document.

### Stage 2 (was 0.12) — round-trip honesty (#2, #3, #4)

The core promise, made true or stated. Infrastructure first because the rest is
unpinnable without it.

| AC | Test |
|---|---|
| R1. A round-trip harness exists: markdown in, editor, markdown out, in Node with the real bundle's parser and serialiser | `editor-src/test/roundtrip.test.mjs` with a fixture corpus: `editor-src/test/fixtures/roundtrip-audit.md` (committed with this plan) plus README.md and HELP.md themselves |
| R2. The serialiser's conventions are pinned: `-` bullets, `1.` ordered, ATX headings, fenced code, `*`/`**` emphasis, the backslash hard break (decided 2026-09-10 over two trailing spaces), and tight bullet lists stay tight (today `- one` / `- two` comes back loose; ordered lists already stay tight) — and the harness fails if any drifts | `roundtrip.test.mjs: ConventionsArePinned` (each convention one case) |
| R3. Intraword underscores are not escaped (`snake_case_word` survives) | `roundtrip.test.mjs: IntrawordUnderscoreSurvives` — investigate `mdast-util-to-markdown` `unsafe` overrides; if it cannot be done safely, this AC moves to "documented limit" |
| R4. Line endings are preserved end to end: a CRLF file saves CRLF throughout, an LF file LF, a mixed file takes the majority — including inside code and HTML blocks, where the serialiser today keeps the original endings while normalising everything else | `DocumentTextTests.LineEndingsRoundTrip` (host, pure): detect on load; on save FOLD every `\r\n`, `\r` and `\n` to `\n` first, then re-apply the detected ending. A naive `Replace("\n", "\r\n")` would produce `\r\r\n` inside every code block. The corpus must include a CRLF file with a fenced block, an indented block and an HTML block |
| R5. A UTF-8 BOM is preserved when present and not added when absent | `DocumentTextTests.BomRoundTrip` |
| R6. A file rewritten on disk with identical bytes is NOT reported as an external change | `ExternalChangeTests.IdenticalBytesAreNotAChange` — keep the raw loaded text beside the normalised baseline and compare against the raw one |
| R7. HELP documents the conventions and states plainly that a document which passes through the formatted view is saved in them, listing what is converted (reference links inlined, setext to ATX, tight bullet lists loosened unless R2 fixes it, …) | reviewed against R2's list; `EmbeddedReaderDocsTests` already pins HELP loads |

Mutations to kill: R2 — flip one serialiser option and the pin must go red;
R4 — write with `\n` unconditionally and the CRLF case must go red, and skip the
fold step and the CRLF-with-code-block case must go red on `\r\r\n`; R6 — compare
against the normalised baseline and the identical-bytes case must go red.

Decided against for 1.0: preserving reference-style links (the parser consumes the
definition; keeping it needs a schema change) and preserving the user's *existing*
bullet/heading style per document (a per-document style sniff is possible but it is
a second serialiser configuration to test). Both go in the documented list.

### Stage 3 (was 0.13) — Find & Replace (#5)

| AC | Test |
|---|---|
| F1. Replace and Replace All in both views — a Replace field and Replace / Replace All buttons added to the existing Find dialog (decided 2026-09-10) — honouring the current search mode (normal, extended, wildcard, regex with groups) | `FindEngineTests.Replace*` (pure, per mode) |
| F2. Replace All scoped to the selection when there is one | `FindEngineTests.ReplaceAllWithinSelection` |
| F3. Replace in the formatted view replaces exactly the found range and nothing else, including across inline marks | `find.test.mjs` (jsdom, the editor's find ranges) |
| F4. Replace All is one undo step in both views | source: `SourceEditorTests.ReplaceAllIsOneUndoUnit`; formatted: `find.test.mjs` |
| F5. A regex replacement with `$1` groups substitutes correctly and a malformed pattern is refused with the existing message, never applied | `FindEngineTests.ReplaceGroups`, `MalformedPatternIsRefused` |

### Stage 4 (was 0.14) — images (#6, #7)

| AC | Test |
|---|---|
| I1. Dropping an image file on either view inserts it as a picture (data URI, like Insert ▸ Picture); dropping any other non-markdown file is refused with a message and does NOT replace the document | `DropRoutingTests` (pure routing on name + sniffed content) |
| I2. Pasting a clipboard image into the source view inserts the same markdown the formatted view would (`![](data:image/png;base64,…)`) at the caret | `SourceEditorTests.ImagePasteInsertsDataUri` (STA, clipboard stubbed at the seam) |

On I2, Markdown Monster's cue: it *does* accept pasted images in its text editor,
but saves them as files beside the document and links them. Our model embeds as a
data URI in both views already, so the consistent behaviour is the data URI, not a
file. It is small (clipboard image to PNG to base64 to insert at caret). If it turns
out to fight AvalonEdit's paste pipeline, the fallback is a status message pointing
at the formatted view, which is still better than today's silent nothing.

### Stage 5 — the limits, written down; no new features (#9)

| AC | Test |
|---|---|
| L1. ROADMAP's "Won't unless asked" and a new HELP "Known limits" section list every deliberate limit from the audit (below) | reviewed; `EmbeddedReaderDocsTests` pins HELP loads; `DocAnchorLinksTests` pins that the section's own cross-references resolve |
| L2. README's promise paragraph is true as written after 0.12 (either "not lossy" holds, or it says "normalises to these conventions") | reviewed; `DocAnchorLinksTests` pins that its link into HELP's conventions list lands on that heading |
| L3. README Status, Recent changes and the badge reflect 1.0; CHANGELOG has the promotion section | same shape as the 0.9.0 promote |

## The installer decision

The roadmap calls the Add/Remove Programs entry a hard requirement from user
feedback, and it is the largest, least-certain piece of work on the list (MSI vs
MSIX spike, WebView2 packaging, updater hand-off). Two honest options:

- **A. Ship 1.0 portable-only**, with a README/HELP line that says so and why, and
  the installer as the 1.1 headline. Recommended: it keeps 1.0 to work whose shape
  is known.
- **B. Add a minimal per-user MSI to 1.0**: ARP entry, shortcut, `.md` association,
  uninstall; the in-app updater no-ops when it detects the MSI install. A spike
  first; if the spike is clean, it can ride 1.0 without moving the other stages.

**Decided 2026-09-10: A.** Recorded on #8 (closed). Stage 5 adds the README/HELP
line; the installer is the 1.1 headline.

## Stated limits (what "Won't unless asked" should say)

- Spell check is en-US only; the UI is English only.
- Task-list checkboxes render (the GFM preset supports them) but there is no menu
  or toolbar item to insert one. Cheap to add; not required.
- The source view is Consolas 14 with no font, size or zoom control.
- PDF is the only export.
- One document per window, no tabs, no Window menu (a deliberate SDI choice).
- Reference-style links are converted to inline links on save; setext headings to
  ATX; indented code to fenced (all from 0.12's conventions list).
- Mermaid ships in the bundle whether or not a document uses it.

## Tracking

Filed 2026-09-10 as GitHub issues under the
[v1.0 milestone](https://github.com/FuncularLabs/MarkdownMidget/milestone/1), one per
gap, so the discovery and the fix are both public. Commits and CHANGELOG entries
reference them.

Status is the state on `master`, where every stage below lands unreleased: the
0.11 line ships as 1.0.0-beta1.

| Stage | Issues | Status |
|---|---|---|
| Stage 1 already-open guard | #1 (enhancement — single-instance-per-file was never promised) | Done 2026-09-11 (unreleased) |
| Stage 2 round-trip honesty | #2 conventions rewritten, #3 line endings and BOM, #4 identical-bytes external change | Done 2026-09-11 (unreleased) |
| Stage 3 Find & Replace | #5 | Open |
| Stage 4 images | #6 dropped image file opens as text, #7 source-view image paste | #7 done 2026-09-11 (unreleased); #6 open |
| installer decision | #8 — decided A, closed | Decided 2026-09-10 |
| Stage 5 limits written down | #9 | In progress |
