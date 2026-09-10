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
| 2 | CRLF becomes LF; a UTF-8 BOM is dropped | Confirmed by reading the load/save path |
| 3 | Find has no Replace | Confirmed |
| 4a | Pasting an image into the formatted view does nothing | **Wrong.** It works, via the browser's own paste; there was no handler to grep for |
| 4b | Dropping an image *file* on the editor opens it as text | Confirmed: `HandleDroppedContent` opens any dropped file as a document, after the discard prompt |
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
| `\|:-----\|------:\|` | `\| :--- \| ----: \|` |

Why it is invisible: `SetCleanBaselineAsync` takes the clean baseline from the
editor's re-serialisation, not the file, so a freshly opened document shows no
asterisk. The rewrite lands on the first save after any edit. A second consequence:
`HandleExternalChangeAsync` compares the disk text to that normalised baseline, so a
tool that rewrites the file with identical bytes reads as an external change.

## Stages

One user-visible change per release, in the order the risk deserves. Versions are
the house cadence; the numbers are suggestions.

### 0.10.0 — promote the current beta

After dogfooding. Same shape as the 0.9.0 promote. Nothing new.

### 0.11.0 — round-trip honesty

The core promise, made true or stated. Infrastructure first because the rest is
unpinnable without it.

| AC | Test |
|---|---|
| R1. A round-trip harness exists: markdown in, editor, markdown out, in Node with the real bundle's parser and serialiser | `editor-src/test/roundtrip.test.mjs` with a fixture corpus (the audit sample plus README.md and HELP.md themselves) |
| R2. The serialiser's conventions are pinned: `-` bullets, `1.` ordered, ATX headings, fenced code, `*`/`**` emphasis, and a chosen hard-break form — and the harness fails if any drifts | `roundtrip.test.mjs: ConventionsArePinned` (each convention one case) |
| R3. Intraword underscores are not escaped (`snake_case_word` survives) | `roundtrip.test.mjs: IntrawordUnderscoreSurvives` — investigate `mdast-util-to-markdown` `unsafe` overrides; if it cannot be done safely, this AC moves to "documented limit" |
| R4. Line endings are preserved: a CRLF file saves CRLF, an LF file LF, a mixed file takes the majority | `DocumentTextTests.LineEndingsRoundTrip` (host, pure: detect on load, re-apply on save) |
| R5. A UTF-8 BOM is preserved when present and not added when absent | `DocumentTextTests.BomRoundTrip` |
| R6. A file rewritten on disk with identical bytes is NOT reported as an external change | `ExternalChangeTests.IdenticalBytesAreNotAChange` — keep the raw loaded text beside the normalised baseline and compare against the raw one |
| R7. HELP documents the conventions and states plainly that saving normalises to them, listing what is converted (reference links inlined, setext to ATX, …) | reviewed against R2's list; `EmbeddedReaderDocsTests` already pins HELP loads |

Mutations to kill: R2 — flip one serialiser option and the pin must go red;
R4 — write with `\n` unconditionally and the CRLF case must go red; R6 — compare
against the normalised baseline and the identical-bytes case must go red.

Decided against for 1.0: preserving reference-style links (the parser consumes the
definition; keeping it needs a schema change) and preserving the user's *existing*
bullet/heading style per document (a per-document style sniff is possible but it is
a second serialiser configuration to test). Both go in the documented list.

### 0.12.0 — Find & Replace

| AC | Test |
|---|---|
| F1. Replace and Replace All in both views, honouring the current search mode (normal, extended, wildcard, regex with groups) | `FindEngineTests.Replace*` (pure, per mode) |
| F2. Replace All scoped to the selection when there is one | `FindEngineTests.ReplaceAllWithinSelection` |
| F3. Replace in the formatted view replaces exactly the found range and nothing else, including across inline marks | `find.test.mjs` (jsdom, the editor's find ranges) |
| F4. Replace All is one undo step in both views | source: `SourceEditorTests.ReplaceAllIsOneUndoUnit`; formatted: `find.test.mjs` |
| F5. A regex replacement with `$1` groups substitutes correctly and a malformed pattern is refused with the existing message, never applied | `FindEngineTests.ReplaceGroups`, `MalformedPatternIsRefused` |

### 0.13.0 — images and the second-window guard

| AC | Test |
|---|---|
| I1. Dropping an image file on either view inserts it as a picture (data URI, like Insert ▸ Picture); dropping any other non-markdown file is refused with a message and does NOT replace the document | `DropRoutingTests` (pure routing on name + sniffed content) |
| I2. Pasting a clipboard image into the source view inserts the same markdown the formatted view would (`![](data:image/png;base64,…)`) at the caret | `SourceEditorTests.ImagePasteInsertsDataUri` (STA, clipboard stubbed at the seam) |
| W1. Opening a file already open in another window focuses that window instead of opening a second copy; if focusing fails, the second window opens read-only with a message | `OpenGuardTests` (pure: per-path named-mutex/lock decision; the focus call is dogfood-verified) |
| W2. The guard releases on close and on crash (a stale lock from a dead process is not honoured) | `OpenGuardTests.StaleLockIsIgnored` |

On I2, Markdown Monster's cue: it *does* accept pasted images in its text editor,
but saves them as files beside the document and links them. Our model embeds as a
data URI in both views already, so the consistent behaviour is the data URI, not a
file. It is small (clipboard image to PNG to base64 to insert at caret). If it turns
out to fight AvalonEdit's paste pipeline, the fallback is a status message pointing
at the formatted view, which is still better than today's silent nothing.

W1 is the minimal form of the roadmap's cross-instance registry: a per-path lock is
enough for "already open", and the Window menu can be built on the same piece later.

### 1.0.0 — the limits, written down; no new features

| AC | Test |
|---|---|
| L1. ROADMAP's "Won't unless asked" and HELP's limits section list every deliberate limit from the audit (below) | reviewed; `EmbeddedReaderDocsTests` pins HELP loads |
| L2. README's promise paragraph is true as written after 0.11 (either "not lossy" holds, or it says "normalises to these conventions") | reviewed |
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

Pick before 0.13 starts.

## Stated limits (what "Won't unless asked" should say)

- Spell check is en-US only; the UI is English only.
- Task-list checkboxes render (the GFM preset supports them) but there is no menu
  or toolbar item to insert one. Cheap to add; not required.
- The source view is Consolas 14 with no font, size or zoom control.
- PDF is the only export.
- One document per window, no tabs, no Window menu (a deliberate SDI choice).
- Reference-style links are converted to inline links on save; setext headings to
  ATX; indented code to fenced (all from 0.11's conventions list).
- Mermaid ships in the bundle whether or not a document uses it.

## Tracking

Each stage's ACs become GitHub issues under a `v1.0` milestone (decision pending —
see the audit conversation), referenced from commits and the CHANGELOG, so the
discovery and the fix are both public.
