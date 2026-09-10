# Source view on AvalonEdit — verification and migration scope

Status: **scoped, not started.** Written 2026-09-09 against master at `48d117a` (v0.9.0).

Prerequisite for themed/syntax-coloured code view. See the theming design question that
prompted it: a WPF `TextBox` cannot colour text, so every code-view theming option is
gated on replacing the control.

---

## 1. Verification: does AvalonEdit run on .NET 10?

**Yes.** Measured, not assumed — a throwaway `net10.0-windows` WPF project was built and
run against the package.

| | |
|---|---|
| Package | `AvalonEdit` 6.3.1.120 |
| Licence | MIT (`licenses.nuget.org/MIT`), "AvalonEdit Contributors" |
| Ships assets for | `net462`, `net6.0-windows7.0`, `net8.0-windows7.0` |
| **A `net10.0` asset** | **No** — `net8.0-windows7.0` is what a .NET 10 project resolves |
| Built and run on | SDK 10.0.301, runtime 10.0.9 |
| Assembly size | 608 KB, about 9% on top of the 6.9 MB release exe |

Everything below was exercised in the probe and behaved:

- The XAML namespace `http://icsharpcode.net/sharpdevelop/avalonedit` resolves, and
  `<ae:TextEditor>` instantiates from XAML with `InitializeComponent`.
- **Single-file publish works.** Published with `PublishSingleFile=true`,
  `SelfContained=false`, matching `win-x64-fxdependent.pubxml`, and the embedded
  highlighting resources still load from inside the bundle. The publish profile sets no
  trimming, so AvalonEdit's reflection-based `.xshd` loading is not at risk.
- 21 highlighting definitions ship in the box, including `MarkDown` — see §4, they are
  not usable as-is.
- A definition's named colours are **mutable at runtime**: swapping C#'s `Comment`
  foreground from `#FF008000` to `#FFFF00FF` took effect. That is the theming hook.

**`HighlightingManager.Instance` is a process-wide singleton, and that is safe here.**
File ▸ New spawns a separate process (`MainWindow.xaml.cs:974`), so one window is one
process. Mutating the shared definition to apply a theme cannot leak into a window that
chose a different one. If windows are ever merged into one process, this becomes a real
bug and the definition must be cloned per window.

---

## 2. What has to move

69 references to `SourceBox`, across 27 distinct members.

| File | Sites | Nature |
|---|---|---|
| `MainWindow.xaml.cs` | 42 | text, focus, visibility, clipboard, undo, find, scroll anchor, word wrap, read-only |
| `MainWindow.Spell.cs` | 19 | squiggle wiring, context menu hit-test, replacement |
| `MainWindow.Theme.cs` | 7 | background, foreground, caret brush |
| `MainWindow.xaml` | 1 | the control itself |
| `SourceFormat.cs` | whole file, 88 lines | 6 methods typed to `TextBox` |
| `Spelling/SquiggleAdorner.cs` | whole file, 173 lines | built entirely on `TextBox` geometry |

**No test depends on the control.** `FindEngineTests`, `ScrollAnchorTests` and
`SpellHitTestTests` cover the logic with no reference to `TextBox`, and the only two
files that name it do so incidentally: `ContextMenuFocusTests` uses a bare `TextBox` as a
menu host unrelated to the source view, and `ThemeReadBackTests` names it in comments.
The migration therefore cannot break an existing test by construction, which also means
the existing suite will not catch a regression in this work. New tests are required, not
optional — see §6.

---

## 3. Member-by-member mapping

Every one of these compiled and ran in the probe.

| Today (`TextBox`) | AvalonEdit |
|---|---|
| `Text`, `Focus()`, `Visibility`, `IsReadOnly` | same |
| `Background`, `Foreground` | same |
| `CaretBrush` | `TextArea.Caret.CaretBrush` |
| `TextWrapping` | `WordWrap` (bool) |
| `CaretIndex` | `CaretOffset` |
| `SelectionStart`, `SelectionLength`, `SelectedText` | same, all get and set |
| `Select`, `SelectAll`, `Cut`, `Copy`, `Paste`, `Undo`, `Redo` | same |
| `LineCount` | `Document.LineCount` |
| `GetLineIndexFromCharacterIndex(i)` | `Document.GetLineByOffset(i).LineNumber - 1` |
| `GetCharacterIndexFromLineIndex(n)` | `Document.GetLineByNumber(n + 1).Offset` |
| `GetLineText(n)` | `Document.GetText(Document.GetLineByNumber(n + 1))` |
| `ScrollToLine(n)` | `ScrollToLine(n + 1)` |
| `GetFirstVisibleLineIndex()` | `TextArea.TextView.VisualLines[0].FirstDocumentLine.LineNumber - 1` |
| `GetRectFromCharacterIndex` | `BackgroundGeometryBuilder.GetRectsForSegment` |
| `GetCharacterIndexFromPoint(p, snapToText: true)` | `GetPositionFromPoint(p)` — **differs, see §5** |
| `SpellCheck.IsEnabled = false` | delete; AvalonEdit has no built-in spell check |
| `AdornerLayer` squiggles | `TextArea.TextView.BackgroundRenderers` |

Note the **one-based line numbering**. AvalonEdit counts document lines from 1; the
`TextBox` API counts display lines from 0. Every conversion above carries the offset, and
a missed one is an off-by-one that will look like a working feature until a squiggle
lands on the wrong line.

Also note **display line versus document line**. `GetFirstVisibleLineIndex` returns a
*display* line, which differs from the document line when word wrap is on. The scroll
anchor at `MainWindow.xaml.cs:2449` relies on that. `VisualLine.FirstDocumentLine` gives
the document line, so with wrap enabled the anchor must go through
`VisualLine`/`VisualLinesValid` rather than assume the two agree.

---

## 4. The built-in markdown definition is not shippable

It exists, and it is worse than nothing for an app whose subject is markdown. Run
against real constructs:

```
**bold** plain **bold again**    -> StrongEmphasis over the ENTIRE line
a `code` b `more code` c         -> Code over '`code` b `more code`'
- list item                      -> no highlighting
1. numbered item                 -> no highlighting
| col | col |                    -> no highlighting
---                              -> no highlighting
> quoted line                    -> BlockQuote (correct)
```mermaid                       -> Code over the backticks only, language ignored
- [ ] task                       -> no highlighting
```

Two separate faults. The emphasis and code rules use greedy `.*` between delimiters, so
two spans on one line merge into one and swallow the text between them. And lists,
ordered lists, tables, rules, task lists and front matter have no rules at all.

It also offers only 8 named colours, one of which (`Code`) has no foreground.

**Conclusion: adopt the control, author our own `Markdown.xshd`.** Budget for it as a
first-class work item, not a configuration step. The named-colour mechanism is the part
worth keeping — a custom definition declares its own colour names and the host swaps
their brushes per theme.

Colour roles the definition should declare, chosen to match what a theme can supply:
heading marker, heading text, emphasis, strong, inline code, fence delimiter, fence
language, link text, link target, image, list marker, block quote, rule, table
delimiter, HTML, front matter.

---

## 5. Behavioural differences and hazards

**Hit-testing past the end of text returns null.** Measured: a point in the empty area
below the last line gives `GetPositionFromPoint` → `null`, where
`GetCharacterIndexFromPoint(p, snapToText: true)` always returns an index. The spell
context menu at `MainWindow.Spell.cs:221` depends on getting one. Right-clicking below
the text currently snaps to the nearest word and offers its suggestions; without a
fallback it would silently offer the plain menu instead. This is the single behavioural
regression in the whole migration and it needs an explicit nearest-offset fallback.

**Read-only does not block programmatic edits.** `IsReadOnly = true` still permits
`Document.Insert`, same as `TextBox`. Confirmed, so the read-only paths are unaffected.

**`SelectedText` assignment is one undo unit.** Confirmed by round-trip: a single `Undo`
reverted the whole assignment. `SourceFormat` and the spell replacement both depend on
this and both keep working.

**The `ScrollChanged` routed event still bubbles** from the templated `ScrollViewer`, so
the squiggle repaint hook at `MainWindow.Spell.cs:51` survives. `TextView.ScrollOffsetChanged`
is the cleaner alternative if the routed event proves flaky under wrap.

---

## 6. What gets deleted

The migration is a net simplification in two places, which is worth weighing against the
608 KB and the churn.

- **`SquiggleAdorner.DrawRange`**, roughly 90 lines of character-index arithmetic, is
  replaced by `BackgroundGeometryBuilder.GetRectsForSegment`. Verified: a 9-character
  word yields one rectangle directly. The hand-written correction for "a segment ending
  in a newline reports the next line's leading edge" disappears with it.
- **`SquiggleAdorner.ShiftForEdit`** disappears entirely. `Document.CreateAnchor` tracks
  an offset through edits automatically — verified, offset 20 became 30 after a 10-char
  insert at 0. Today's version drops any range the edit touched; anchors survive it.

---

## 7. Test plan (house rule: tests designed before implementation)

The existing suite cannot catch a regression here, so these are the acceptance criteria.
STA-thread WPF tests are established practice in this repo, see `ContextMenuFocusTests`.

| Test | Proves |
|---|---|
| `SourceEditor_LineMapping_IsOneBasedCorrectly` | document line ↔ offset round-trips at first line, last line, and empty document |
| `SourceEditor_FirstVisibleLine_TracksWrap` | display versus document line divergence with `WordWrap` on |
| `Squiggles_RectsMatchWordBounds` | one rectangle per word, correct span, nothing drawn off-viewport |
| `Squiggles_AnchorsSurviveEdit` | a range stays glued to its word after an insert before, inside and after it |
| `ContextMenu_HitTestBelowText_FallsBackToNearestOffset` | the §5 regression, explicitly |
| `SourceFormat_Wrap_LeavesCaretBetweenMarkers` | empty-selection bold puts the caret inside |
| `SourceFormat_Prefix_ReplacesExistingBlockMarker` | heading swap on a line that already has one |
| `SourceFormat_AllOperations_AreSingleUndoUnits` | one `Undo` reverts one command, for each of the 6 methods |
| `Highlighting_TwoBoldSpansOnOneLine_DoNotMerge` | the greedy bug the built-in definition has |
| `Highlighting_CoversListsTablesRulesAndFences` | the constructs the built-in definition ignores |
| `Palette_AppliesToNamedColors` | swapping a theme changes the rendered brushes |
| `ReadOnly_BlocksTypingButNotProgrammaticEdits` | parity with today |

Coverage target per the house rule: 85% line coverage on every touched file.

---

## 8. Not in scope

- **Font and size control, and zoom.** The pane is fixed at Consolas 14 and has no zoom;
  `Web.ZoomFactor` is WebView-only. AvalonEdit brings line numbers and folding for free,
  and users will ask for font control the same day they get colour. Deliberately a
  separate item so this one stays a like-for-like control swap.
- **The theming design itself** — which palette is linked to the document theme, and how
  independent code-view themes are authored and resolved. That decision is upstream of
  this work and is being decided separately.
- **Moving the source view into the WebView2** as a CodeMirror surface. The competing
  option, discarded for now: it would make theming pure CSS and reuse the existing
  read-back, at the cost of reimplementing the native spell and find paths that already
  work. Revisit only if the source view needs to stop being a native control for some
  other reason.
