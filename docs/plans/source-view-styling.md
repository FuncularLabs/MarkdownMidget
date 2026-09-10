# Source-view styling — test-first implementation plan

Companion to `source-view-editor.md` (the verification + migration scope). That doc
proved AvalonEdit runs on .NET 10 and mapped every `TextBox` member. This one is the
actionable plan: acceptance criteria, the AC→test matrix, and the staged tasks.

Written 2026-09-09 against master at `7de499a`.

## The feature, in one line

The raw-markdown source view (Ctrl+E) gains **syntax highlighting that follows the
document theme** — headings, links, emphasis, code, quotes and list markers coloured
with the same palette the WYSIWYG view uses, on the same page background. Today the
pane is a plain `TextBox`: it already tracks the theme's background/foreground/caret,
but it cannot colour a run of text, so nothing else is possible until the control can.

The user's steer, verbatim: capture the *general vibe of the corresponding WYSIWYG
themes, not the code-block theme, since e.g. we can have dark code blocks in light
themes*. So source tokens map to the theme's **page-level** semantic variables
(`--mdm-heading`, `--mdm-link`, `--mdm-quote-*`, `--mdm-text`), which are tuned for
the page and measured legible on it — never to `--mdm-token-*`, which are tuned for
the dark code panel and fail on a light page (measured: 8–9 of 9 tokens under 4.5:1).

## Stages

- **Stage 1 — control swap (like-for-like).** Replace the `TextBox` with an
  AvalonEdit-backed `SourceEditor` adapter that exposes the exact TextBox-shaped API
  the app already calls, so the 69 call sites barely move and every AvalonEdit-ism
  lives in one tested file. No colouring yet; the pane looks and behaves as today.
- **Stage 2 — themed markdown highlighting.** A `Markdown.xshd` definition whose
  named colours are set at runtime from the theme's page-level palette, read back the
  same way the source background already is. This is the visible feature.
- **Stage 3 — the source view's own theme.** Shipped in a simpler shape than first
  scoped: no second palette format, no second list. One list of themes; **View ▸
  Theme ▸ Same Theme for Both Views** (on by default) and, when off, View ▸ Theme
  changes only the view the user is in, with the menu ticking that view's theme.
  The source theme is resolved in a hidden same-origin iframe carrying the bundle's
  layers but not the page's theme element (`MDM.resolveTheme`), so the document is
  never repainted — verified on the shipped bundle under the real CSP. The rule
  (which views a selection targets; which key the menu ticks) is pure and tested in
  `ThemeLinking` / `ThemeLinkingTests`.

## Design decisions

1. **Adapter, not a scattered rewrite.** `SourceEditor : ICSharpCode.AvalonEdit.TextEditor`
   re-exposes `CaretIndex`, `TextWrapping`, `CaretBrush`, `LineCount`, `GetLineText`,
   `GetLineIndexFromCharacterIndex`, `GetCharacterIndexFromLineIndex`,
   `GetFirstVisibleLineIndex`, `GetLastVisibleLineIndex`, `GetCharacterIndexFromPoint`
   and `GetRectFromCharacterIndex` with **TextBox semantics** (0-based display lines,
   control-relative rects). The seam is one file with its own tests; the call sites in
   `MainWindow.*.cs` and `SourceFormat.cs` change by type name and little else.
2. **Squiggle adorner is kept, not rewritten.** It already draws against
   `GetRectFromCharacterIndex` + the visible-line methods; the adapter provides those,
   so the highest-risk file (spell squiggles) changes only its field type. The
   `ShiftForEdit` offset-tracking is preserved but now fed by `Document.Changed`
   (real offsets) instead of WPF's `TextChangedEventArgs.Changes`.
3. **`GetCharacterIndexFromPoint` gets an explicit nearest-offset fallback.** The one
   measured behavioural gap: AvalonEdit returns null for a point below the text where
   `TextBox` snapped to the last character. The spell context menu depends on an index,
   so the adapter falls back to the end offset when the hit is past the document.
4. **Highlighting is host-driven colour, not a themed .xshd file.** The `.xshd` names
   its colours (`Heading`, `Emphasis`, `Strong`, `InlineCode`, `Link`, `BlockQuote`,
   `ListMarker`, `Rule`, `FenceMarker`, `FenceLang`); the host sets each named colour's
   brush from the theme read-back after every theme change. One resolution engine (the
   browser) stays the source of truth for colour, matching the existing model.
5. **Print is untouched.** Printing already ignores the theme and renders via the
   WYSIWYG surface; the source pane is a screen-only control.

## Acceptance criteria → test matrix

Tests live in `tests/MarkdownMidget.Tests` unless noted. WPF surface tests use the
established STA-thread harness (see `ContextMenuFocusTests`). `coverlet.collector` is
added to the test project (Rule 2); touched files target ≥85% line coverage.

### Stage 1 — control swap

| # | Acceptance criterion | Test | Project |
|---|---|---|---|
| 1.1 | Line↔offset mapping is 0-based and round-trips at first line, last line, empty doc; out-of-range → -1 | `SourceEditorTests.LineAndOffsetRoundTrip`, `OutOfRangeMappingReturnsMinusOne` | Tests |
| 1.2 | Visible-line indices are DOCUMENT lines, diverging from display rows under wrap | `SourceEditorTests.VisibleLineIndicesAreDocumentLines`, `WrapMakesOneLongLineSpanTheViewport` | Tests |
| 1.3 | `GetCharacterIndexFromPoint` past the end of text returns the end offset with snap, -1 without | `SourceEditorTests.HitTestBelowTextFallsBackToEnd`, `HitTestOverTextReturnsAnOffsetInThatLine` | Tests |
| 1.4 | `CaretIndex`/`TextWrapping`/`CaretBrush` behave as the TextBox members they replace; `GetLineText`; `TextEdited` offsets | `SourceEditorTests.CaretIndexClampsAndRoundTrips`, `TextWrappingMapsToWordWrap`, `CaretBrushRoundTrips`, `GetLineTextReturnsTheLineWithoutTerminator`, `TextEditedReportsOffsetInsertionAndRemoval` | Tests |
| 1.5 | Each `SourceFormat` op lands the caret where the old code did and is a single undo unit | `SourceFormatTests.*` | Tests |
| 1.6 | Squiggle geometry: one underline per visible word-run, nothing off-screen, stale ranges clamped | `SquiggleRendererTests.*` | Tests |
| 1.7 | `ShiftForEdit` keeps ranges glued through inserts/deletes before/inside/after/at-boundaries | `SquiggleRangesTests.*` | Tests |
| 1.8 | Mutation guard (killed): the greedy built-in definition merges two bolds; ours keeps them apart | `MarkdownHighlightingTests.TwoStrongSpansOnOneLineDoNotMerge` | Tests |

(`GetRectFromCharacterIndex` from an earlier draft was dropped: the squiggle renderer
draws via AvalonEdit's `GetRectsForSegment`, so a control-relative rect shim had no
production caller.)

### Stage 2 — themed highlighting

| # | Acceptance criterion | Test | Project |
|---|---|---|---|
| 2.1 | The `.xshd` colours two bold spans on one line separately (no greedy merge) | `MarkdownHighlightingTests.TwoStrongSpansDoNotMerge` | Tests |
| 2.2 | Headings, emphasis, strong, inline code, links, quotes, list markers, rules, fences each get their named colour | `MarkdownHighlightingTests.EachConstructGetsItsColour` | Tests |
| 2.3 | The theme read-back parses the page-level syntax palette; a missing/mis-shaped field yields no palette, not a half one | `SourcePaletteTests.*` (mirrors `ThemeReadBackTests`) | Tests |
| 2.4 | Applying a palette sets every named colour's brush, floored to legible | `SourcePaletteContrastTests.EveryNamedColourAppliedToTheDefinitionIsLegible` | Tests |
| 2.5 | Every applied colour is ≥4.5:1 on the page OR the body-text colour, all 6 built-ins | `SourcePaletteContrastTests.*` | Tests |
| 2.6 | The carrier-property read-back resolves oklch()/color-mix() to rgb in Chromium | manual browser probe (recorded below) | — |

Mutations killed (Rule 1): 2.1 — the greedy AvalonEdit built-in definition merges two
bolds; ours keeps them separate (`TwoStrongSpansOnOneLineDoNotMerge`). 2.4/2.5 — a role
applied without the legibility floor fails `EveryNamedColourAppliedToTheDefinitionIsLegible`.

Two findings during implementation, recorded so the next reader does not relearn them:

- **The .xshd namespace is `syntaxdefinition/2008`, not `highlighting/2008`.** With the
  wrong one the loader silently parses zero rules (no error) and Spans throw "Token is
  not valid". Both symptoms vanish with the right namespace.
- **The read-back must flatten, not read.** In Chromium, `getComputedStyle` returns an
  `outline-color` set to `oklch(...)` as the literal `oklch(...)` string, and a
  `color-mix()` as `color(srgb …)` — neither is rgb. The canvas-flatten step resolves
  all of them to 8-bit RGB (verified in the Browser pane, same engine as WebView2), which
  is why the carrier-property trick works and why SourcePalette receives clean bytes.

Fenced code uses a multiline `<Span>` (working once the namespace was fixed), colouring
the whole block including its body.

## Interface coverage (Stage 1 adapter — Rule 2)

Every public member of `SourceEditor` is called by a test: the shim properties (1.5),
the line/offset/visible/rect/hit-test methods (1.1–1.4, 1.7), and the `TextEdited`
event (1.8). `GetLineText` is covered through `SourceFormatTests` (1.6).

## Out of scope (stated, per house rule)

- Font/size control and zoom in the source pane (arrives with the control but is a
  separate feature; the pane stays Consolas 14 for now).
- Line numbers and folding (AvalonEdit offers them; not enabled here).
- A separate palette format or a second theme list for the source view — retired;
  Stage 3 reuses the one list.
- Any change to WYSIWYG code-block colouring (Prism/CSS, already themed).
