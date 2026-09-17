# Markdown Midget 1.0 test plan

This plan covers every user-facing change going into 1.0.0: everything under `## [Unreleased]` in `CHANGELOG.md` since `1.0.0-beta1`, a few commits since then with no CHANGELOG bullet (the view pair and the opening card), and block-level source preservation, which applies from the first build that includes it.

Results are tracked by build number in the [run log](#4-run-log). Claude runs and checks off every test marked **Automated (Claude)** and the automated part of every **Both** test. Developers then run the **Human** tests and the human part of **Both** tests.

The test documents are in [`documents/`](documents/). Each test names the ones it uses.

## 1. Setup

### 1.1 Install the build

1. Close every Markdown Midget window.
2. In PowerShell, from the repository root, copy the published build over the installed copy:

   ```powershell
   Copy-Item .\src\MarkdownMidget\bin\Release\publish\framework-dependent\MarkdownMidget.exe "$env:LOCALAPPDATA\Programs\MarkdownMidget\MarkdownMidget.exe"
   ```

3. Start `%LOCALAPPDATA%\Programs\MarkdownMidget\MarkdownMidget.exe`.
4. Open **Help ▸ About Markdown Midget** and check the version and build number (`1.0.0-…+build.N`). Write them in the run log heading for this run.

### 1.2 Get the test documents

Work on **copies**. Several tests save, and a save from the formatted view can rewrite a file.

1. Copy the fixtures to a working folder. Run this again whenever you need clean copies:

   ```powershell
   Remove-Item "$env:TEMP\mdm-test-copies" -Recurse -Force -ErrorAction SilentlyContinue
   Copy-Item .\docs\test-plan\documents "$env:TEMP\mdm-test-copies" -Recurse
   ```

2. Generate the large documents (Node 18 or later, no packages needed):

   ```powershell
   node .\docs\test-plan\documents\make-large.mjs
   ```

   This writes these files to `%TEMP%\mdm-test-docs`. Don't commit them.
   - `large-600kb.md` (about 600 KB) and `large-3mb.md` (about 3 MB): table- and list-heavy documents with long cells. Every paragraph starts and ends with the misspelling `Qwzxvy`.
   - `drop-01.md` to `drop-12.md`: small documents for drop tests.
   - `picture.png`: a picture for the drop test.
   - `front-matter-bom-crlf.md`: front matter with a UTF-8 byte-order mark and CRLF line endings, for FM-02 and SRC-08. It's generated because the repository allows no committed file to start with a byte-order mark.

3. For the zip test, right-click `drop-01.md` in File Explorer and choose **Compress to ZIP file**.

### 1.3 Timing log

To record timings, start the app from a PowerShell window with `MDM_TIMING` set:

```powershell
$env:MDM_TIMING = '1'
& "$env:LOCALAPPDATA\Programs\MarkdownMidget\MarkdownMidget.exe"
```

Timings are appended to `%TEMP%\MarkdownMidget-timing.log`. Only windows started from that PowerShell window, and the windows they open, write to it. To stop logging, close every Markdown Midget window started from that PowerShell window, and that PowerShell window.

### 1.4 Before each run

- Unless a test says otherwise, start in the formatted view with **View ▸ Line Numbers ▸ Same Setting for Both Views** on.
- Note your saved settings for **View ▸ Line Numbers ▸ Show Line Numbers** and **View ▸ Spell Check**. The BIG tests check that they don't change.
- To compare two files byte for byte, run `fc.exe /b first.md second.md`. "FC: no differences encountered" means they are identical.

### 1.5 How Claude runs the automated items

```powershell
cd editor-src; npm ci; npm test                   # every jsdom test
node --test test\line-map.test.mjs               # one jsdom test file
dotnet test tests\MarkdownMidget.Tests --filter "FullyQualifiedName~LargeDocumentTests"
```

A **fixture round-trip check** loads a fixture into the shipped editor in jsdom (`mountEditor` in `editor-src/test/jsdom-editor.mjs`). It reads the document back as markdown with no edit, or with the one edit the test names, then compares the bytes. A second pass confirms the output is stable. The check runs from a script outside the repository; it adds nothing to the product test suites.

For the WEB tests, a **link replay** takes each link's address as the editor stores it for `links.md` and runs it through the checks in `LinkOpening.TryValidate` on .NET 10.

## 2. Legend

| Execution type | Meaning |
|---|---|
| **Automated (Claude)** | Checked without the app window, by a named jsdom test in `editor-src/test`, a named `dotnet test` in `tests/MarkdownMidget.Tests`, or a fixture round-trip check. |
| **Human** | Needs the real app. The test says why: a WPF menu or dialog, drag and drop, the real clipboard or WebView2 events, how something looks, or how long it feels. |
| **Both** | An automated part, which Claude runs, and a human confirmation in the app. Mark the test PASS only when both parts pass. |

| Result | Meaning |
|---|---|
| **PASS** | Every expected result was seen. |
| **FAIL** | Something differed. Say what in the note, with the case number or line. |
| **BLOCKED** | The test couldn't run, for example because an earlier failure stopped it. Say why. |
| **N/A** | The test doesn't apply to this build, for example SRC tests before source preservation is in. |

## 3. Tests

Areas: [LIN](#lin--line-numbers-go-to-line-and-the-status-bar-10) line numbers · [VIEW](#view--view-pair-and-opening-card) view pair · [FND](#fnd--find-and-replace-shortcuts) Find and Replace · [TIP](#tip--toolbar-tooltips) toolbar tooltips · [THM](#thm--light-and-dark-mode-themes) themes · [PRN](#prn--printing-with-a-theme) printing · [MRK](#mrk--formatting-marks) formatting marks · [ANC](#anc--heading-links) heading links · [LNK](#lnk--copy-link) Copy Link · [WEB](#web--opening-web-links) web links · [MENU](#menu--submenu-arrows) submenus · [SPL](#spl--spell-check-on-large-documents) spell check · [PERF](#perf--opening-performance) performance · [SWT](#swt--switching-back-to-the-formatted-view) view switch · [FM](#fm--front-matter) front matter · [TBL](#tbl--tables) tables · [BR](#br--inline-line-breaks) line breaks · [LST](#lst--lists-11) lists · [OPN](#opn--opening-in-a-new-window) opening files · [BIG](#big--large-documents) large documents · [SRC](#src--block-level-source-preservation) source preservation · [INST](#inst--install-and-update) install and update

### LIN — Line numbers, Go to Line and the status bar (#10)

#### LIN-01 The status bar shows the line and column
- **Change:** #10, Added: "The cursor's line and column in the status bar" · **Documents:** `line-numbers.md` · **Settings:** any
1. Open the document. Click in "A paragraph after a nested list." (line 25), then in the table cell `3` (line 40).
2. Press Ctrl+E and click the same two places in the source view.
- **Expected:** The status bar shows `Ln 25, Col …` and `Ln 40, Col …` in both views. In the formatted view, Col counts text only, not markdown symbols. An emoji counts as one character.
- **Type:** Both. Automated: `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `LineColumnTests.TheColumnCountsCharactersTheWayAReaderDoes`, and `line-map.test.mjs` "the caret's line in a nested list, a quote, a table and a code block, in both numberings". Human: the real status bar.

#### LIN-02 Margin numbers in both views, per-view setting and toolbar button
- **Change:** #10, Added: "View ▸ Line Numbers … together or per view" · **Documents:** `line-numbers.md` · **Settings:** Show Line Numbers off
1. Turn on **View ▸ Line Numbers ▸ Show Line Numbers**. Press Ctrl+E, then press it again.
2. Turn off **Same Setting for Both Views**. Click the **Line numbers** toolbar button in the source view only. Press Ctrl+E.
3. Close the app, start it again and open the document.
- **Expected:** In step 1, numbers show in both views and the menu tick matches the button. In step 2, the source view has no numbers and the formatted view keeps them. In step 3, both settings are as you left them. The numbers never print.
- **Type:** Both. Automated: `line-map.test.mjs` "the margin numbers top-level blocks and list items … and nothing when off" and "the margin redraws for a setting or a numbering that changed …". Human: the WPF menu, the toolbar button and saved settings.

#### LIN-03 The formatted margin shows block ranges and gap labels
- **Change:** #10, Added: "its margin shows every line once" · **Documents:** `line-numbers.md` · **Settings:** Show Line Numbers on
1. Open the document in the formatted view and read the margin from top to bottom.
- **Expected:** Every line from 1 to the end shows exactly once, in order. Examples: the paragraph is `3–4`, the gap `5–7` is labelled, the code block is `10–14`, the empty fence is `16–17`, the table is `37–40`, and the HTML block is `42–45`. Under the rule and the table, the gap's lines lead the next block's range. The nested quote item's range stops at its own last line, and the paragraph after it has its own. No two labels overlap.
- **Type:** Both. Automated: `line-map.test.mjs` "a block shows the lines it covers as a range …", "a gap with no room for a label …", "a block after a nested list in a quote or a list item has its own range …", and "the margin shows every line of … once and in order …". Human: the layout and overlap in WebView2.

#### LIN-04 Ctrl+G reaches every line, including blank lines and fences
- **Change:** #10, Added: "Go to Line reaches every line in the formatted view too" · **Documents:** `line-numbers.md` · **Settings:** formatted view
1. Press Ctrl+G, type `6` and press Enter. Repeat for lines 10, 17, 33, 38, 44, 52 and 59.
2. After each one, look at the status bar and the title bar.
- **Expected:** The status bar reads `Ln N, Col 1` for each line you asked for. It keeps that reading until you move, type or save. The caret sits in or next to the nearest block. The title gains no `*`.
- **Type:** Both. Automated: `line-map.test.mjs` "Go to Line N reads Ln N for every line …", "a line with no place of its own reads as itself …", "Go to Line on a rule selects the rule …", and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse`. Human: the dialog and the status bar.

#### LIN-05 Numbers match the file on disk until you edit
- **Change:** #10 · **Documents:** `line-numbers.md` · **Settings:** Show Line Numbers on
1. Open the document in Notepad and in the app. Compare the lines of "Three blank lines…", the table and "Last paragraph…".
2. In the app, add a word to the last paragraph and save. Reopen the saved file in Notepad and compare again.
- **Expected:** Before the edit, the app's numbers match Notepad's. After the save, they match the saved file.
- **Type:** Both. Automated: `line-map.test.mjs` "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save, the numbers are the saved markdown's". Human: the comparison with the real file.

#### LIN-06 End-of-file tables, fences and empty fences count right
- **Change:** #10 · **Documents:** `line-numbers-eof-table.md`, `line-numbers-eof-fence.md` · **Settings:** Show Line Numbers on
1. Open each document in the formatted view and read the margin. Press Ctrl+G to the last line.
- **Expected:** The table reads `5–7`. The empty fence reads `5–6`, and the last fence reads `8–10`. Go to Line reaches line 7 and line 10.
- **Type:** Both. Automated: `line-map.test.mjs` "a table or a code fence ending a file with no final newline, and an empty code fence, count their own lines …". Human: the margin in WebView2.

### VIEW — View pair and opening card

#### VIEW-01 The formatted and source buttons on the toolbar
- **Change:** commit 75a9e15 (no CHANGELOG bullet) · **Documents:** `br-forms.md` · **Settings:** formatted view
1. Look at the two view buttons on the toolbar. Click the one that is already pressed.
2. Click the other one. Then press Ctrl+E.
- **Expected:** The button for the showing view is pressed, and clicking it does nothing. Clicking the other one switches views exactly as Ctrl+E does. The buttons and the **View ▸ Edit Markdown Source** tick always agree. The icons are sharp, not blurred.
- **Type:** Human. WPF toolbar state and icon rendering have no test.

#### VIEW-02 The opening card names each phase and suggests the source view
- **Change:** commit 40710b9 (no CHANGELOG bullet) · **Documents:** `large-600kb.md`, `br-forms.md` · **Settings:** formatted view
1. From the "No document open" screen, open `large-600kb.md`. Watch the card.
2. Open `br-forms.md` in a new window.
- **Expected:** The card shows the phases in order: "Reading file…", "Checking encoding…", "Building formatted view…", "Drawing page…". (Opening into the source view shows "Preparing document…" and "Finishing up…" in place of the last two. This test doesn't cover that.) For the large file, it adds a line suggesting the Markdown source view (Ctrl+E), but nothing switches. When the formatted view is showing, the same text appears in the status bar. The small file shows no suggestion.
- **Type:** Both. Automated: `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered_the_source_view_at_or_over_the_threshold`, `A_file_opening_into_the_source_view_is_never_offered_it`. Human: the card and the status bar.

#### VIEW-03 The view pair sits in one faint box, with its icons unchanged
- **Change:** Changed: "The formatted view and Markdown source buttons now sit in one faint box" · **Documents:** `br-forms.md` · **Settings:** formatted view
1. Compare the toolbar with the previous build's, at 100% and 150% display scaling. Hover over and click both view buttons.
2. Switch **View ▸ Theme** between a light and a dark theme, then turn on Windows high contrast.
- **Expected:** A faint rounded box surrounds the two view buttons, with a thin line between them. The icons have the same size and spacing as before, the hover and pressed highlights fill the same area, and the toolbar is at most 2 pixels taller. The box and line stay faint but visible in every colour scheme.
- **Type:** Both. Automated: `ToolBarToggleTests.AGroupedButtonKeepsTheToolbarTemplateAndSize`. Human: how it looks.

### FND — Find and Replace shortcuts

#### FND-01 Ctrl+H opens Replace in both views
- **Change:** Added: "Ctrl+H now opens Replace" · **Documents:** `br-forms.md` · **Settings:** formatted view
1. Start Markdown Midget, open the document, click in the text and press Ctrl+H. Type `form` in **Find what**, close the dialog, press Ctrl+E, click in the text and press Ctrl+H again.
2. Click back in the document, leaving the dialog open, and press Ctrl+H.
- **Expected:** The first Ctrl+H puts the cursor in the empty **Find what**. After that, each time the Find and Replace dialog opens, or comes to the front, the cursor is in **Replace with** with its text selected, because **Find what** has text. Nothing is typed into the document, and neither the browser nor the Style box (Ctrl+Shift+H) reacts.
- **Type:** Both. Automated: `FindDialogFocusTests.CtrlHFocusesReplaceWithOnlyWhenFindWhatHasTextAndReplaceCanRun`. Human: the real keypress in WebView2 and in the source view.

#### FND-02 Edit ▸ Replace… sits under Find…
- **Change:** Added: "Edit ▸ Replace… now sits under Find…" · **Documents:** `br-forms.md` · **Settings:** any
1. Press Alt+E, then L.
- **Expected:** **Replace…** is directly under **Find…**, shows `Ctrl+H` and has its L underlined. L opens the dialog as in FND-01: the cursor is in **Replace with** when **Find what** has text, and in **Find what** when it's empty. No other Edit item takes L.
- **Type:** Human. A WPF menu; `MenuAccessKeysTests` covers Alt handling, not clashes between items.

#### FND-03 Read-only, Help and no document
- **Change:** Added: "Ctrl+H now opens Replace" · **Documents:** `br-forms.md` · **Settings:** any
1. Open Help (F1) and press Ctrl+H. Close the dialog and choose **Edit ▸ Replace…**.
2. In `br-forms.md`, turn on **Edit ▸ Read Only**, press Ctrl+H, type in both boxes and press Enter.
3. Press Ctrl+W. At "No document open", press Ctrl+F, close the dialog, then press Ctrl+H, then choose **Edit ▸ Replace…**.
4. Leave the dialog open, open `br-forms.md` and turn off **Edit ▸ Read Only**. Then press Ctrl+W.
- **Expected:** In steps 1 and 2 the cursor is in **Find what**, **Replace** and **Replace All** are greyed with the tooltip "The document is read-only.", and the document doesn't change. In step 3 Ctrl+H does exactly what Ctrl+F does: the cursor is in **Find what** and both Replace buttons are greyed with the tooltip "No document is open."; Enter still presses **Find Next**, which finds nothing. In step 4 both buttons work once the document is editable, and grey again after Ctrl+W.
- **Type:** Both. Automated: `FindDialogFocusTests.CtrlHFocusesReplaceWithOnlyWhenFindWhatHasTextAndReplaceCanRun`, `FindDialogFocusTests.ReplaceIsGreyedWhenReadOnlyOrNoDocument`. Human: the dialog, the menu and the keypress.

#### FND-05 The selected text fills Find what, in both views
- **Change:** Added: "Find and Replace now start with your selected text" · **Documents:** `br-forms.md` · **Settings:** formatted view, Normal search mode
1. Select one word and press Ctrl+F. Close the dialog, select a phrase that includes bold text, and choose **Edit ▸ Replace…**. Leave the dialog open, select another word and press Ctrl+H.
2. Select from one paragraph into the next and press Ctrl+F. Then click in the text without selecting and press Ctrl+F.
3. Type `a.b+c` on a new line. Choose **Regular expression**, select `a.b+c`, press Ctrl+F, then press **Find Next**. Type `a.b.c` in **Find what**, then choose **Edit ▸ Replace…** in the main window.
4. Choose **Normal** and type `cat one` and `cat two` on two new lines. Select `cat` in the second, press Ctrl+H, type `dog` and click **Replace All**, then press Ctrl+Z. Select from `one` to `two`, over the line break, and click **Replace All** again.
5. Press Ctrl+E and repeat steps 1 to 4 in the source view.
- **Expected:** In step 1, **Find what** shows exactly the selected text, as the page shows it (no `**`), with its text selected; Ctrl+F puts the cursor in **Find what**, and Replace puts it in **Replace with**. In step 2 **Find what** doesn't change. In step 3 it shows `a\.b\+c`, and Find Next finds the line you typed; Replace… then leaves `a.b.c` as it is, because the selection is Find's own match. In step 4 the first Replace All changes both lines, not only the selected word, and the second changes only `cat two`, and the status says "in the selection". The source view behaves the same, but **Find what** shows the Markdown you selected.
- **Type:** Both. Automated: `FindEngineTests.TheSelectionFillsFindWhatOnlyWhenItIsOneLineOfAtMostTheLimit`, `FindEngineTests.TheSeededQueryFindsTheSelectedTextItselfInEveryMode`, `FindEngineTests.OnlyASelectionOverALineBreakLimitsReplaceAll`, and `find.test.mjs` "is the text the page shows over marks, links and code, and nothing for Find's own current match" and "only matches wholly inside a selection over a line break are replaced …". Human: the keypresses, the menu and both views.

### TIP — Toolbar tooltips

#### TIP-01 A greyed-out toolbar button shows a tooltip that says when it's available
- **Change:** Changed: "Toolbar buttons now explain themselves when greyed out" · **Documents:** `br-forms.md` · **Settings:** formatted view
1. Hover over every toolbar button and the Style box: in the formatted view, in the source view (Ctrl+E), with no document open (Ctrl+W), and in Help (F1).
- **Expected:** Each one shows its tooltip, greyed out or not, with its shortcut where it had one. A greyed-out one says when it's available: **Word wrap (source view only)**, "editable documents only" on Save and the formatting controls, and Undo and Redo say when there is something to undo or redo. With a standard-size pointer, the tooltips open where they did before; with an enlarged pointer, see TIP-02.
- **Type:** Human. WPF tooltips on disabled controls have no test.

#### TIP-02 Toolbar tooltips open below an enlarged mouse pointer
- **Change:** Fixed: "Toolbar tooltips now open below an enlarged mouse pointer" · **Documents:** `br-forms.md`, the Help window · **Settings:** formatted view; Windows Settings ▸ Accessibility ▸ Mouse pointer and touch ▸ Size
1. Close every Markdown Midget window. Set the pointer **Size** to 1 (standard) and open `br-forms.md`. Hover over **Bold**, the **Style** box, the formatted/source view pair, the code-block chevron, and a greyed-out button in Help (F1). On each, rest the pointer near the top edge, move it off the control, then rest it near the bottom edge. Then close Help (an open Help window is reused, not reopened).
2. Set **Size** to 3. In the `br-forms.md` window that is still open, hover over **Bold** again. Then close that window, open `br-forms.md` again (a file open in one window can't open in a second), and repeat step 1's hovering, from **Bold** through closing Help, leaving **Size** at 3.
3. Close every Markdown Midget window, set **Size** back to 1, open `br-forms.md` and hover over **Bold**.
- **Expected:** In step 1 every tooltip opens just below the pointer, exactly as in rc2. In step 2 the window that was already open still places the **Bold** tooltip as in step 1; after reopening, each tooltip opens under its control, clear of the whole pointer, wherever the pointer rests on the control. In step 3 the tooltip opens below the pointer again, as in step 1.
- **Type:** Both. Automated: `ToolbarToolTipTests.AStandardPointerKeepsWpfsPlacementUnderThePointer`, `ToolbarToolTipTests.AnEnlargedPointerMovesTheTooltipUnderTheButtonByItsWholeSize`, `ToolbarToolTipTests.AMissingInvalidOrSmallSizeKeepsWpfsPlacement`, `ToolbarToolTipTests.AReadThatThrowsKeepsWpfsPlacement`, `ToolbarToolTipTests.TheStandardValuesAreWhatToolbarControlsAndTheirTooltipsHaveUnset`. Human: where the tooltips open in the window, which needs the app.

### THM — Light and dark mode themes

#### THM-01 The document theme follows Windows light and dark mode
- **Change:** Added: "The document theme now follows Windows light and dark mode" · **Documents:** `br-forms.md`, `tables.md` · **Settings:** Windows Settings ▸ Personalization ▸ Colors ▸ Choose your mode; formatted view; settings.json backed up in step 1 and restored in step 5
1. Close every Markdown Midget window. In PowerShell, back up your settings, then set them up the way an earlier version left them, with one saved theme, Dracula:

   ```powershell
   $p = "$env:LOCALAPPDATA\MarkdownMidget\settings.json"
   if (-not (Test-Path "$p.thm-backup")) { Copy-Item $p "$p.thm-backup" }
   $s = Get-Content $p -Raw -Encoding utf8 | ConvertFrom-Json
   'ThemeLight','ThemeDark','SourceThemeLight','SourceThemeDark' | ForEach-Object { $s.PSObject.Properties.Remove($_) }
   $s.Theme = 'Dracula.css'; $s.LinkThemes = $true
   $s | ConvertTo-Json -Depth 5 | Set-Content $p -Encoding utf8
   ```

   Set Windows to **Light**, open `br-forms.md` and open View ▸ Theme. Set Windows to **Dark** and open View ▸ Theme again. Close the window, run the lines again with `''` in place of `'Dracula.css'` (the backup line keeps your first backup), and repeat.
2. In Dark, pick **Dracula**. Set Windows to Light and pick **One Light**. Set Windows to Dark, then Light.
3. Set Windows to Dark. In the `br-forms.md` window use **File ▸ Open…** to open `tables.md`, which opens in a second window. Pick **GitHub Dark Dimmed** in the `br-forms.md` window. In the `tables.md` window, turn **View ▸ Spell Check** off and on again. Set Windows to Light, then Dark, and look at the `tables.md` window. Close both windows, open `br-forms.md`, then set Windows to Light.
4. Set Windows to Dark. Turn off **View ▸ Theme ▸ Same Theme for Both Views**, press Ctrl+E and pick **Solarized Light**. Set Windows to Light, then Dark; after each switch, look at the source view, press Ctrl+E and look at the formatted view, then press Ctrl+E again. Turn **Same Theme for Both Views** back on, then press Ctrl+E.
5. Turn on a contrast theme (Settings ▸ Accessibility ▸ Contrast themes) and open View ▸ Theme. Turn the contrast theme off and open View ▸ Theme again. Close every Markdown Midget window and put your settings back. If you stop THM-01 before this step, close every window and run this line when you stop:

   ```powershell
   Move-Item "$env:LOCALAPPDATA\MarkdownMidget\settings.json.thm-backup" "$env:LOCALAPPDATA\MarkdownMidget\settings.json" -Force
   ```

- **Expected:** In step 1, with Dracula saved, light shows Midget Solarized and dark shows Dracula; with `''` (Default) saved, light shows Midget Solarized and dark shows Obsidiminutive. The greyed top line of View ▸ Theme names the current mode. Every switch recolours every open window within about a second, with no restart. In step 2 dark shows Dracula and light One Light. In step 3 the `tables.md` window shows GitHub Dark Dimmed after the switches, and after reopening dark shows GitHub Dark Dimmed and light One Light: turning spell check off and on didn't undo the pick. In step 4 the source view shows One Light in light and Solarized Light in dark, and the formatted view One Light in light and GitHub Dark Dimmed in dark; turning the setting back on gives the source view GitHub Dark Dimmed. In step 5, with the contrast theme on, the top line reads *For Windows light mode* and **One Light** is ticked (the contrast theme may override the page colours); with it off, the line reads *For Windows dark mode*, and GitHub Dark Dimmed is ticked and showing.
- **Type:** Both. Automated: `ThemeModesTests.TheSlotForTheModeWindowsIsInIsAppliedAndTicked`, `ThemeModesTests.APickWritesOnlyTheSlotForTheModeItWasMadeIn`, `ThemeModesTests.ASavedThemeMigratesToTheModeItMatches`, `ThemeModesTests.SwitchingWindowsModeAppliesTheOtherSlotAndWritesNothing`, `ThemeModesTests.AModeSwitchShowsWhatAnotherWindowPickedForThatMode`, `ThemeModesTests.AnotherSettingsSaveKeepsThemeFieldsAsTheyAreOnDisk`, `ThemeModesTests.TurningLinkingOnOrOffStartsTheSourceViewFromTheDocumentInBothModes`, `WindowsAppearanceTests.AppsUseLightThemeDecidesDarkMode`, `WindowsAppearanceTests.HighContrastCountsAsLight`, `WindowsAppearanceTests.ChangedIsRaisedOncePerFlipOfTheEffectiveMode`. Human: the Windows mode switch, the menu and the windows' colours, which need the app.

### PRN — Printing with a theme

#### PRN-01 Dark themes print dark headings and links on a white page; light themes print as before
- **Change:** Fixed: "Dark themes now print with dark headings and links" · **Documents:** `print-themes.md` · **Settings:** formatted view; the print preview's **More settings ▸ Background graphics**, unticked then ticked in each step; settings.json backed up in step 1 and restored in step 5
1. Close every Markdown Midget window. View ▸ Theme saves a theme for the Windows mode you're in, so back up your settings first. In PowerShell, back them up and create two custom themes. The script writes the files itself, because Notepad can save them as `print-dark.css.txt`:

   ```powershell
   $p = "$env:LOCALAPPDATA\MarkdownMidget\settings.json"
   if (-not (Test-Path "$p.prn-backup")) { Copy-Item $p "$p.prn-backup" }
   $c = "$env:LOCALAPPDATA\MarkdownMidget\themes\custom"
   New-Item -ItemType Directory -Force $c | Out-Null
   $pale = '--mdm-heading: #eeeeee; --mdm-h4: #eeeeee; --mdm-h5: #eeeeee; --mdm-h6: #eeeeee; --mdm-link: #ddddff;'
   [IO.File]::WriteAllText("$c\print-dark.css", ":root { --mdm-color-scheme: dark; $pale }")
   [IO.File]::WriteAllText("$c\print-light.css", ":root { --mdm-color-scheme: light; $pale }")
   ```

2. Open `print-themes.md`. Choose **View ▸ Theme ▸ Dracula** and press **Ctrl+P**. With **More settings ▸ Background graphics** unticked, look at the page, the six heading levels, the heading in the quote, the four links (paragraph, quote, table cell, table header row) and the table. Tick **Background graphics** and look again. Close the preview.
3. Repeat step 2 with **GitHub Dark Dimmed**, **Obsidiminutive** and **Print Dark**. With Obsidiminutive, also use **File ▸ Print ▸ Export to PDF…** and open the PDF.
4. Repeat step 2 with **Default**, **Solarized Light** and **Print Light**.
5. Close every Markdown Midget window, then delete the two themes and put your settings back. If you stop PRN-01 before this step, close every window and run these lines when you stop:

   ```powershell
   Remove-Item "$env:LOCALAPPDATA\MarkdownMidget\themes\custom\print-dark.css", "$env:LOCALAPPDATA\MarkdownMidget\themes\custom\print-light.css"
   Move-Item "$env:LOCALAPPDATA\MarkdownMidget\settings.json.prn-backup" "$env:LOCALAPPDATA\MarkdownMidget\settings.json" -Force
   ```

- **Expected:** In steps 2 and 3, with Background graphics unticked or ticked, the page and its margins are white, body text is black, every heading (the one in the quote too) is near-black, and the paragraph, quote and cell links are dark blue and underlined. The table's header row keeps its theme's look (Default's grey for Print Dark), and its link is in the header's text colour and readable. The PDF looks like the unticked preview. In step 4 everything prints as it did before this build: headings and links in the theme's colours, which the preview may darken where they are very light while Background graphics is unticked. Ticked, Solarized Light's page is cream and Print Light's pale headings and link are hard to read, as before. After each preview closes, the page on screen is unchanged.
- **Type:** Both. Automated: `theme-parity.test.mjs` "every built-in is sorted into dark or light by what it declares", "a dark built-in prints every heading and its links dark enough for paper, with Background graphics on or off", "paper pins a dark theme's heading and link variables, not their colours", "Default and every light built-in print headings and links in their own colours, as before", "a custom theme prints dark headings and links by declaring --mdm-color-scheme: dark, and only then"; `layers.test.mjs` "print is the earliest layer, so nothing a theme writes reaches paper". Human: jsdom can't evaluate the style query that tells print a theme is dark, nor the print dialog's Background graphics option, so only the real print preview shows WebView2 applying them.

### MRK — Formatting marks

#### MRK-01 Formatting marks show in the Markdown source view too
- **Change:** Added: "Formatting marks (¶) now show in the Markdown source view too" · **Documents:** `br-forms.md`, `front-matter-bom-crlf.md`, the Help window · **Settings:** formatted view, a light theme
1. Close every Markdown Midget window. Open `br-forms.md`, click **¶** on the toolbar and press Ctrl+E. Press Ctrl+End, then type `a`, a space, Tab, `b`, Enter and `c`.
2. Click **¶** off, then on again. Press Ctrl+E twice, to the formatted view and back.
3. Click just after the tab you typed and note Ln and Col in the status bar; click **¶** off, note them again, and click **¶** on. Select the two lines you typed, press Ctrl+C and paste into Notepad. Press Ctrl+F, choose **Extended**, find `\t` and close the dialog. Press Ctrl+G and go to the last line. Press Ctrl+P, look at the preview and cancel.
4. Switch **View ▸ Theme** to Dracula, press Ctrl+E to compare the formatted view's marks, press Ctrl+E again, then switch back to the light theme.
5. Press Ctrl+S and open the saved file in Notepad. Press Ctrl+W, then open `front-matter-bom-crlf.md` in the same window (a window with no document opens the file in place), and press Ctrl+E if it opens in the formatted view.
6. Press F1. In Help, press Ctrl+E, then click **¶**. Close Help (an open Help window is reused, not reopened).
- **Expected:** In step 1 every line but the last (`c`) ends in a faint **¶** and the tab shows **→**, so your first line reads `a →b¶`. The single space before the tab has no dot, and no `\n`, `\r` or `»` appears anywhere. In step 2 the marks go and come back with the button and stay on through both switches, and the formatted view shows its own marks. In step 3 Ln and Col are the same with **¶** on and off, Notepad gets the text without marks, Find says "Match 1 of 1", Go to Line lands on `c`, and the preview has no marks. In step 4 the source view's marks are the same colour as the formatted view's, a muted blue on Dracula, and the light theme's own mark colour again, the same in both views. In step 5 the saved file has no marks, and `front-matter-bom-crlf.md`, whose lines end in CRLF, shows one **¶** per line, with **¶** still on. In step 6 Help shows no marks until **¶** is clicked in its own window, then shows them although it's read-only.
- **Type:** Both. Automated: `SourceFormattingMarksTests.TurningMarksOnDrawsOurMarksNotAvalonEditsAndOffHidesThemAll`, `EachLineEndingGetsOnePilcrowAfterItsTextWhateverTheNewline`, `EachTabAndLineEndIsMarkedOnTheWrappedRowItIsOn`, `LinesScrolledOutOfViewAreNotMarked`, `MarksStayOnThroughANewDocumentAHiddenPaneAndReadOnly`, `TheMarkColourIsTheThemeTextFadedTowardItsPage`, `AFailedThemeReadBackGivesTheDefaultThemesMarkGrey`, `TheMarksBrushIsTheTextViewsNonPrintableCharacterBrush`, `TheThemesOwnMarkColourWinsOverTheMix`, `SourcePaletteTests.TheMarkColourIsKeptWhenThePageSendsOne`, `SourceFormattingMarksTests.MarksLeaveTheTextSavedBytesFindMatchesCopiedTextWordMovesCaretColumnAndLinesUnchanged`. Human: the toolbar button, view switches, theme changes, the clipboard, print and Help, which need the app.

#### MRK-02 Spaces that change the Markdown show a dot in the source view
- **Change:** Added: "Formatting marks (¶) now show in the Markdown source view too" · **Documents:** `space-marks.md`, `large-3mb.md` · **Settings:** formatted view, Word wrap off
1. Close every Markdown Midget window. Open `space-marks.md`, click **¶** on the toolbar and press Ctrl+E. Read each line: it says which of its spaces show a dot.
2. Click at the start of the line `- A list item, nothing dotted`, type three backticks and press Enter. Look at the lines below, then press Ctrl+Z once and look again. Press Ctrl+Z three more times, once for each backtick.
3. Click at the start of `Two spaces sit here:` and press Ctrl+Right six times, noting where the cursor stops and Col in the status bar. Press Ctrl+F, choose **Normal**, type two spaces in **Find what** and press **Find Next** until it wraps, noting the count. Close the dialog, press Ctrl+A and Ctrl+C, and paste into Notepad. Click **¶** off and repeat this step, then click **¶** on.
4. Turn on **Word wrap** and make the window narrow enough that the long lines wrap, then wide again, and turn **Word wrap** off.
5. Press Ctrl+W, then open `large-3mb.md` in the same window. Press Ctrl+E if it opens in the formatted view, press Ctrl+End and type a few words, then hold Page Up for a few seconds. Click **¶** off and do the same.
- **Expected:** In step 1 the dots are exactly where each line says: at the ends of the two lines that end in spaces, between `here:` and `between`, on the line of three spaces, on both leading spaces of the badly indented `- two spaces in` line, and on the space before the tab (which shows **→**). Nothing else has a dot: not the well-indented nested list or the quoted one, not the spaces after a list marker or a `>`, not the table's padding, and in the code block only the two spaces at the end of `padding  = 2`. In step 2 every line from the new backticks down to the old code block's end is code, so the dots on the badly indented and mixed lines go, and the two at the end of `padding  = 2` stay. The first Ctrl+Z takes back only the Enter, so the dots stay away: the line now starting with three backticks still opens a code block. After all four, the document and its dots are back as they were. In step 3 the cursor stops, Col and the Find count are the same with **¶** on and off, and Notepad gets plain spaces. In step 4 each dot stays on its own space, whichever row it lands on. In step 5 typing and scrolling keep up as well with **¶** on as with it off.
- **Type:** Both. Automated: `SpaceMarksTests.TrailingSpacesRunsOfTwoAndWhitespaceOnlyLinesAreMarked`, `InCodeBlocksOnlyTrailingSpacesAreMarked`, `SuspectIndentationMarksItsLeadingSpaces`, `WellFormedIndentationIsNotMarked`, `BlockquoteMarkersAreNeitherIndentationNorRuns`, `FencesCommentsAndFrontMatterMarkOnlyTrailingSpaces`, `EachCorpusDocumentIsDottedExactlyWhereItsFileShows`, and `SourceFormattingMarksTests.SpacesAreDottedWhereTheRulesSayAndFollowAnEditAbove`, `TypedBackticksAndEnterUndoOneKeystrokeAtATime`, `OnlyTheMarksInViewArePlacedOnAVeryLongLine`, `AWrappedPictureSizedLineWalksOnlyTheRowsDownToTheViewsBottom`, `TurningMarksOnDrawsOurMarksNotAvalonEditsAndOffHidesThemAll`, `MarksLeaveTheTextSavedBytesFindMatchesCopiedTextWordMovesCaretColumnAndLinesUnchanged`. Human: how the dots look, typing into the real editor, wrapping, the clipboard and speed on a large document.

### ANC — Heading links

#### ANC-01 Ctrl+click jumps in the editable view; a plain click only places the caret
- **Change:** Added: "A `#link` to a heading in the same document jumps there" · **Documents:** `headings-anchors.md` · **Settings:** formatted view, editable
1. Click **Far away** without Ctrl.
2. Ctrl+click **Far away**, then **Setup and install**.
- **Expected:** In step 1, only the caret moves into the link text. In step 2, the view scrolls to each heading, the caret is at its start, and the status bar line follows.
- **Type:** Both. Automated: `anchor-links.test.mjs` "HELP's Known limits link: a plain click edits, Ctrl+click jumps and scrolls #app …". Human: real WebView2 scrolling.

#### ANC-02 A plain click jumps in read-only windows and in Help
- **Change:** Added: "click it in a read-only window such as Help" · **Documents:** `headings-anchors.md`, the Help window · **Settings:** Edit ▸ Read Only on
1. Click **Far away** and **Café menu, as written**.
2. Open **Help ▸ View Help** and click a link to **Known limits**.
- **Expected:** Each link jumps to its heading.
- **Type:** Both. Automated: the same `anchor-links.test.mjs` test (its read-only half). Human: the Help window.

#### ANC-03 Repeated, encoded, missing and malformed anchors
- **Change:** Added: `#link` · **Documents:** `headings-anchors.md` · **Settings:** formatted view
1. Ctrl+click each link in the list at the top of the document, in order.
- **Expected:** Each link does what its line says. The three **Repeat** links reach the first, second and third heading (`#repeat`, `#repeat-1`, `#repeat-2`). The percent-encoded **Café** link jumps. The missing and stray-percent links do nothing: no jump, no note, no error box.
- **Type:** Both. Automated: `anchor-links.test.mjs` "a repeated heading takes its numbered anchor, and a percent-encoded fragment is decoded" and "an unmatched or malformed fragment does nothing …". Human: the real click.

#### ANC-04 Every anchor link in Help lands on a heading
- **Change:** Added: `#link` · **Documents:** `HELP.md`, `README.md` · **Settings:** none
- **Expected:** Every `#` link in the shipped documents names a heading that exists, by GitHub's rules, and the editor's slug matches.
- **Type:** Automated (Claude). `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists`, `DocAnchorLinksTests.ARepeatedHeadingGetsGitHubsNumberedAnchors`, and `anchor-links.test.mjs` "a slug is DocAnchorLinksTests' slug".

### LNK — Copy Link

#### LNK-01 Copy Link copies exactly the destination, in the editor and in Help
- **Change:** Added: "Right-click a link ▸ Copy Link" · **Documents:** `links.md` · **Settings:** formatted view
1. Right-click each link in cases 1–64 and choose **Copy Link**. Paste into Notepad and compare with the **Copy Link** column. Cover at least cases 1 (absolute), 62 (relative `.md`), 56 (`#`), 12 (www autolink), 54 (mailto with a query) and 14–16 (reference links).
2. In **Help ▸ View Help**, right-click any link and choose **Copy Link**.
- **Expected:** The pasted text is exactly the column's value. Case 12 pastes `http://www.example.com`. In Help, Copy Link is offered and copies the link's address.
- **Type:** Both. Automated: `link-at.test.mjs` "a right-click on a link reports its mark href: relative, absolute, #fragment, autolink, in a table, list or quote" and `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash`. The link replay confirmed each case's address for `links.md` on master a4577ae. Human: the real menu and clipboard.

#### LNK-02 Copy Link isn't offered off a link
- **Change:** Added: Copy Link · **Documents:** `links.md` · **Settings:** formatted view
1. Right-click plain words, then case 22 (a code span), 65 (block HTML) and 66 (inline HTML).
- **Expected:** The menu has no **Copy Link** item.
- **Type:** Both. Automated: `link-at.test.mjs` "a right-click off a link or on a raw-HTML <a> reports none …". Human: the WPF menu.

#### LNK-03 The spelling menu on a misspelled link word offers Copy Link
- **Change:** Added: Copy Link · **Documents:** `links.md`, gesture G5 · **Settings:** View ▸ Spell Check on
1. Right-click the squiggled word **Qwzxvy** in G5.
- **Expected:** The spelling menu has suggestions and **Copy Link**, which copies `https://example.com/spelling`.
- **Type:** Human. The spelling menu is built by WPF code that no test covers.

#### LNK-04 A linked picture shows Resize… and Copy Link
- **Change:** Added: Copy Link · **Documents:** `links.md`, case 28 · **Settings:** formatted view, editable
1. Right-click the blue picture in case 28.
- **Expected:** The menu shows **Resize…** and **Copy Link**. Copy Link copies `https://example.com/picture`.
- **Type:** Both. Automated: `link-at.test.mjs` "… a linked picture reports its link …". Human: the picture menu.

#### LNK-05 Copy Link's place in the table menu
- **Change:** Added: Copy Link · **Documents:** `links.md`, case 24 · **Settings:** formatted view, editable
1. Right-click the link in case 24, which is in a table.
- **Expected:** This is the table menu. **Copy Link** is its last item, after **Select**.
- **Type:** Human. The menu layout is XAML with no test.

#### LNK-06 The Menu key or Shift+F10 with the caret in a link
- **Change:** Added: Copy Link · **Documents:** `links.md`, gesture G4 · **Settings:** formatted view
1. Use the arrow keys to put the caret inside the text of case 1. Press Shift+F10, then Esc. Press the Menu key.
2. Move the caret into plain words and press Shift+F10.
- **Expected:** In step 1, each press opens one menu with **Copy Link**, which copies `https://example.com/`. In step 2, the menu has no Copy Link.
- **Type:** Both. Automated: `link-at.test.mjs` "… with no target the caret decides". Human: real keyboard events.

#### LNK-07 A locked clipboard gives a status note, not a crash
- **Change:** Added: Copy Link · **Documents:** `links.md`, case 1 · **Settings:** formatted view
1. In PowerShell, run this. It holds the clipboard open for 15 seconds:

   ```powershell
   Add-Type -Name Clip -Namespace W -MemberDefinition '[DllImport("user32.dll")] public static extern bool OpenClipboard(IntPtr h); [DllImport("user32.dll")] public static extern bool CloseClipboard();'
   [W.Clip]::OpenClipboard([IntPtr]::Zero); Start-Sleep 15; [W.Clip]::CloseClipboard()
   ```

2. Within those 15 seconds, right-click case 1 ▸ **Copy Link**.
- **Expected:** After about a second, the status bar says "The link wasn't copied: another program is using the clipboard. Try again." No error box appears, and the app keeps running.
- **Type:** Both. Automated: `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash`. Human: the real clipboard.

### WEB — Opening web links

#### WEB-01 Ctrl+click an https link shows the full address, and Cancel is the default
- **Change:** Added: "Ctrl+click a web link to open it in your browser" · **Documents:** `links.md`, cases 1, 10, gesture G3 · **Settings:** formatted view
1. Ctrl+click case 1. Press Enter. Ctrl+click it again and press Esc. Ctrl+click it again and press Alt+O.
2. Ctrl+click case 10 and scroll the address box.
3. Ctrl+click case 1 and click **Open**.
- **Expected:** The **Open Link** dialog shows `https://example.com/`. Enter and Esc both cancel, and Alt+O does nothing. For case 10 the box holds all 2,048 characters. **Open** opens the page in your default browser.
- **Type:** Both. Automated: `web-links.test.mjs` "Ctrl+click posts openLink with the mark's href …" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole`. Human: the WPF dialog and launching the browser.

#### WEB-02 A plain click does nothing in the editor, and opens the dialog in Help
- **Change:** Added: "in a read-only window such as Help, just click" · **Documents:** `links.md`, gestures G1, G2, G7; the Help window · **Settings:** formatted view
1. Click case 1 without Ctrl. Ctrl+double-click case 1.
2. Turn on **Edit ▸ Read Only** and click cases 1, 33 and 56. In **Help ▸ View Help**, click an https link.
- **Expected:** In step 1, a plain click only places the caret, and a Ctrl+double-click opens one dialog. In step 2, a plain click gives the Ctrl+click result: a dialog for case 1, the note for case 33, and a jump for case 56. In Help, the dialog opens.
- **Type:** Both. Automated: `web-links.test.mjs` "Ctrl+click posts openLink … a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link". Human: real clicks and the Help window.

#### WEB-03 Mail, file and relative links are refused with the note
- **Change:** Added: "Email links don't open" · **Documents:** `links.md`, cases 43, 54, 55, 62–64 · **Settings:** formatted view
1. Ctrl+click each case.
- **Expected:** No dialog. The status bar shows "Only web links (http, https) open from here. Right-click a link to copy it."
- **Type:** Both. Automated: `web-links.test.mjs` "a relative, file:, javascript: or mailto: link is refused …" and `LinkOpeningTests.EverythingElseIsRefused`. Human: the status bar note.

#### WEB-04 A Unicode host is shown in punycode, and what's shown is what opens
- **Change:** Fixed (commit 23ad525): the link check · **Documents:** `links.md`, cases 29–31, 77–80 · **Settings:** formatted view
1. Ctrl+click each case and read the dialog.
- **Expected:** Case 29 shows `https://xn--mnchen-3ya.test/`, and case 30 shows `https://xn--ample-ywe6i.test/`. Case 77 shows `http://127.0.0.1/`. Cases 78–80 show their addresses unchanged. If you click **Open**, the browser's address bar shows exactly the dialog's address.
- **Type:** Both. Automated: `LinkOpeningTests.WebLinksAreAccepted`, `LinkOpeningTests.HostsThatCannotShowInPunycodeAreRefused`, and the link replay. Human: the dialog and the browser.

#### WEB-05 Risky addresses are refused with the note, never an error box
- **Change:** Fixed (commit 23ad525): the link check · **Documents:** `links.md`, cases 32–53 and 68–76 · **Settings:** formatted view
1. Ctrl+click every case in **Refused addresses** and **Hosts the link check refuses**, and case 32.
- **Expected:** Each case gives the note, except 50 and 77–80, which show their dialog. The refused cases cover a user name before the host (33, 34), hosts that aren't DNS names (68–70, 72), hosts ending in a number (73–75), a fullwidth digit (76) and hyphen-ending IDN labels (32, 71). No case shows the "hit an unexpected error" box.
- **Type:** Both. Automated: `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment`, `HostsThatCannotShowInPunycodeAreRefused`, and the link replay. Human: that no error box appears in the app.

#### WEB-06 Full sweep of the link document
- **Change:** Added: Ctrl+click and Copy Link · **Documents:** `links.md`, all 80 cases and G1–G7 · **Settings:** formatted view, then Edit ▸ Read Only
1. Follow **How to use** at the top of `links.md`.
- **Expected:** Every case matches its row. Case 66 (an inline raw-HTML `<a>`) does nothing; this is a known limit.
- **Type:** Both. Automated: the link replay confirmed every address and outcome on master a4577ae. Human: real clicks, dialogs and clipboard.

### MENU — Submenu arrows

#### MENU-01 Right and Left open and close the submenu you're on
- **Change:** Fixed: "Right on File ▸ Open Recent or View ▸ Theme opens it at its first entry" · **Documents:** any · **Settings:** at least two recent files
1. Press Alt+F, move down to **Open Recent** and press Right, then Left.
2. Press Alt+V, move to **Theme** and press Right, then Left.
- **Expected:** Right opens the submenu with the first item you can choose highlighted (in View ▸ Theme, **Default**, under the greyed *For Windows … mode* line). Left closes it and leaves you on the parent item. Neither key jumps to the next top-level menu.
- **Type:** Both. Automated: `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `MenuAccessKeysTests.ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn`. Human: WPF keyboard navigation.

### SPL — Spell check on large documents

#### SPL-01 Underlines sit on the right words across chunk boundaries
- **Change:** commit 8c82f9e, large documents are checked in chunks · **Documents:** `large-600kb.md` · **Settings:** turn on **View ▸ Spell Check** after opening, because a document this size opens with it off
1. Open the document and turn on spell check. Wait for the squiggles.
2. Scroll to the top, the middle and the end. At each place, check several paragraphs.
- **Expected:** Every `Qwzxvy` and `qwzxvy` is underlined, and nothing else is. No underline is shifted onto a neighbouring word, including on paragraphs about 16 KB apart.
- **Type:** Both. Automated: `SpellChunkTests.Chunks_BreakAtALineBreak_PreferringABlankLine`, `SpellChunkTests.Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk`, and `SpellChunkTests.CheckInChunks_GivesTheRangesOfOneWholeTextCall`. Human: the Windows spell checker and squiggle placement.

### PERF — Opening performance

#### PERF-01 No stall on the first edit after opening
- **Change:** commits 98e12d3, 6a891f5, 76bb8fd, 17df3fb · **Documents:** `large-600kb.md`, `tables.md` · **Settings:** start the app with `MDM_TIMING=1`
1. Open `large-600kb.md`. As soon as the page appears, type a letter.
2. Press Ctrl+Z. Repeat with `tables.md`.
- **Expected:** The letter appears with no noticeable pause, and the title gains `*` straight away. Ctrl+Z removes both the letter and the `*`. Opening doesn't mark the document modified. The timing log shows the open and the first edit.
- **Type:** Both. Automated: `load-state.test.mjs` (both tests), `marks.test.mjs` "a keystroke keeps every ¶ node already drawn …", and `parser-types.test.mjs`. Human: perceived timing.

#### PERF-02 The timing log is written only when asked for
- **Change:** commit 17df3fb · **Documents:** `br-forms.md` · **Settings:** none
1. Delete `%TEMP%\MarkdownMidget-timing.log` if it exists. Start the app normally, open the document, press Ctrl+E twice and close.
2. Repeat with `MDM_TIMING=1`, as in section 1.3.
- **Expected:** After step 1, there is no log file. After step 2, the log has one line per phase: time of day, sequence, phase and whole milliseconds.
- **Type:** Both. Automated: `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture`. Human: the environment variable in the real app.

### SWT — Switching back to the formatted view

#### SWT-01 A large file shows the busy box, then "Drawing page…"
- **Change:** Added: "Switching from the Markdown source view back to the formatted view shows the busy spinner" · **Documents:** `large-600kb.md` · **Settings:** open in the formatted view, press Ctrl+E and wait
1. Press Ctrl+E to go back to the formatted view. Watch the window.
- **Expected:** A busy box over the source view says "Switching to the formatted view…". When the formatted page replaces it, the status bar says "Drawing page…" until the page is drawn. There is no flicker and no blank flash.
- **Type:** Both. Automated: `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame`. Human: how it looks.

#### SWT-02 A small file switches with no busy box
- **Change:** Added: spinner · **Documents:** `br-forms.md` · **Settings:** source view
1. Press Ctrl+E several times.
- **Expected:** Each switch finishes with no busy box, no flash and no flicker.
- **Type:** Human. The 200 ms threshold and flashing are visual.

#### SWT-03 Typing and Ctrl+B are blocked during the switch, and nothing is lost
- **Change:** Added: "until the switch is done the source view can't be typed in" · **Documents:** `large-600kb.md` · **Settings:** source view
1. At the top, type `EDIT-MARK` on a new line.
2. Press Ctrl+E. While the busy box shows, type `abc` and press Ctrl+B.
- **Expected:** `abc` isn't typed and nothing turns bold. After the switch, `EDIT-MARK` is in the formatted view and `abc` isn't.
- **Type:** Human. Keyboard input during a real WebView2 load.

#### SWT-04 Pressing Ctrl+E twice gives one switch
- **Change:** Added: spinner · **Documents:** `large-600kb.md` · **Settings:** source view
1. Press Ctrl+E twice quickly.
- **Expected:** One switch to the formatted view. It doesn't bounce back to the source view, and the busy box and status note clear.
- **Type:** Both. Automated: `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator`. Human: real key presses.

#### SWT-05 Replace All during the switch replaces nothing
- **Change:** Added: spinner · **Documents:** `large-600kb.md` · **Settings:** source view
1. Press Ctrl+F. Search for `apple` and replace it with `pear`.
2. Press Ctrl+E, and click **Replace All** while the busy box shows.
- **Expected:** The Find dialog says "Switching views — nothing replaced." After the switch, the document still says `apple`.
- **Type:** Human. The Find dialog during a real switch.

#### SWT-06 Ctrl+E straight after the page appears leaves nothing behind
- **Change:** Added: spinner · **Documents:** `large-600kb.md` · **Settings:** source view
1. Press Ctrl+E. As soon as the formatted page appears, press Ctrl+E again.
- **Expected:** You're back in the source view, with no busy box and no "Drawing page…" left in the status bar. The text is unchanged.
- **Type:** Human. Timing against the real paint.

#### SWT-07 Read-only documents and Help stay read-only
- **Change:** Added: spinner · **Documents:** `large-600kb.md`, the Help window · **Settings:** Edit ▸ Read Only on
1. Press Ctrl+E twice. Try to type in each view.
2. Open **Help ▸ View Help** and try to type.
- **Expected:** You can't type in either view, and **Edit ▸ Read Only** stays ticked. Help stays read-only.
- **Type:** Human. The read-only state after a real switch.

### FM — Front matter

#### FM-01 Front matter shows as a grey block with a tooltip
- **Change:** Fixed: "YAML front matter survives a save from the formatted view" · **Documents:** `front-matter.md` · **Settings:** Spell Check on
1. Open the document. Hover over the grey block.
- **Expected:** The top of the document is a grey code block showing its `---` lines. The title has no `*`. Hovering shows the tooltip "Front matter". Words inside the block aren't underlined, but "misspeled" below it is.
- **Type:** Both. Automated: `front-matter.test.mjs` "front matter opens as no edit and saves byte for byte …" and "… its words are code to spell check". Human: the look and the tooltip.

#### FM-02 A save is byte-identical, including after editing a value
- **Change:** Fixed: front matter · **Documents:** `front-matter.md`, `front-matter-blank-lines.md`, `%TEMP%\mdm-test-docs\front-matter-bom-crlf.md` (generated, see 1.2), `front-matter-at-eof.md` · **Settings:** formatted view
1. Open each document, choose **File ▸ Save As…** and save under a new name. Compare with `fc.exe /b`.
2. In `front-matter.md`, change `draft: true` to `draft: false` and save. Compare with the original.
3. In `front-matter-at-eof.md`, add `, edited` to the end of the `title:` line. Save under a new name and compare. Reopen that file and save it again.
- **Expected:** In step 1, every file is identical, `front-matter-at-eof.md` included. The byte-order mark and CRLF endings are kept. In step 2, only the `draft:` line differs. In step 3, the `title:` line differs and the file gains a final newline. The second save changes nothing.
- **Type:** Both. Automated: the fixture round-trip check (byte-identical for all four on master 437ccc9) and `front-matter.test.mjs` "front matter opens as no edit and saves byte for byte …". Human: the host's save, byte-order mark and line endings.

#### FM-03 Enter after the closing `---` still saves as front matter
- **Change:** Fixed: front matter · **Documents:** `front-matter.md` · **Settings:** formatted view
1. Put the caret at the end of the closing `---` line in the grey block and press Enter. Save and reopen.
- **Expected:** The file still starts with the same front matter, and the new line is saved as a blank line after it. On reopen, the block is still grey front matter.
- **Type:** Both. Automated: `front-matter.test.mjs` "Enter and typing edit its text …". Human: the real key press.

#### FM-04 Heading, list and style commands are refused inside it
- **Change:** Fixed: front matter · **Documents:** `front-matter.md` · **Settings:** formatted view
1. Put the caret in the grey block. Press Ctrl+1, click the bullet list button and apply a style from the menus. Type `## ` at the start of a line.
- **Expected:** The block stays a grey front matter block. Nothing turns into a heading, list or quote. Typed text goes in as plain text.
- **Type:** Both. Automated: `front-matter.test.mjs` "… input rules and the toolbar cannot make it another block …". Human: the real toolbar and menus.

#### FM-05 Line numbers and Ctrl+G work inside and after it
- **Change:** Fixed: front matter; #10 · **Documents:** `front-matter.md` · **Settings:** Show Line Numbers on
1. Read the margin. Press Ctrl+G to lines 3, 6 and 8.
- **Expected:** The block reads `1–6` and the heading reads `8`. Go to Line reaches each line and reads `Ln N`.
- **Type:** Both. Automated: `front-matter.test.mjs` "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line …". Human: the margin.

### TBL — Tables

#### TBL-01 A no-edit save keeps each table's layout
- **Change:** Fixed: "Saving from the formatted view keeps each table's layout" · **Documents:** `tables.md` · **Settings:** formatted view
1. Open the document and save it under a new name with **File ▸ Save As…**. Compare with `fc.exe /b`.
- **Expected:** The file is identical, every table included. Tables change only after they're edited: TBL-02, TBL-03, TBL-06 and TBL-07 cover what an edit changes.
- **Type:** Both. Automated: `table-fidelity.test.mjs` "…: byte-identical; edited, saved, reopened and saved again" (aligned, unaligned, GitHub styles, empty cells, Prettier …) and the fixture round-trip check. Human: one save through the app.

#### TBL-02 Editing a cell changes only that row, unless the column widens
- **Change:** Fixed: tables · **Documents:** `tables.md` · **Settings:** formatted view
1. In **Aligned**, change `3` to `4`. Save and compare.
2. In **Prettier style**, change `Mercury` to `Mercury-long-name`. Save and compare.
- **Expected:** In step 1, only the `apple` row differs. In step 2, that column widens in every row of that table only, and the delimiter row widens to match.
- **Type:** Both. Automated: `table-fidelity.test.mjs` "aligned, a cell grows" and "Prettier, a cell grows past its width". Human: editing in the real view.

#### TBL-03 Old `| <br /> |` cells now open empty, and save as `| |` once their table is edited
- **Change:** Fixed: "an empty cell is no longer saved as `<br />`" · **Documents:** `tables.md` · **Settings:** formatted view
1. Open the document and save it under a new name with no edit.
2. In **Empty cells written by earlier versions**, change `h2` to `h2X`. Save, then reopen the saved file and save it again.
- **Expected:** In **Empty cells written by earlier versions**, the `<br />` cells show empty. In step 1, the file is identical. In step 2, only that table's header row and its two `<br />` rows change, to `| | text |` and `| text | |`. The second save changes nothing.
- **Type:** Automated (Claude). `roundtrip.test.mjs` InlineBreakSurvives "a cell holding only a break loads as an empty cell", and the fixture round-trip check.

#### TBL-04 A new table saves aligned, with columns at least 3 wide
- **Change:** Fixed: "a new table is lined up unless a cell is longer than 80 characters" · **Documents:** a new document · **Settings:** formatted view
1. Insert a table from the toolbar. Type `a`, `b` in the header and `1`, `2` in a row. Save and open the file in Notepad.
2. Type 81 characters into one cell and save again.
- **Expected:** In step 1, the table is lined up, with each column at least 3 characters wide (`| a   | b   |`) and empty cells saved empty. In step 2, the table is saved unaligned.
- **Type:** Both. Automated: `table-fidelity.test.mjs` "a new table: aligned, columns at least 3 wide, up to 80 characters in a cell; unaligned past it; empty cells stay empty". Human: inserting from the toolbar.

#### TBL-05 A large table-heavy document doesn't grow on save
- **Change:** Fixed: "A file with long table cells no longer grows several times over" · **Documents:** `large-600kb.md` · **Settings:** none
- **Expected:** A no-edit save is the same size as the file, and identical to it.
- **Type:** Automated (Claude). `table-fidelity.test.mjs` "wide-cell repro: the save is the size of the file …", and the fixture round-trip check on `large-600kb.md` (identical on master a4577ae).

#### TBL-06 A centred column written left-justified now keeps its layout until the table is edited
- **Change:** Fixed: tables ("stays lined up at the column widths it was written with") · **Documents:** `tables.md` · **Settings:** none
1. Save the document with no edit.
2. In **Aligned, a centred column written left-justified**, change `red` to `pink`. Save, then reopen and save again.
- **Expected:** In step 1, the table is identical. In step 2, the `Note` header and the edited row are re-centred: `| Note   |` becomes `|  Note  |`, and the cell saves as `|  pink  |`. The `fig` row is identical, and the second save changes nothing. Section 5 lists this re-centring as a candidate limitation.
- **Type:** Automated (Claude). Fixture round-trip check, with and without the edit.

#### TBL-07 Outer pipes and CJK tidying now happen only after an edit, and are stable
- **Change:** Fixed: tables · **Documents:** `tables.md` · **Settings:** none
1. Save the document with no edit.
2. In **No outer pipes**, change `2` to `3`. In **CJK**, change `3` to `4`. Save, then reopen and save again.
- **Expected:** In step 1, both tables are identical. In step 2, **No outer pipes** gains outer pipes (`|a|b|`, `|---|---|`, `|1|3|`), and the edited **CJK** row loses its extra padding (`| 3  |` becomes `| 4 |`). The second save changes nothing.
- **Type:** Automated (Claude). Fixture round-trip check, with the edits and a second pass.

### BR — Inline line breaks

#### BR-01 An inline `<br>` shows as a line break
- **Change:** Fixed: "An inline `<br>` … is no longer deleted when a document is opened" · **Documents:** `br-forms.md` · **Settings:** formatted view
1. Open the document.
- **Expected:** Each `<br>`, `<br/>` and `<br />` shows as a line break, in the paragraphs, the table cells, the list items and the quote. No text is joined.
- **Type:** Both. Automated: `roundtrip.test.mjs` InlineBreakSurvives "an inline break renders as a line break". Human: WebView2 rendering.

#### BR-02 Every form is kept on save, and so is a hard break before it
- **Change:** Fixed: inline `<br>` · **Documents:** `br-forms.md` · **Settings:** none
- **Expected:** A no-edit save is identical, including `line one<br>line two`, the backslash break before `<br>`, and the plain line ending before `<br>`. It is still identical after an edit elsewhere.
- **Type:** Automated (Claude). `roundtrip.test.mjs` InlineBreakSurvives "the line ending before an inline break is kept …", and the fixture round-trip check (identical on master a4577ae).

#### BR-03 An empty-line marker paragraph still works
- **Change:** Fixed: inline `<br>` · **Documents:** `br-forms.md` · **Settings:** none
- **Expected:** The paragraph that is only `<br />` opens as an empty line and saves as `<br />`.
- **Type:** Automated (Claude). `roundtrip.test.mjs` InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line".

### LST — Lists (#11)

#### LST-01 A list item that starts with a block keeps it
- **Change:** Fixed: "A list item that starts with a quote, code block, table, heading, rule or another list keeps it" · **Documents:** `list-leading-blocks.md` · **Settings:** none
- **Expected:** A no-edit save is identical. `- > quoted text` stays as written, not `- <br />`, and the task checkboxes survive.
- **Type:** Automated (Claude). `roundtrip.test.mjs` ListItemLeadingBlockSurvives, and the fixture round-trip check (identical on master a4577ae).

#### LST-02 A paragraph after a nested list keeps its blank line (#11)
- **Change:** #11, Fixed: "A paragraph after a nested list keeps its blank line" · **Documents:** `issue-11-list-paragraph.md` · **Settings:** none
- **Expected:** A no-edit save is identical, including the three cases inside quotes. Reopened, each paragraph is still a paragraph, not part of the list's last item.
- **Type:** Automated (Claude). `roundtrip.test.mjs` ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose, an item after, a quote tail, a table", and the fixture round-trip check (identical on master a4577ae).

#### LST-03 The same, when the list and paragraph are typed in the editor
- **Change:** #11 · **Documents:** a new document · **Settings:** formatted view
1. Type `- one`, Enter, Tab, `nested`, Enter, Enter, Enter, then `After the list.` Make sure it is a plain paragraph.
2. Save, close and reopen. Press Ctrl+E.
- **Expected:** "After the list." is still a separate paragraph, with a blank line before it in the source.
- **Type:** Both. Automated: `roundtrip.test.mjs` ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote". Human: real typing.

### OPN — Opening in a new window

#### OPN-01 Open, Open Recent and Ctrl+O start a new window when this one has a document
- **Change:** Changed: "File ▸ Open, Open Recent and dropping … open it in a new window" · **Documents:** `br-forms.md`, `tables.md` · **Settings:** `tables.md` in Open Recent
1. Open `br-forms.md`. Use **File ▸ Open…** to open `issue-11-list-paragraph.md`.
2. In the first window, use Ctrl+O, then **File ▸ Open Recent ▸ tables.md**.
- **Expected:** Each file opens in its own new window. The first window still shows `br-forms.md`, with no prompt about unsaved changes.
- **Type:** Both. Automated: `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document`. Human: starting real windows.

#### OPN-02 An empty window opens the file in place, and a window still loading doesn't
- **Change:** Changed: "A window showing 'No document open', or a blank untitled document … still opens the first file itself" · **Documents:** `br-forms.md`, `tables.md` · **Settings:** none
1. Press Ctrl+W to get "No document open", then open `br-forms.md`.
2. Press Ctrl+N. In the new blank window, open `tables.md`.
3. Press Ctrl+N, type a letter, and open `tables.md`.
4. Start the app with `large-3mb.md` on the command line. While it loads, use **File ▸ Open Recent** or drop `drop-01.md`.
- **Expected:** Steps 1 and 2 open in the same window. Step 3 opens a new window and keeps the typed letter. Step 4 opens a new window, and the large file still loads in the first one.
- **Type:** Both. Automated: `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way`. Human: real windows and timing.

#### OPN-03 A file dropped on the formatted view is the real file
- **Change:** Fixed: "A markdown file dropped on the formatted view opens as the file itself" · **Documents:** `drop-01.md` · **Settings:** a blank untitled window in the formatted view
1. Drag `drop-01.md` from File Explorer onto the editor.
2. Add a word and press Ctrl+S. Open the file in Notepad. Check **File ▸ Open Recent**.
- **Expected:** The title is `drop-01.md` with no `*`. Save writes to the dropped file, and the word is in Notepad. The file is on Open Recent.
- **Type:** Both. Automated: `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and `file-drop.test.mjs` "postWithFiles hands the host the dropped files …". Human: a real drop and WebView2's file path.

#### OPN-04 A drop opens at most 10 windows
- **Change:** Changed: "A drop opens at most 10 new windows; the status bar names the rest" · **Documents:** `drop-01.md` … `drop-12.md` · **Settings:** a window with a document
1. Select all 12 drop files in File Explorer and drop them on the editor.
- **Expected:** Ten new windows open. The status bar says "At most 10 new windows at once; not opened: drop-11.md, drop-12.md" (the names can be in a different order).
- **Type:** Both. Automated: `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note`. Human: a real multi-file drop.

#### OPN-05 A picture drop embeds, and a folder or zip gets a note
- **Change:** Changed: drops; beta1 picture routing · **Documents:** `picture.png`, `drop-01.zip`, any folder · **Settings:** a window with a document
1. Drop `picture.png`, then `drop-01.zip`, then a folder.
- **Expected:** The picture is inserted at the caret. The zip and the folder each give a status note naming them (for example "Not a picture or a markdown file: drop-01.zip"). No window opens and the document is unchanged.
- **Type:** Both. Automated: `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder`. Human: a real drop, and folders, which no test covers.

#### OPN-06 An encrypted `.mdenc` drop asks for the password
- **Change:** Changed: drops · **Documents:** an `.mdenc` file (see Help, *Secure Markdown*, to make one) · **Settings:** a blank untitled window
1. Drop the `.mdenc` file on the editor.
- **Expected:** The password prompt appears. The right password opens the document, and Cancel leaves the window as it was.
- **Type:** Human. The password dialog on the drop path has no test.

#### OPN-07 A file already open elsewhere brings that window forward
- **Change:** Changed: "Opening the file this window already has just brings it forward"; beta1 #1 · **Documents:** `drop-02.md`, `br-forms.md` · **Settings:** none
1. Open `drop-02.md` in window A and `br-forms.md` in window B.
2. From window B, open `drop-02.md` with File ▸ Open, then with Open Recent, then by dropping it. In window A, open `drop-02.md` again.
- **Expected:** Each time, window A comes to the front and no new window stays open.
- **Type:** Both. Automated: `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` (same-path case). Human: real windows coming to the front.

#### OPN-08 A drop during crash recovery goes to a new window
- **Change:** Changed: drops · **Documents:** a copy of `br-forms.md`, `drop-03.md` · **Settings:** Edit ▸ Settings, backups on
1. Open the copy, type a word, and wait 10 seconds without saving.
2. In Task Manager, end the MarkdownMidget task. Start the app again.
3. As soon as the recovered document appears, drop `drop-03.md` on it.
- **Expected:** `drop-03.md` opens in a new window. The recovered window keeps your word and stays marked unsaved.
- **Type:** Human. Crash recovery and process start-up.

#### OPN-09 Dropping an Outlook attachment or a file inside a zip
- **Change:** Changed: drops (exploratory) · **Documents:** a `.md` email attachment; a `.md` inside an opened zip in File Explorer · **Settings:** a window with a document
1. Drag the attachment from Outlook onto the editor. Note the title and where Save writes.
2. Open a zip in File Explorer and drag a `.md` from it onto the editor. Note the same.
- **Expected:** Record what happens. It's a FAIL if Save quietly writes to a temporary copy you can't find again.
- **Type:** Human. Outlook and zip shell drops can't be simulated.

### BIG — Large documents

#### BIG-01 A document over 512 KB opens with line numbers and spell check off
- **Change:** Added: "A document over 512 KB opens with line numbers and spell check off" · **Documents:** `large-600kb.md` · **Settings:** Show Line Numbers on, Spell Check on
1. Open the document. Read the status bar and the **View** menu. Press Ctrl+E and check again.
- **Expected:** The status bar says "Large document: line numbers and spell check are off. Turn them on from View ▸ Line Numbers ▸ Show Line Numbers and View ▸ Spell Check." It stays visible and isn't replaced within a few seconds. Both items are unticked, and the toolbar button isn't pressed, in both views. There are no squiggles.
- **Type:** Both. Automated: `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document`. Human: the note, ticks, margins and squiggles.

#### BIG-02 Saved settings don't change, and a small document follows them
- **Change:** Added: large documents ("your saved settings don't change") · **Documents:** `large-600kb.md`, `line-numbers.md` · **Settings:** as BIG-01
1. With the large document open, open `line-numbers.md` in a new window.
- **Expected:** The small document has line numbers and spell check on, as saved.
- **Type:** Both. Automated: `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again`. Human: the real settings.

#### BIG-03 Turning a feature on sticks across Ctrl+E
- **Change:** Added: large documents · **Documents:** `large-600kb.md` · **Settings:** as BIG-01
1. Turn on **Show Line Numbers**. Press Ctrl+E twice. Turn on **Spell Check** and press Ctrl+E.
2. Open `line-numbers.md` in a new window after changing the saved settings to off.
- **Expected:** In step 1, both stay on for the large document in both views. In step 2, the small document follows the saved settings.
- **Type:** Both. Automated: `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again`. Human: the menu wiring.

#### BIG-04 A load or switch over 5 seconds turns both off
- **Change:** Added: "A document whose opening or view switch takes more than 5 seconds gets the same" · **Documents:** none · **Settings:** none
- **Expected:** A document whose load or switch takes more than 5 seconds gets both off and the note from then on.
- **Type:** Automated (Claude). `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note`.

#### BIG-05 Reloading after an external change keeps the choices
- **Change:** Added: large documents ("A reload keeps it") · **Documents:** `large-copy.md`, a copy of `large-600kb.md` · **Settings:** as BIG-01
1. In PowerShell, make the copy: `Copy-Item "$env:TEMP\mdm-test-docs\large-600kb.md" "$env:TEMP\mdm-test-copies\large-copy.md"`. Open `large-copy.md` and turn on **Show Line Numbers**.
2. In PowerShell, append a line to the same file: `Add-Content "$env:TEMP\mdm-test-copies\large-copy.md" "External line."`
3. Reload when the app offers it.
- **Expected:** After the reload, line numbers are still on and spell check is still off. The new line is there.
- **Type:** Human. The commit notes the reload wiring as a manual check.

#### BIG-06 Ctrl+W resets the ticks
- **Change:** Added: large documents ("closing it" forgets the choice) · **Documents:** `large-600kb.md` · **Settings:** as BIG-01
1. Turn on **Show Line Numbers** and press Ctrl+W. Open the **View** menu.
- **Expected:** On the "No document open" screen, the ticks show your saved settings again.
- **Type:** Both. Automated: `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings`. Human: the close wiring.

#### BIG-07 Save As in the external-change conflict keeps the choices
- **Change:** Added: large documents · **Documents:** `large-copy.md`, made as in BIG-05 step 1 · **Settings:** as BIG-01
1. Open `large-copy.md`, turn on **Show Line Numbers**, and type a word without saving.
2. In PowerShell, append a line to the same file: `Add-Content "$env:TEMP\mdm-test-copies\large-copy.md" "External line."`
3. When the app reports the conflict, choose to save under a new name.
- **Expected:** The document is saved under the new name, line numbers are still on, and spell check is still off.
- **Type:** Both. Automated: `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices`. Human: the conflict dialog.

#### BIG-08 A 3 MB document opens and switches without a long hang
- **Change:** Added: large documents (Ctrl+E on a 2.8 MB document used to hang for about 20 seconds) · **Documents:** `large-3mb.md` · **Settings:** start with `MDM_TIMING=1`
1. Open the document. Press Ctrl+E, wait, and press Ctrl+E again. Type a word.
- **Expected:** Line numbers and spell check are off, with the note. Each switch finishes, with the busy box on the way back. Typing is responsive. Attach the timing log lines to the note.
- **Type:** Human. Perceived timing on a real machine.

### SRC — Block-level source preservation

> **Applies from the first build that includes source-keep.** Before that build, mark every SRC test N/A. The design is as follows. Saving from the formatted view, Save As, backups, and Formatted → Source all write untouched top-level blocks byte for byte from the original text. Only edited blocks are re-serialised, and a no-edit save is byte-identical. On master a4577ae, `source-keep-roundtrip.md` comes back heavily rewritten, which is what these tests guard against.

#### SRC-01 A no-edit save is byte-identical
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md`, then every other fixture and `large-600kb.md` · **Settings:** formatted view
1. Open the document, click around without typing, and use **File ▸ Save As…** to save it under a new name.
2. Compare with `fc.exe /b`.
- **Expected:** "FC: no differences encountered" for every document. That includes `~ * [ _` text, both rules, the `*` and `+` lists, the `1)` list, the setext heading, reference definitions, the HTML comment and the indented code.
- **Type:** Both. Automated: fixture round-trip check on every fixture. Human: the host's save path.

#### SRC-02 Formatted → Source with no edit shows the original text
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md` · **Settings:** open in the formatted view
1. Press Ctrl+E. Compare the source view with the file in Notepad.
- **Expected:** The text is identical, with `* star bullet one`, `Setext heading level two` and its underline, and `~tilde~` as written.
- **Type:** Both. Automated: fixture round-trip check. Human: the real switch.

#### SRC-03 Editing one block changes only that block
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md` · **Settings:** formatted view
1. At the end of the last paragraph, type ` Edited.` Save As under a new name and compare with `fc.exe`.
- **Expected:** Only the last paragraph's line differs. Every other line is identical.
- **Type:** Both. Automated: fixture round-trip check with that one edit. Human: the host's save.

#### SRC-04 Editing a block in the middle leaves its neighbours alone
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md` · **Settings:** formatted view
1. In "Bold around code", change `now` to `today`. Save As and compare.
- **Expected:** Only that paragraph differs, and it may be written in the app's conventions. The `* star` list after it, the `+` list, the rules, the reference definitions and the HTML comment are identical.
- **Type:** Both. Automated: fixture round-trip check with that edit. Human: the host's save.

#### SRC-05 Adding and deleting blocks
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md` · **Settings:** formatted view
1. Add a paragraph `New paragraph.` after "Rule written with dashes:". Delete the "Rule written with stars:" paragraph. Save As and compare.
- **Expected:** The new paragraph appears and the deleted one is gone. Every other block is identical, including the blank lines between untouched blocks.
- **Type:** Both. Automated: fixture round-trip check with those edits. Human: the host's save.

#### SRC-06 Undoing back to the original saves identical bytes
- **Change:** source preservation · **Documents:** `source-keep-roundtrip.md`, `tables.md` · **Settings:** formatted view
1. Edit a word in two blocks and in one table cell. Undo every edit until the `*` goes. Save As and compare.
- **Expected:** Identical to the original.
- **Type:** Human. Real undo history.

#### SRC-07 Backups hold the preserved text
- **Change:** source preservation · **Documents:** a copy of `source-keep-roundtrip.md` · **Settings:** backups on
1. Edit the last paragraph and wait 10 seconds without saving.
2. Copy the newest file from `%LocalAppData%\MarkdownMidget\backup` to another folder, and compare the copy with the original.
- **Expected:** Only the last paragraph differs.
- **Type:** Human. The backup timer and folder.

#### SRC-08 Line endings and the byte-order mark are kept
- **Change:** source preservation · **Documents:** `%TEMP%\mdm-test-docs\front-matter-bom-crlf.md` (generated, see 1.2); a CRLF copy of the corpus made with the commands below · **Settings:** formatted view

  ```powershell
  $t = [IO.File]::ReadAllText("$env:TEMP\mdm-test-copies\source-keep-roundtrip.md") -replace "`n", "`r`n"
  [IO.File]::WriteAllText("$env:TEMP\mdm-test-copies\source-keep-crlf.md", $t)
  ```

1. Save As each document with no edit, and compare.
2. Edit the last paragraph of the CRLF copy, Save As and compare.
- **Expected:** In step 1, both are identical. In step 2, only the last paragraph differs, and every line still ends in CRLF.
- **Type:** Human. The host applies line endings and the byte-order mark.

### INST — Install and update

#### INST-01 Updating over an existing install keeps the .md default
- **Change:** Fixed: "Updating Markdown Midget now keeps your file associations" · **Documents:** a copy of `br-forms.md` · **Settings:** this build installed and registered; another app that opens `.md` files installed, such as Markdown Monster or Visual Studio Code
1. Right-click the copy ▸ **Open with** ▸ **Choose another app** ▸ **Choose an app on your PC**, pick `%LocalAppData%\Programs\MarkdownMidget\MarkdownMidget.exe`, then **Always**.
2. Run **File ▸ Windows Integration ▸ Register as .md editor…** again. Then, if **Help ▸ About Markdown Midget** offers an update, click **Update**; if not, write N/A for this part in the note.
3. After each step, double-click the copy and look at Windows **Settings ▸ Apps ▸ Default apps** for `.md`.
- **Expected:** The copy opens in Markdown Midget each time, Settings still names Markdown Midget for `.md`, and Windows shows no notice that an app default was reset.
- **Control:** on rc1 (build 328), step 1 then Register resets `.md` to the other app. If that doesn't happen, this test can't fail; say so in the note.
- **Type:** Human. Only a person can set a Windows default app, and no test may touch the real registry. An update is carried out by the version already installed, so it proves the fix only from a build that has it.

#### INST-02 Register with Move keeps a downloaded copy you chose working
- **Change:** Fixed: "Updating Markdown Midget now keeps your file associations" · **Documents:** a copy of `br-forms.md` · **Settings:** this build's exe in Downloads; another app that opens `.md` files installed
1. Right-click the copy ▸ **Open with** ▸ **Choose another app** ▸ **Choose an app on your PC**, pick the exe in Downloads, then **Always**.
2. From that exe, run **File ▸ Windows Integration ▸ Register as .md editor…** with **Move the downloaded file into the app folder** ticked.
3. Close every Markdown Midget window and double-click the copy.
- **Expected:** The download is gone from Downloads, and the copy opens in the installed Markdown Midget with no error and no app chooser.
- **Type:** Human. Windows decides what a default chosen that way runs, and no test may touch the real registry.

#### INST-03 Make default, and the notice after an update
- **Change:** Added: "Markdown Midget now helps you set it as the .md default" and "After an update, Markdown Midget now tells you if Windows doesn't open .md files with it"; Fixed: "Registering with "Make it my default for .md files" now opens Settings on Markdown Midget's page" · **Settings:** this build installed and registered; this build's exe in Downloads; another app that opens `.md` files installed. To stand in for an update, close Markdown Midget and set `"LastRunVersion"` to `"v0"` in `%LocalAppData%\MarkdownMidget\settings.json`.
1. On Windows 11, then on Windows 10, click **File ▸ Windows Integration ▸ Make Markdown Midget the default…**.
2. Unregister, keeping the installed copy; open **Register as .md editor…** and hover **Make Markdown Midget the default…**; then register again.
3. Set `.md` to Markdown Midget and start it once. Set `.md` to the other app, stand in for an update, and start it from the Start menu: click **Not now**. Start it again.
4. Repeat step 3, ticking **Don't show this again** before **Not now**. Then repeat step 3 once more.
5. Repeat step 3 with a copy of the exe outside `%LocalAppData%\Programs\MarkdownMidget\`.
6. Repeat step 3, but start it by opening a `.md` file with **Open with** ▸ **Markdown Midget**. If **Help ▸ About Markdown Midget** offers an update, also apply it with a document open.
7. Repeat step 3, and while the notice shows, start a second Markdown Midget from the Start menu.
8. Close Markdown Midget and delete the `"MdOpensWithUs"` entry from settings.json, as a copy updated from rc1 has none. With `.md` set to the other app (**Open with** ▸ **Choose another app** ▸ **Always**), stand in for an update and start it: click **Not now**. Start it again.
9. With rc1 (build 328) installed, registered and set as the `.md` default, apply the in-app update to this build (**Help ▸ About Markdown Midget** ▸ **Update**). While this build's first start is up, note in Windows Settings what `.md` opens with.
10. Unregister, then register with **Make it my default for .md files** ticked, and click **OK**.
11. Close every Markdown Midget window. Start this build's exe in Downloads and register it with **Move the downloaded file into the app folder** and **Make it my default for .md files** ticked.
- **Expected:** 1: Settings opens on Markdown Midget's page on Windows 11, and on Default apps on Windows 10. 2: the button is greyed out and its tooltip says to register first. 3: "Windows no longer opens .md files with Markdown Midget." shows on the first start only. 4: no notice in the last repeat. 5: no notice. 6: the notice appears once the document has loaded. 7: the second window shows no notice. 8: "Windows doesn't open .md files with Markdown Midget." on the first start only. 9: the note says what `.md` opened with; unless that is Markdown Midget, the notice says "Windows doesn't open .md files with Markdown Midget." 10: Settings opens on Markdown Midget's page (Windows 11 22H2+), as the message says. 11: after the installed copy starts, a message says it's registered and where Settings opens, then Settings opens on Markdown Midget's page (Windows 11 22H2+).
- **Type:** Human. Only a person can set a Windows default app, and no test may touch the real registry, open Settings or start the app.

## 4. Run log

For each build, copy the empty template below and paste it above the template. Fill in the heading, then record a result for every test. Automated and the automated half of Both are Claude's; developers record the rest. For a Both test, write both parts in the note, for example `auto PASS; human PASS`.

### Build 284 (1.0.0-beta1+build.284) — 2026-09-15

Automated items only, run by Claude on 2026-09-15 in `C:\code\MarkdownMidget\.claude\worktrees\test-plan` (product code = master a4577ae = build 284). The branch moved from 3a02d04 to 0fb923a during the run. That commit changed one line of section 5 of the plan (the escaped-pipe limit) and nothing else, so the results are unaffected.

- `dotnet test tests/MarkdownMidget.Tests`: 1532 passed, 1 failed (`dotnet-284.trx`). The failure isn't a plan-named test: `SourceEncodingTests.NoTextFileStartsWithAByteOrderMark` reports "UTF-8 byte-order mark at the start of: docs/test-plan/documents/front-matter-bom-crlf.md", so the fixture itself trips the repo's no-BOM rule. All 36 plan-named C# tests passed.
- `npm test` (editor-src, spec reporter): 397 pass, 0 fail (`npm-284.txt`). Every plan-named jsdom title was found and passed.
- Fixture round-trip check: `roundtrip-284.mjs`, output in `roundtrip-284.txt`, saved bytes in `rt\`. It loads with a stripped BOM and folds endings to LF, then saves with the original endings and BOM, as `DocumentText.Detect`/`Encode` do. Pass 2 reopens pass 1's bytes.
- Not re-run: the WEB **link replay**. It needs a new .NET harness around `LinkOpening.TryValidate`, which this run doesn't build. The plan records it as confirmed on master a4577ae, the same commit as build 284.
- SRC: build 284 doesn't include source-keep, so every SRC test is N/A, as section 3 says.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | Claude (2026-09-15) | auto PASS — human pending. `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `TheColumnCountsCharactersTheWayAReaderDoes` (6 cases), line-map "the caret's line in a nested list…" all pass. |
| LIN-02 | | Claude (2026-09-15) | auto PASS — human pending. line-map "the margin numbers top-level blocks…" and "the margin redraws…" pass. |
| LIN-03 | | Claude (2026-09-15) | auto PASS — human pending. line-map range, gap-label, nested-list range and "every line … once and in order" tests pass. |
| LIN-04 | | Claude (2026-09-15) | auto PASS — human pending. line-map Go to Line tests (3 titles) and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse` (9 cases) pass. |
| LIN-05 | | Claude (2026-09-15) | auto PASS — human pending. line-map "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save…" pass. |
| LIN-06 | | Claude (2026-09-15) | auto PASS — human pending. line-map "a table or a code fence ending a file with no final newline…" passes. |
| VIEW-01 | | | |
| VIEW-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered…` (4 cases), `A_file_opening_into_the_source_view_is_never_offered_it` (2 cases) pass. |
| ANC-01 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "HELP's Known limits link: a plain click edits, Ctrl+click jumps…" passes. |
| ANC-02 | | Claude (2026-09-15) | auto PASS — human pending. Same anchor-links test (read-only half) passes. |
| ANC-03 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "a repeated heading takes its numbered anchor…" and "an unmatched or malformed fragment does nothing…" pass. |
| ANC-04 | PASS | Claude (2026-09-15) | `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists` (HELP, README, CHANGELOG, ROADMAP), `ARepeatedHeadingGetsGitHubsNumberedAnchors`, anchor-links "a slug is DocAnchorLinksTests' slug" pass. |
| LNK-01 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click on a link reports its mark href…" and `CopyLinkTests.CopyLinkWritesExactlyTheHref…` pass. Link replay not re-run (see WEB-06). |
| LNK-02 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click off a link or on a raw-HTML <a> reports none…" passes. |
| LNK-03 | | | |
| LNK-04 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… a linked picture reports its link …" passes. |
| LNK-05 | | | |
| LNK-06 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… with no target the caret decides" passes. |
| LNK-07 | | Claude (2026-09-15) | auto PASS — human pending. `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash` passes. |
| WEB-01 | | Claude (2026-09-15) | auto PASS — human pending. web-links "Ctrl+click posts openLink with the mark's href…" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole` pass. |
| WEB-02 | | Claude (2026-09-15) | auto PASS — human pending. web-links "… a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link" pass. |
| WEB-03 | | Claude (2026-09-15) | auto PASS — human pending. web-links "a relative, file:, javascript: or mailto: link is refused…" and `LinkOpeningTests.EverythingElseIsRefused` (31 cases) pass. |
| WEB-04 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.WebLinksAreAccepted` (10 cases) and `HostsThatCannotShowInPunycodeAreRefused` (17 cases) pass. Link replay not re-run (see WEB-06). |
| WEB-05 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment` (2 cases), `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-06 | | Claude (2026-09-15) | auto BLOCKED — human pending. This test's only automated part is the link replay (links.md addresses through `LinkOpening.TryValidate`). This run didn't re-run it because it needs a new .NET harness. The plan records it as confirmed on master a4577ae, the same commit as build 284. |
| MENU-01 | | Claude (2026-09-15) | auto PASS — human pending. `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `MenuAccessKeysTests+OnARealMenu.ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn` pass. |
| SPL-01 | | Claude (2026-09-15) | auto PASS — human pending. The three tests live in class `SpellChunkTests`, not `SpellingTests` as the plan says: `Chunks_BreakAtALineBreak_PreferringABlankLine` (LF, CRLF), `Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk`, `CheckInChunks_GivesTheRangesOfOneWholeTextCall` pass. |
| PERF-01 | | Claude (2026-09-15) | auto PASS — human pending. load-state.test.mjs, parser-types.test.mjs (whole files, 0 fail in the suite) and marks "a keystroke keeps every ¶ node already drawn…" pass. |
| PERF-02 | | Claude (2026-09-15) | auto PASS — human pending. `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture` passes. |
| SWT-01 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame` pass. |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator` passes. |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "front matter opens as no edit and saves byte for byte…" and "… its words are code to spell check" pass. |
| FM-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: front-matter.md (242 B), front-matter-blank-lines.md (77 B) and front-matter-bom-crlf.md (103 B, BOM + CRLF kept) are byte-identical. front-matter-at-eof.md gains only a final LF (72 → 73 B). All are stable on pass 2. The front-matter "opens as no edit and saves byte for byte…" test passes. |
| FM-03 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "Enter and typing edit its text…" passes. |
| FM-04 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "… input rules and the toolbar cannot make it another block…" passes. |
| FM-05 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line…" passes. |
| TBL-01 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "…: byte-identical; edited, saved, reopened and saved again" (12 cases) pass. Fixture check: tables.md 1158 → 1143 B. Differences are only in "Aligned, a centred column written left-justified", "Empty cells written by earlier versions", "No outer pipes" and "CJK", all expected. Stable on pass 2. |
| TBL-02 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "aligned, a cell grows" and "Prettier, a cell grows past its width" pass. |
| TBL-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "a cell holding only a break loads as an empty cell" passes. Fixture check: `\| <br /> \| text \|` → `\| \| text \|` and `\| text \| <br /> \|` → `\| text \| \|`, and pass 2 is unchanged. |
| TBL-04 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "a new table: aligned, columns at least 3 wide…" passes. |
| TBL-05 | PASS | Claude (2026-09-15) | table-fidelity "wide-cell repro: the save is the size of the file…" passes. Fixture check: large-600kb.md (generated, 619,574 B) is byte-identical after a no-edit save, and stable on pass 2 (3.3 s in jsdom). |
| TBL-06 | FAIL | Claude (2026-09-15) | Expected FAIL (section 5 candidate). The fixture check re-centres the centred column on the first save. L12 `\| Name  \| Note   \|` → `\| Name  \|  Note  \|`, and L14 `\| apple \| red    \|` → `\| apple \|   red  \|`. Pass 2 is stable (no further change). |
| TBL-07 | PASS | Claude (2026-09-15) | Fixture check. No outer pipes: `a \| b` / `--- \| ---` / `1 \| 2` → `\|a\|b\|` / `\|---\|---\|` / `\|1\|2\|`. CJK: `\| 林檎 \| 3  \|` → `\| 林檎 \| 3 \|`. Pass 2 changes nothing more. |
| BR-01 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip InlineBreakSurvives "an inline break renders as a line break" passes. |
| BR-02 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "the line ending before an inline break is kept…" passes. Fixture check: br-forms.md (534 B) is byte-identical and stable on pass 2. With "Edited " typed at the first text, exactly one line differs. |
| BR-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line" passes. |
| LST-01 | PASS | Claude (2026-09-15) | roundtrip ListItemLeadingBlockSurvives (suite) passes. Fixture check: list-leading-blocks.md (530 B) is byte-identical and stable on pass 2. |
| LST-02 | PASS | Claude (2026-09-15) | roundtrip ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose…" pass. Fixture check: issue-11-list-paragraph.md (336 B) is byte-identical and stable on pass 2. |
| LST-03 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote" passes. |
| OPN-01 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` (4 cases) passes. |
| OPN-02 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way` (9 cases) passes. |
| OPN-03 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and file-drop "postWithFiles hands the host the dropped files…" pass. |
| OPN-04 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note` passes. |
| OPN-05 | | Claude (2026-09-15) | auto PASS — human pending. `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder` pass. |
| OPN-06 | | | |
| OPN-07 | | Claude (2026-09-15) | auto PASS — human pending. `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` (same-path case `c:\DOCS\A.md`) pass. |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document` passes. |
| BIG-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `Turning_one_on_is_the_documents_own_choice_and_sticks…` pass. |
| BIG-03 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again` passes. |
| BIG-04 | PASS | Claude (2026-09-15) | `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note` passes (5000 ms → not off; 5001 ms → off). |
| BIG-05 | | | |
| BIG-06 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings` passes. |
| BIG-07 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices` passes. |
| BIG-08 | | | |
| SRC-01 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-02 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-03 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-04 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-05 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-06 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-07 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |
| SRC-08 | N/A | Claude (2026-09-15) | Build 284 (a4577ae) predates source-keep. |

### Build 304 (1.0.0-beta1+build.304) — 2026-09-15

Automated items only, run by Claude on 2026-09-15 in `C:\code\MarkdownMidget\.claude\worktrees\source-keep` on branch `runlog-304`. The product code is master 437ccc9, which is build 304 and the first build with source-keep.

- `dotnet test tests/MarkdownMidget.Tests`: 1533 passed, 0 failed (`dotnet.trx`). All 38 plan-named C# tests were found and passed. Build 284's one failure (`SourceEncodingTests.NoTextFileStartsWithAByteOrderMark`) is gone, because the BOM fixture is now generated instead of committed.
- `npm test` (editor-src, spec reporter): 438 pass, 0 fail (`npm.txt`). All 47 plan-named jsdom titles were found and passed.
- Fixture round-trip check: `roundtrip-304.mjs`, output in `roundtrip-304.txt`. It loads the way `MDM.setMarkdown` does (beginLoad, replaceAll, `settleDocument`, endLoad) and saves the way the host does (`MDM.getMarkdown`, then `lineBaseSaved`). The BOM and line endings are handled as `DocumentText.Detect` and `Encode` do. Pass 2 reopens pass 1's bytes. It covers all 13 committed fixtures, the generated `front-matter-bom-crlf.md` and `large-600kb.md`, and a CRLF copy of `source-keep-roundtrip.md`. The CRLF copy is for information only, because SRC-08 is a human test. In jsdom its no-edit save is identical, and an edit to the last paragraph changes only that line and keeps CRLF throughout.
- Not re-run: the WEB **link replay**, for the same reason as build 284.
- Stale expectations: with source-keep, a no-edit save is byte-identical. FM-02 (`front-matter-at-eof.md`), TBL-01, TBL-03, TBL-06 and TBL-07 still expect changes on the first save that no longer happen. They are recorded as PASS, with notes. The test definitions are unchanged.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | Claude (2026-09-15) | auto PASS — human pending. `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `TheColumnCountsCharactersTheWayAReaderDoes` and line-map "the caret's line in a nested list…" pass. |
| LIN-02 | | Claude (2026-09-15) | auto PASS — human pending. line-map "the margin numbers top-level blocks…" and "the margin redraws…" pass. |
| LIN-03 | | Claude (2026-09-15) | auto PASS — human pending. line-map range, gap-label, nested-list range and "every line … once and in order" tests pass. |
| LIN-04 | | Claude (2026-09-15) | auto PASS — human pending. line-map Go to Line tests (3 titles) and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse` pass. |
| LIN-05 | | Claude (2026-09-15) | auto PASS — human pending. line-map "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save…" pass. |
| LIN-06 | | Claude (2026-09-15) | auto PASS — human pending. line-map "a table or a code fence ending a file with no final newline…" passes. |
| VIEW-01 | | | |
| VIEW-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered…` and `A_file_opening_into_the_source_view_is_never_offered_it` pass. |
| ANC-01 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "HELP's Known limits link: a plain click edits, Ctrl+click jumps…" passes. |
| ANC-02 | | Claude (2026-09-15) | auto PASS — human pending. Same anchor-links test (read-only half) passes. |
| ANC-03 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "a repeated heading takes its numbered anchor…" and "an unmatched or malformed fragment does nothing…" pass. |
| ANC-04 | PASS | Claude (2026-09-15) | `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists`, `ARepeatedHeadingGetsGitHubsNumberedAnchors` and anchor-links "a slug is DocAnchorLinksTests' slug" pass. |
| LNK-01 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click on a link reports its mark href…" and `CopyLinkTests.CopyLinkWritesExactlyTheHref…` pass. Link replay not re-run (see WEB-06). |
| LNK-02 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click off a link or on a raw-HTML <a> reports none…" passes. |
| LNK-03 | | | |
| LNK-04 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… a linked picture reports its link …" passes. |
| LNK-05 | | | |
| LNK-06 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… with no target the caret decides" passes. |
| LNK-07 | | Claude (2026-09-15) | auto PASS — human pending. `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash` passes. |
| WEB-01 | | Claude (2026-09-15) | auto PASS — human pending. web-links "Ctrl+click posts openLink with the mark's href…" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole` pass. |
| WEB-02 | | Claude (2026-09-15) | auto PASS — human pending. web-links "… a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link" pass. |
| WEB-03 | | Claude (2026-09-15) | auto PASS — human pending. web-links "a relative, file:, javascript: or mailto: link is refused…" and `LinkOpeningTests.EverythingElseIsRefused` pass. |
| WEB-04 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.WebLinksAreAccepted` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-05 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-06 | | Claude (2026-09-15) | auto BLOCKED — human pending. This test's only automated part is the link replay, which runs the links.md addresses through `LinkOpening.TryValidate`. It needs a new .NET harness, which this run doesn't build. The plan last records it as confirmed on master a4577ae. |
| MENU-01 | | Claude (2026-09-15) | auto PASS — human pending. `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn` pass. |
| SPL-01 | | Claude (2026-09-15) | auto PASS — human pending. `SpellChunkTests.Chunks_BreakAtALineBreak_PreferringABlankLine`, `Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk` and `CheckInChunks_GivesTheRangesOfOneWholeTextCall` pass. |
| PERF-01 | | Claude (2026-09-15) | auto PASS — human pending. load-state.test.mjs and parser-types.test.mjs (whole files, 0 fail in the suite) and marks "a keystroke keeps every ¶ node already drawn…" pass. |
| PERF-02 | | Claude (2026-09-15) | auto PASS — human pending. `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture` passes. |
| SWT-01 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame` pass. |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator` passes. |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "front matter opens as no edit and saves byte for byte…" and "… its words are code to spell check" pass. |
| FM-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: front-matter.md (242 B), front-matter-blank-lines.md (77 B), front-matter-bom-crlf.md (103 B, BOM and CRLF kept) and front-matter-at-eof.md (72 B) are all byte-identical and stable on pass 2. Step 2's edit (`draft: true` → `draft: false`) changes only that line. The front-matter "opens as no edit and saves byte for byte…" test passes. **Stale expectation:** the plan says front-matter-at-eof.md gains a final newline, and section 5 lists that as a limit. With source-keep, it doesn't. |
| FM-03 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "Enter and typing edit its text…" passes. |
| FM-04 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "… input rules and the toolbar cannot make it another block…" passes. |
| FM-05 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line…" passes. |
| TBL-01 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "…: byte-identical; edited, saved, reopened and saved again" (12 cases) pass. Fixture check: tables.md (1158 B) is byte-identical and stable on pass 2. **Stale expectation:** the plan expects four sections to differ (TBL-03, TBL-06, TBL-07). With source-keep, none of them do on a no-edit save. |
| TBL-02 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "aligned, a cell grows" and "Prettier, a cell grows past its width" pass. |
| TBL-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "a cell holding only a break loads as an empty cell" passes. Fixture check: a no-edit save keeps `\| <br /> \| text \|` as written. After a cell in that table is edited, the rows save as `\| \| text \|` and `\| textX \| \|`, and a reopen and save is stable. **Stale expectation:** the plan says the `<br />` cells save as `\| \|` on any save. With source-keep, that happens only once the table is edited. |
| TBL-04 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "a new table: aligned, columns at least 3 wide…" passes. |
| TBL-05 | PASS | Claude (2026-09-15) | table-fidelity "wide-cell repro: the save is the size of the file…" passes. Fixture check: large-600kb.md (generated, 619,574 B) is byte-identical after a no-edit save and stable on pass 2 (2.6 s in jsdom). |
| TBL-06 | PASS | Claude (2026-09-15) | Fixture check: a no-edit save leaves the table unchanged, and pass 2 is stable. **Stale expectation:** the plan expects FAIL, with the cells re-centred. With source-keep, that happens only after the table is edited. Appending X to a cell still gives `\| Name  \|   Note  \|`, so the section 5 candidate still applies to edited tables. |
| TBL-07 | PASS | Claude (2026-09-15) | Fixture check: a second save changes nothing. **Stale expectation:** the plan says a first save adds outer pipes to **No outer pipes** and tidies **CJK**. With source-keep, a no-edit save leaves both as written. After an edit, **No outer pipes** gains them (`\|a\|b\|` / `\|---\|---\|` / `\|1\|2X\|`) and is stable when reopened and saved. An edit in **CJK** changed only its row, and was also stable. |
| BR-01 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip InlineBreakSurvives "an inline break renders as a line break" passes. |
| BR-02 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "the line ending before an inline break is kept…" passes. Fixture check: br-forms.md (534 B) is byte-identical and stable on pass 2. With "Edited " typed at the first text, only that line differs. |
| BR-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line" passes. |
| LST-01 | FAIL | Claude (2026-09-15) | roundtrip ListItemLeadingBlockSurvives (suite) passes, but the fixture check doesn't. A no-edit save of list-leading-blocks.md adds a `\n` at the end of the file (530 → 531 B): after the closing task list, `- [x] a done task item\n` becomes `…item\n\n`. Nothing else changes, and pass 2 is stable at 531 B. In jsdom the plain serialiser gives 530 B. After `settleDocument` (run by `MDM.setMarkdown`), the document ends in an empty paragraph, and the save writes a blank line for it. Build 284's check didn't run `settleDocument`, so it would not have shown this. |
| LST-02 | PASS | Claude (2026-09-15) | roundtrip ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose…" pass. Fixture check: issue-11-list-paragraph.md (336 B) is byte-identical and stable on pass 2. |
| LST-03 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote" passes. |
| OPN-01 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` passes. |
| OPN-02 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way` passes. |
| OPN-03 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and file-drop "postWithFiles hands the host the dropped files…" pass. |
| OPN-04 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note` passes. |
| OPN-05 | | Claude (2026-09-15) | auto PASS — human pending. `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder` pass. |
| OPN-06 | | | |
| OPN-07 | | Claude (2026-09-15) | auto PASS — human pending. `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` pass. |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document` passes. |
| BIG-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `Turning_one_on_is_the_documents_own_choice_and_sticks…` pass. |
| BIG-03 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again` passes. |
| BIG-04 | PASS | Claude (2026-09-15) | `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note` passes. |
| BIG-05 | | | |
| BIG-06 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings` passes. |
| BIG-07 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices` passes. |
| BIG-08 | | | |
| SRC-01 | FAIL | Claude (2026-09-15); Paul (2026-09-15) | human PASS: Paul saved his own spec document with no edit, and `git diff` was empty with no `*` in the title. auto FAIL, which keeps the overall result at FAIL until the trailing-list fix lands. Each document was saved with no edit through the host path: all 13 committed fixtures, front-matter-bom-crlf.md, large-600kb.md and a CRLF copy of source-keep-roundtrip.md. 15 of the 16 are byte-identical and stable on pass 2, including source-keep-roundtrip.md (1225 B) and its CRLF copy (1281 B). The exception is list-leading-blocks.md, which gains a final `\n` (530 → 531 B; see LST-01). |
| SRC-02 | PASS | Claude (2026-09-15); Paul (2026-09-15) | human PASS: on Paul's own spec document, Ctrl+E with no edit showed the original text. auto PASS. Fixture check: with no edit, `MDM.getMarkdown` (what Ctrl+E shows) returns source-keep-roundtrip.md's text exactly, including `* star bullet one`, the setext underline and `~tilde~`. |
| SRC-03 | PASS | Claude (2026-09-15); Paul (2026-09-15) | human PASS: on a copy of Paul's own spec document, editing one paragraph changed only that paragraph in the diff, and it stayed that way across edit, save, edit, save. Ctrl+G and the margin line numbers were correct after saving. auto PASS. Fixture check: ` Edited.` typed at the end of the last paragraph. The save is the original with only that change (L56; 1225 → 1233 B), and a reopen and save is stable. |
| SRC-04 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `now` → `today` in "Bold around code". Only L10 differs, rewritten in the app's conventions: the bold is split around the code span. The lists, rules, reference definitions and HTML comment are identical, and a reopen and save is stable. |
| SRC-05 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `New paragraph.` inserted after "Rule written with dashes:", and the "Rule written with stars:" paragraph deleted. The save is exactly the original with that paragraph and its blank line added and that one removed (1225 → 1215 B). Blank lines between untouched blocks are kept, and a reopen and save is stable. |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |

### Build 314 (1.0.0-beta1+build.314) — 2026-09-15

Automated items only, run by Claude on 2026-09-15 in `C:\code\MarkdownMidget\.claude\worktrees\trailing-para` on branch `runlog-314`. The product code is master a11c312, which is build 314: build 304 plus the fix for list items that open with a block or with nothing.

- `dotnet test tests/MarkdownMidget.Tests`: 1533 passed, 0 failed (`dotnet-314.trx`). All 36 plan-named C# tests were found and passed.
- `npm test` (editor-src): 450 pass, 0 fail (`npm-314.txt`). All 45 plan-named jsdom titles were found and passed, and so did the named suites and the two whole files PERF-01 names.
- Fixture round-trip check: `roundtrip-314.mjs`, output in `roundtrip-314.txt`. It is build 304's script, with each edit step the plan now names added. Loading, saving, the BOM and line endings are handled as in build 304. All 13 committed fixtures, the generated `front-matter-bom-crlf.md` and `large-600kb.md`, and a CRLF copy of `source-keep-roundtrip.md` are byte-identical after a no-edit save and stable on pass 2. `list-leading-blocks.md` is now identical, so build 304's LST-01 and SRC-01 failures are gone. Every edit step gives the expected change and is stable when reopened and saved.
- Not re-run: the WEB **link replay**, for the same reason as builds 284 and 304.
- Stale expectations: none found. The expectations rewritten at 531bfba match this build.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | Claude (2026-09-15) | auto PASS — human pending. `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `TheColumnCountsCharactersTheWayAReaderDoes` and line-map "the caret's line in a nested list…" pass. |
| LIN-02 | | Claude (2026-09-15) | auto PASS — human pending. line-map "the margin numbers top-level blocks…" and "the margin redraws…" pass. |
| LIN-03 | | Claude (2026-09-15) | auto PASS — human pending. line-map range, gap-label, nested-list range and "every line … once and in order" tests pass. |
| LIN-04 | | Claude (2026-09-15) | auto PASS — human pending. line-map Go to Line tests (3 titles) and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse` pass. |
| LIN-05 | | Claude (2026-09-15) | auto PASS — human pending. line-map "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save…" pass. |
| LIN-06 | | Claude (2026-09-15) | auto PASS — human pending. line-map "a table or a code fence ending a file with no final newline…" passes. |
| VIEW-01 | | | |
| VIEW-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered…` and `A_file_opening_into_the_source_view_is_never_offered_it` pass. |
| ANC-01 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "HELP's Known limits link: a plain click edits, Ctrl+click jumps…" passes. |
| ANC-02 | | Claude (2026-09-15) | auto PASS — human pending. Same anchor-links test (read-only half) passes. |
| ANC-03 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "a repeated heading takes its numbered anchor…" and "an unmatched or malformed fragment does nothing…" pass. |
| ANC-04 | PASS | Claude (2026-09-15) | `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists`, `ARepeatedHeadingGetsGitHubsNumberedAnchors` and anchor-links "a slug is DocAnchorLinksTests' slug" pass. |
| LNK-01 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click on a link reports its mark href…" and `CopyLinkTests.CopyLinkWritesExactlyTheHref…` pass. Link replay not re-run (see WEB-06). |
| LNK-02 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click off a link or on a raw-HTML <a> reports none…" passes. |
| LNK-03 | | | |
| LNK-04 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… a linked picture reports its link …" passes. |
| LNK-05 | | | |
| LNK-06 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… with no target the caret decides" passes. |
| LNK-07 | | Claude (2026-09-15) | auto PASS — human pending. `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash` passes. |
| WEB-01 | | Claude (2026-09-15) | auto PASS — human pending. web-links "Ctrl+click posts openLink with the mark's href…" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole` pass. |
| WEB-02 | | Claude (2026-09-15) | auto PASS — human pending. web-links "… a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link" pass. |
| WEB-03 | | Claude (2026-09-15) | auto PASS — human pending. web-links "a relative, file:, javascript: or mailto: link is refused…" and `LinkOpeningTests.EverythingElseIsRefused` pass. |
| WEB-04 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.WebLinksAreAccepted` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-05 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-06 | | Claude (2026-09-15) | auto BLOCKED — human pending. This test's only automated part is the link replay, which runs the links.md addresses through `LinkOpening.TryValidate`. It needs a new .NET harness, which this run doesn't build. The plan last records it as confirmed on master a4577ae. |
| MENU-01 | | Claude (2026-09-15) | auto PASS — human pending. `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn` pass. The second is in the nested class `MenuAccessKeysTests+OnARealMenu`. |
| SPL-01 | | Claude (2026-09-15) | auto PASS — human pending. `SpellChunkTests.Chunks_BreakAtALineBreak_PreferringABlankLine`, `Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk` and `CheckInChunks_GivesTheRangesOfOneWholeTextCall` pass. |
| PERF-01 | | Claude (2026-09-15) | auto PASS — human pending. Every test in load-state.test.mjs and parser-types.test.mjs passes, and so does marks "a keystroke keeps every ¶ node already drawn…". |
| PERF-02 | | Claude (2026-09-15) | auto PASS — human pending. `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture` passes. |
| SWT-01 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame` pass. |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator` passes. |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "front matter opens as no edit and saves byte for byte…" and "… its words are code to spell check" pass. |
| FM-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: front-matter.md (242 B), front-matter-blank-lines.md (77 B), front-matter-bom-crlf.md (103 B, BOM and CRLF kept) and front-matter-at-eof.md (72 B) are byte-identical after a no-edit save and stable on pass 2. Step 2 (`draft: true` → `draft: false`) changes only that line. Step 3 (`, edited` at the end of the `title:` line) changes only that line and adds a final newline (72 → 81 B), and a reopen and save changes nothing. The front-matter "opens as no edit and saves byte for byte…" test passes. |
| FM-03 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "Enter and typing edit its text…" passes. |
| FM-04 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "… input rules and the toolbar cannot make it another block…" passes. |
| FM-05 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line…" passes. |
| TBL-01 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "…: byte-identical; edited, saved, reopened and saved again" (12 cases) pass. Fixture check: tables.md (1158 B) is byte-identical after a no-edit save, every table included, and stable on pass 2. |
| TBL-02 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "aligned, a cell grows" and "Prettier, a cell grows past its width" pass. Fixture check: step 1 changes only the `apple` row. Step 2 widens that column in every row of **Prettier style** only, delimiter row included. Both are stable when reopened and saved. |
| TBL-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "a cell holding only a break loads as an empty cell" passes. Fixture check: step 1 is byte-identical. Step 2 (`h2` → `h2X`) changes only that table's header row and its two `<br />` rows, to `\| h1 \| h2X \|`, `\| \| text \|` and `\| text \| \|`. A reopen and save changes nothing. |
| TBL-04 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "a new table: aligned, columns at least 3 wide…" passes. |
| TBL-05 | PASS | Claude (2026-09-15) | table-fidelity "wide-cell repro: the save is the size of the file…" passes. Fixture check: large-600kb.md (generated, 619,574 B) is byte-identical after a no-edit save and stable on pass 2 (3.5 s in jsdom). |
| TBL-06 | PASS | Claude (2026-09-15) | Fixture check: step 1 leaves the table identical. Step 2 (`red` → `pink`) re-centres the header and the edited row: `\| Note   \|` becomes `\|  Note  \|`, and the cell saves as `\|  pink  \|`. The `fig` row is identical, and a reopen and save changes nothing. |
| TBL-07 | PASS | Claude (2026-09-15) | Fixture check: step 1 leaves both tables identical. In step 2, **No outer pipes** gains outer pipes (`\|a\|b\|`, `\|---\|---\|`, `\|1\|3\|`), and the edited **CJK** row `\| 林檎 \| 3  \|` saves as `\| 林檎 \| 4 \|`. A reopen and save changes nothing. |
| BR-01 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip InlineBreakSurvives "an inline break renders as a line break" passes. |
| BR-02 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "the line ending before an inline break is kept…" passes. Fixture check: br-forms.md (534 B) is byte-identical and stable on pass 2. With "Edited " typed at the first text, only that line differs. |
| BR-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line" passes. |
| LST-01 | PASS | Claude (2026-09-15) | roundtrip ListItemLeadingBlockSurvives (suite) passes. Fixture check: list-leading-blocks.md (530 B) is byte-identical after a no-edit save and stable on pass 2, so `- > quoted text` and the task checkboxes are kept. Build 304's extra final `\n` is gone. For information: with `X` typed at the end of the last task item, only that line changes, and the file still ends with one newline. |
| LST-02 | PASS | Claude (2026-09-15) | roundtrip ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose…" pass. Fixture check: issue-11-list-paragraph.md (336 B) is byte-identical and stable on pass 2. |
| LST-03 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote" passes. |
| OPN-01 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` passes. |
| OPN-02 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way` passes. |
| OPN-03 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and file-drop "postWithFiles hands the host the dropped files…" pass. |
| OPN-04 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note` passes. |
| OPN-05 | | Claude (2026-09-15) | auto PASS — human pending. `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder` pass. |
| OPN-06 | | | |
| OPN-07 | | Claude (2026-09-15) | auto PASS — human pending. `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` pass. |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document` passes. |
| BIG-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `Turning_one_on_is_the_documents_own_choice_and_sticks…` pass. |
| BIG-03 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again` passes. |
| BIG-04 | PASS | Claude (2026-09-15) | `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note` passes. |
| BIG-05 | | | |
| BIG-06 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings` passes. |
| BIG-07 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices` passes. |
| BIG-08 | | | |
| SRC-01 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: all 13 committed fixtures, front-matter-bom-crlf.md, large-600kb.md and a CRLF copy of source-keep-roundtrip.md are byte-identical after a no-edit save and stable on pass 2. That includes source-keep-roundtrip.md (1225 B), its CRLF copy (1281 B), and list-leading-blocks.md (530 B), which failed on build 304. |
| SRC-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: with no edit, `MDM.getMarkdown` (what Ctrl+E shows) returns source-keep-roundtrip.md's text exactly, including `* star bullet one`, the setext underline and `~tilde~`. |
| SRC-03 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: ` Edited.` typed at the end of the last paragraph. The save is the original with only that line changed (L56; 1225 → 1233 B), and a reopen and save is stable. |
| SRC-04 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `now` → `today` in "Bold around code". Only L10 differs, rewritten in the app's conventions: the bold is split around the code span. The lists, rules, reference definitions and HTML comment are identical, and a reopen and save is stable. |
| SRC-05 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `New paragraph.` inserted after "Rule written with dashes:", and the "Rule written with stars:" paragraph deleted. The save is exactly the original with that paragraph and its blank line added and that one removed (1225 → 1215 B). Blank lines between untouched blocks are kept, and a reopen and save is stable. |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |

### Build 328 (1.0.0-rc1+build.328) — 2026-09-15

Automated items only, run by Claude on 2026-09-15 in `C:\code\MarkdownMidget\.claude\worktrees\release-1.0.0` on branch `runlog-328`. Build 328 is master e050543, published as 1.0.0-rc1+build.328. Its product code is the same as build 314 (a11c312). Since then only docs have changed (this run log, CHANGELOG `[1.0.0-rc1]`, HELP, README and ROADMAP), plus the project version, which is now `1.0.0-rc1`.

- `dotnet test tests/MarkdownMidget.Tests`: 1533 passed, 0 failed (`b328-pre.trx`). All 36 plan-named C# tests were found and passed. The version checks pass with `1.0.0-rc1`: `EmbeddedReaderDocsTests.TheChangelogHasAnEntryForThisBuild`, `TheVersionThisProjectCarriesHasTheChangelogSectionTheReleaseWorkflowCuts`, `TheChangelogsNewestEntryComesFirst` and `BuildNumberVersionTests.ThisVeryBuildCarriesItsNumberInBothVersions`. No test in section 3 names them. The local test build is numbered 329.
- `npm test` (editor-src): 450 pass, 0 fail, 0 skipped, 0 todo. The full log wasn't kept, so each of the 45 plan-named jsdom titles was found in `editor-src/test` (`map-328.mjs`). Since nothing failed or was skipped, each one ran and passed. The named suites and the two whole files PERF-01 names were found too. TBL-02's two titles are cases of the table-fidelity "…: byte-identical; edited, saved, reopened and saved again" loop.
- Fixture round-trip check: `roundtrip-328.mjs`, output in `roundtrip-328.txt`. It is build 314's script, pointed at this worktree. Loading, saving, the BOM and line endings are handled as the host does. `make-large.mjs` regenerated the large documents and `front-matter-bom-crlf.md`, which are byte-identical to build 314's. With timings removed, the output is identical to build 314's line for line. All 13 committed fixtures, the generated `front-matter-bom-crlf.md` and `large-600kb.md`, and a CRLF copy of `source-keep-roundtrip.md` are byte-identical after a no-edit save and stable on pass 2. Every edit step gives the same change as in build 314 and is stable when reopened and saved.
- Not re-run: the WEB **link replay**, for the same reason as builds 284, 304 and 314.
- Stale expectations: none found.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | Claude (2026-09-15) | auto PASS — human pending. `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `TheColumnCountsCharactersTheWayAReaderDoes` and line-map "the caret's line in a nested list…" pass. |
| LIN-02 | | Claude (2026-09-15) | auto PASS — human pending. line-map "the margin numbers top-level blocks…" and "the margin redraws…" pass. |
| LIN-03 | | Claude (2026-09-15) | auto PASS — human pending. line-map range, gap-label, nested-list range and "every line … once and in order" tests pass. |
| LIN-04 | | Claude (2026-09-15) | auto PASS — human pending. line-map Go to Line tests (3 titles) and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse` pass. |
| LIN-05 | | Claude (2026-09-15) | auto PASS — human pending. line-map "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save…" pass. |
| LIN-06 | | Claude (2026-09-15) | auto PASS — human pending. line-map "a table or a code fence ending a file with no final newline…" passes. |
| VIEW-01 | | | |
| VIEW-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered…` and `A_file_opening_into_the_source_view_is_never_offered_it` pass. |
| ANC-01 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "HELP's Known limits link: a plain click edits, Ctrl+click jumps…" passes. |
| ANC-02 | | Claude (2026-09-15) | auto PASS — human pending. Same anchor-links test (read-only half) passes. |
| ANC-03 | | Claude (2026-09-15) | auto PASS — human pending. anchor-links "a repeated heading takes its numbered anchor…" and "an unmatched or malformed fragment does nothing…" pass. |
| ANC-04 | PASS | Claude (2026-09-15) | `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists`, `ARepeatedHeadingGetsGitHubsNumberedAnchors` and anchor-links "a slug is DocAnchorLinksTests' slug" pass. |
| LNK-01 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click on a link reports its mark href…" and `CopyLinkTests.CopyLinkWritesExactlyTheHref…` pass. Link replay not re-run (see WEB-06). |
| LNK-02 | | Claude (2026-09-15) | auto PASS — human pending. link-at "a right-click off a link or on a raw-HTML <a> reports none…" passes. |
| LNK-03 | | | |
| LNK-04 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… a linked picture reports its link …" passes. |
| LNK-05 | | | |
| LNK-06 | | Claude (2026-09-15) | auto PASS — human pending. link-at "… with no target the caret decides" passes. |
| LNK-07 | | Claude (2026-09-15) | auto PASS — human pending. `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash` passes. |
| WEB-01 | | Claude (2026-09-15) | auto PASS — human pending. web-links "Ctrl+click posts openLink with the mark's href…" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole` pass. |
| WEB-02 | | Claude (2026-09-15) | auto PASS — human pending. web-links "… a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link" pass. |
| WEB-03 | | Claude (2026-09-15) | auto PASS — human pending. web-links "a relative, file:, javascript: or mailto: link is refused…" and `LinkOpeningTests.EverythingElseIsRefused` pass. |
| WEB-04 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.WebLinksAreAccepted` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-05 | | Claude (2026-09-15) | auto PASS — human pending. `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-06 | | Claude (2026-09-15) | auto BLOCKED — human pending. This test's only automated part is the link replay, which runs the links.md addresses through `LinkOpening.TryValidate`. It needs a new .NET harness, which this run doesn't build. The plan last records it as confirmed on master a4577ae. |
| MENU-01 | | Claude (2026-09-15) | auto PASS — human pending. `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn` pass. The second is in the nested class `MenuAccessKeysTests+OnARealMenu`. |
| SPL-01 | | Claude (2026-09-15) | auto PASS — human pending. `SpellChunkTests.Chunks_BreakAtALineBreak_PreferringABlankLine`, `Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk` and `CheckInChunks_GivesTheRangesOfOneWholeTextCall` pass. |
| PERF-01 | | Claude (2026-09-15) | auto PASS — human pending. Every test in load-state.test.mjs and parser-types.test.mjs passes, and so does marks "a keystroke keeps every ¶ node already drawn…". |
| PERF-02 | | Claude (2026-09-15) | auto PASS — human pending. `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture` passes. |
| SWT-01 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame` pass. |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | Claude (2026-09-15) | auto PASS — human pending. `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator` passes. |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "front matter opens as no edit and saves byte for byte…" and "… its words are code to spell check" pass. |
| FM-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: front-matter.md (242 B), front-matter-blank-lines.md (77 B), front-matter-bom-crlf.md (103 B, BOM and CRLF kept) and front-matter-at-eof.md (72 B) are byte-identical after a no-edit save and stable on pass 2. Step 2 (`draft: true` → `draft: false`) changes only that line. Step 3 (`, edited` at the end of the `title:` line) changes only that line and adds a final newline (72 → 81 B), and a reopen and save changes nothing. The front-matter "opens as no edit and saves byte for byte…" test passes. |
| FM-03 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "Enter and typing edit its text…" passes. |
| FM-04 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "… input rules and the toolbar cannot make it another block…" passes. |
| FM-05 | | Claude (2026-09-15) | auto PASS — human pending. front-matter "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line…" passes. |
| TBL-01 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "…: byte-identical; edited, saved, reopened and saved again" (12 cases) pass. Fixture check: tables.md (1158 B) is byte-identical after a no-edit save, every table included, and stable on pass 2. |
| TBL-02 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "aligned, a cell grows" and "Prettier, a cell grows past its width" pass. Fixture check: step 1 changes only the `apple` row. Step 2 widens that column in every row of **Prettier style** only, delimiter row included. Both are stable when reopened and saved. |
| TBL-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "a cell holding only a break loads as an empty cell" passes. Fixture check: step 1 is byte-identical. Step 2 (`h2` → `h2X`) changes only that table's header row and its two `<br />` rows, to `\| h1 \| h2X \|`, `\| \| text \|` and `\| text \| \|`. A reopen and save changes nothing. |
| TBL-04 | | Claude (2026-09-15) | auto PASS — human pending. table-fidelity "a new table: aligned, columns at least 3 wide…" passes. |
| TBL-05 | PASS | Claude (2026-09-15) | table-fidelity "wide-cell repro: the save is the size of the file…" passes. Fixture check: large-600kb.md (generated, 619,574 B) is byte-identical after a no-edit save and stable on pass 2. |
| TBL-06 | PASS | Claude (2026-09-15) | Fixture check: step 1 leaves the table identical. Step 2 (`red` → `pink`) re-centres the header and the edited row: `\| Note   \|` becomes `\|  Note  \|`, and the cell saves as `\|  pink  \|`. The `fig` row is identical, and a reopen and save changes nothing. |
| TBL-07 | PASS | Claude (2026-09-15) | Fixture check: step 1 leaves both tables identical. In step 2, **No outer pipes** gains outer pipes (`\|a\|b\|`, `\|---\|---\|`, `\|1\|3\|`), and the edited **CJK** row `\| 林檎 \| 3  \|` saves as `\| 林檎 \| 4 \|`. A reopen and save changes nothing. |
| BR-01 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip InlineBreakSurvives "an inline break renders as a line break" passes. |
| BR-02 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "the line ending before an inline break is kept…" passes. Fixture check: br-forms.md (534 B) is byte-identical and stable on pass 2. With "Edited " typed at the first text, only that line differs. |
| BR-03 | PASS | Claude (2026-09-15) | roundtrip InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line" passes. |
| LST-01 | PASS | Claude (2026-09-15) | roundtrip ListItemLeadingBlockSurvives (suite) passes. Fixture check: list-leading-blocks.md (530 B) is byte-identical after a no-edit save and stable on pass 2, so `- > quoted text` and the task checkboxes are kept. For information: with `X` typed at the end of the last task item, only that line changes, and the file still ends with one newline. |
| LST-02 | PASS | Claude (2026-09-15) | roundtrip ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose…" pass. Fixture check: issue-11-list-paragraph.md (336 B) is byte-identical and stable on pass 2. |
| LST-03 | | Claude (2026-09-15) | auto PASS — human pending. roundtrip ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote" passes. |
| OPN-01 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` passes. |
| OPN-02 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way` passes. |
| OPN-03 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and file-drop "postWithFiles hands the host the dropped files…" pass. |
| OPN-04 | | Claude (2026-09-15) | auto PASS — human pending. `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note` passes. |
| OPN-05 | | Claude (2026-09-15) | auto PASS — human pending. `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder` pass. |
| OPN-06 | | | |
| OPN-07 | | Claude (2026-09-15) | auto PASS — human pending. `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` pass. |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document` passes. |
| BIG-02 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `Turning_one_on_is_the_documents_own_choice_and_sticks…` pass. |
| BIG-03 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again` passes. |
| BIG-04 | PASS | Claude (2026-09-15) | `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note` passes. |
| BIG-05 | | | |
| BIG-06 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings` passes. |
| BIG-07 | | Claude (2026-09-15) | auto PASS — human pending. `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices` passes. |
| BIG-08 | | | |
| SRC-01 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: all 13 committed fixtures, front-matter-bom-crlf.md, large-600kb.md and a CRLF copy of source-keep-roundtrip.md are byte-identical after a no-edit save and stable on pass 2. That includes source-keep-roundtrip.md (1225 B), its CRLF copy (1281 B) and list-leading-blocks.md (530 B). |
| SRC-02 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: with no edit, `MDM.getMarkdown` (what Ctrl+E shows) returns source-keep-roundtrip.md's text exactly, including `* star bullet one`, the setext underline and `~tilde~`. |
| SRC-03 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: ` Edited.` typed at the end of the last paragraph. The save is the original with only that line changed (L56; 1225 → 1233 B), and a reopen and save is stable. |
| SRC-04 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `now` → `today` in "Bold around code". Only L10 differs, rewritten in the app's conventions: the bold is split around the code span. The lists, rules, reference definitions and HTML comment are identical, and a reopen and save is stable. |
| SRC-05 | | Claude (2026-09-15) | auto PASS — human pending. Fixture check: `New paragraph.` inserted after "Rule written with dashes:", and the "Rule written with stars:" paragraph deleted. The save is exactly the original with that paragraph and its blank line added and that one removed (1225 → 1215 B). Blank lines between untouched blocks are kept, and a reopen and save is stable. |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |

### Build 350 (1.0.0-rc1+build.350) — 2026-09-15

Human results only, from Paul on 2026-09-15. No automated run on this interim RC2 build; the full automated run comes on the rc2 candidate build.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | | |
| LIN-02 | | | |
| LIN-03 | | | |
| LIN-04 | | | |
| LIN-05 | | | |
| LIN-06 | | | |
| VIEW-01 | | | |
| VIEW-02 | | | |
| ANC-01 | | | |
| ANC-02 | | | |
| ANC-03 | | | |
| ANC-04 | | | |
| LNK-01 | | | |
| LNK-02 | | | |
| LNK-03 | | | |
| LNK-04 | | | |
| LNK-05 | | | |
| LNK-06 | | | |
| LNK-07 | | | |
| WEB-01 | | | |
| WEB-02 | | | |
| WEB-03 | | | |
| WEB-04 | | | |
| WEB-05 | | | |
| WEB-06 | | | |
| MENU-01 | | | |
| SPL-01 | | | |
| PERF-01 | | | |
| PERF-02 | | | |
| SWT-01 | | | |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | | |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | | |
| FM-02 | | | |
| FM-03 | | | |
| FM-04 | | | |
| FM-05 | | | |
| TBL-01 | | | |
| TBL-02 | | | |
| TBL-03 | | | |
| TBL-04 | | | |
| TBL-05 | | | |
| TBL-06 | | | |
| TBL-07 | | | |
| BR-01 | | | |
| BR-02 | | | |
| BR-03 | | | |
| LST-01 | | | |
| LST-02 | | | |
| LST-03 | | | |
| OPN-01 | | | |
| OPN-02 | | | |
| OPN-03 | | | |
| OPN-04 | | | |
| OPN-05 | | | |
| OPN-06 | | | |
| OPN-07 | | | |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | | |
| BIG-02 | | | |
| BIG-03 | | | |
| BIG-04 | | | |
| BIG-05 | | | |
| BIG-06 | | | |
| BIG-07 | | | |
| BIG-08 | | | |
| SRC-01 | | | |
| SRC-02 | | | |
| SRC-03 | | | |
| SRC-04 | | | |
| SRC-05 | | | |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |
| FND-01 | | | |
| FND-02 | | | |
| FND-03 | | Paul (2026-09-15) | Step 4 PASS: with no document open, Ctrl+F, Ctrl+H and Edit ▸ Replace… each grey Replace and Replace All with "No document is open." and Enter runs Find Next. With the dialog open, opening a document and turning read-only off enables them. Ctrl+W greys them again. Steps 1–3 not reported. |
| VIEW-03 | | | |
| TIP-01 | | | |

### Build 442 (1.0.0-rc1+build.442) — 2026-09-16

Human results only, from Paul on 2026-09-16. There was no automated run on this interim RC2 build; the full automated run is on the rc2 build.

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | | |
| LIN-02 | | | |
| LIN-03 | | | |
| LIN-04 | | | |
| LIN-05 | | | |
| LIN-06 | | | |
| VIEW-01 | | | |
| VIEW-02 | | | |
| ANC-01 | | | |
| ANC-02 | | | |
| ANC-03 | | | |
| ANC-04 | | | |
| LNK-01 | | | |
| LNK-02 | | | |
| LNK-03 | | | |
| LNK-04 | | | |
| LNK-05 | | | |
| LNK-06 | | | |
| LNK-07 | | | |
| WEB-01 | | | |
| WEB-02 | | | |
| WEB-03 | | | |
| WEB-04 | | | |
| WEB-05 | | | |
| WEB-06 | | | |
| MENU-01 | | | |
| SPL-01 | | | |
| PERF-01 | | | |
| PERF-02 | | | |
| SWT-01 | | | |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | | |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | | |
| FM-02 | | | |
| FM-03 | | | |
| FM-04 | | | |
| FM-05 | | | |
| TBL-01 | | | |
| TBL-02 | | | |
| TBL-03 | | | |
| TBL-04 | | | |
| TBL-05 | | | |
| TBL-06 | | | |
| TBL-07 | | | |
| BR-01 | | | |
| BR-02 | | | |
| BR-03 | | | |
| LST-01 | | | |
| LST-02 | | | |
| LST-03 | | | |
| OPN-01 | | | |
| OPN-02 | | | |
| OPN-03 | | | |
| OPN-04 | | | |
| OPN-05 | | | |
| OPN-06 | | | |
| OPN-07 | | | |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | | |
| BIG-02 | | | |
| BIG-03 | | | |
| BIG-04 | | | |
| BIG-05 | | | |
| BIG-06 | | | |
| BIG-07 | | | |
| BIG-08 | | | |
| SRC-01 | | | |
| SRC-02 | | | |
| SRC-03 | | | |
| SRC-04 | | | |
| SRC-05 | | | |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |
| FND-01 | | | |
| FND-02 | | | |
| FND-03 | | | |
| FND-05 | | | |
| VIEW-03 | | | |
| TIP-01 | | | |
| INST-01 | | | |
| INST-02 | | | |
| INST-03 | PASS | Paul (2026-09-16) | Paul reports the INST-03 checks passed on build 442. |

### Build 450 (1.0.0-rc2+build.450) — 2026-09-16

Automated items only, run by Claude on 2026-09-16 in `C:\code\MarkdownMidget\.claude\worktrees\release-rc2` on branch `runlog-450`. Build 450 is master c2d1c35, published as 1.0.0-rc2+build.450, and is the candidate for the `v1.0.0-rc2` tag. It is build 328 plus the RC2 changes: Ctrl+H and Edit ▸ Replace…, the grouped view pair, tooltips on greyed-out controls, Find and Replace starting from the selection (with the single-line Replace All scope), Register keeping file associations, and the default-app button and post-update notice. The project version is now `1.0.0-rc2`.

- `dotnet test tests/MarkdownMidget.Tests`: 1583 passed, 0 failed (`b450-pre.trx`). All 42 plan-named C# tests were found and passed, including the 6 new ones that VIEW-03, FND-01, FND-03 and FND-05 name. The version checks pass with `1.0.0-rc2`: `EmbeddedReaderDocsTests.TheChangelogHasAnEntryForThisBuild`, `TheVersionThisProjectCarriesHasTheChangelogSectionTheReleaseWorkflowCuts`, `TheChangelogsNewestEntryComesFirst` and `BuildNumberVersionTests.ThisVeryBuildCarriesItsNumberInBothVersions`. No test in section 3 names them. The local test build is numbered 451.
- `npm test` (editor-src): 453 pass, 0 fail, 0 cancelled, 0 skipped, 0 todo. This time the full log was kept (`npm-450.txt`), and each of the 47 plan-named jsdom titles is in it as passed, from the file the plan names (`map-450.mjs`). So are the three roundtrip suites named whole (ListItemLeadingBlockSurvives, InlineBreakSurvives and ParagraphAfterNestedListKeepsItsBlankLine) and every test in the two files PERF-01 names. TBL-02's two titles are cases of the table-fidelity "…: byte-identical; edited, saved, reopened and saved again" loop.
- Fixture round-trip check: `roundtrip-450.mjs`, output in `roundtrip-450.txt`. It is build 328's script, pointed at this worktree. Loading, saving, the BOM and line endings are handled as the host does. Since build 328 the editor source has changed only in Find (`find.js`, and `MDM.findSelectionText` in `main.js`), and the fixtures haven't changed. `make-large.mjs` regenerated the large documents and `front-matter-bom-crlf.md`, which are byte-identical to build 328's. All 13 committed fixtures, the generated `front-matter-bom-crlf.md` and `large-600kb.md`, and a CRLF copy of `source-keep-roundtrip.md` are byte-identical after a no-edit save and stable on pass 2, and each save is byte-identical to build 328's. Every edit step gives the same change as in build 328 and is stable when reopened and saved.
- Not re-run: the WEB **link replay**, for the same reason as builds 284, 304, 314 and 328.
- Stale expectations: none found. FND-05 names a jsdom title with a straight apostrophe where the test has a typographic one (see FND-05).

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | Claude (2026-09-16) | auto PASS — human pending. `LineColumnTests.TheStatusTextNamesTheLineAndColumn`, `TheColumnCountsCharactersTheWayAReaderDoes` and line-map "the caret's line in a nested list…" pass. |
| LIN-02 | | Claude (2026-09-16) | auto PASS — human pending. line-map "the margin numbers top-level blocks…" and "the margin redraws…" pass. |
| LIN-03 | | Claude (2026-09-16) | auto PASS — human pending. line-map range, gap-label, nested-list range and "every line … once and in order" tests pass. |
| LIN-04 | | Claude (2026-09-16) | auto PASS — human pending. line-map Go to Line tests (3 titles) and `LineColumnTests.GoToLineClampsANumberAndRefusesAnythingElse` pass. |
| LIN-05 | | Claude (2026-09-16) | auto PASS — human pending. line-map "an untouched document is numbered by the text it was loaded from" and "after an edit, and after a save…" pass. |
| LIN-06 | | Claude (2026-09-16) | auto PASS — human pending. line-map "a table or a code fence ending a file with no final newline…" passes. |
| VIEW-01 | | | |
| VIEW-02 | | Claude (2026-09-16) | auto PASS — human pending. `LargeFileTests.The_threshold_is_250_KB`, `A_file_opening_formatted_is_offered…` and `A_file_opening_into_the_source_view_is_never_offered_it` pass. |
| ANC-01 | | Claude (2026-09-16) | auto PASS — human pending. anchor-links "HELP's Known limits link: a plain click edits, Ctrl+click jumps…" passes. |
| ANC-02 | | Claude (2026-09-16) | auto PASS — human pending. Same anchor-links test (read-only half) passes. |
| ANC-03 | | Claude (2026-09-16) | auto PASS — human pending. anchor-links "a repeated heading takes its numbered anchor…" and "an unmatched or malformed fragment does nothing…" pass. |
| ANC-04 | PASS | Claude (2026-09-16) | `DocAnchorLinksTests.EveryAnchorLinkLandsOnAHeadingThatExists`, `ARepeatedHeadingGetsGitHubsNumberedAnchors` and anchor-links "a slug is DocAnchorLinksTests' slug" pass. |
| LNK-01 | | Claude (2026-09-16) | auto PASS — human pending. link-at "a right-click on a link reports its mark href…" and `CopyLinkTests.CopyLinkWritesExactlyTheHref…` pass. Link replay not re-run (see WEB-06). |
| LNK-02 | | Claude (2026-09-16) | auto PASS — human pending. link-at "a right-click off a link or on a raw-HTML <a> reports none…" passes. |
| LNK-03 | | | |
| LNK-04 | | Claude (2026-09-16) | auto PASS — human pending. link-at "… a linked picture reports its link …" passes. |
| LNK-05 | | | |
| LNK-06 | | Claude (2026-09-16) | auto PASS — human pending. link-at "… with no target the caret decides" passes. |
| LNK-07 | | Claude (2026-09-16) | auto PASS — human pending. `CopyLinkTests.CopyLinkWritesExactlyTheHrefAndAClipboardAnotherProgramHoldsIsANoteNotACrash` passes. |
| WEB-01 | | Claude (2026-09-16) | auto PASS — human pending. web-links "Ctrl+click posts openLink with the mark's href…" and `LinkOpeningTests.AnOverlongUrlIsRefusedAndALongOneIsShownWhole` pass. |
| WEB-02 | | Claude (2026-09-16) | auto PASS — human pending. web-links "… a plain click in the editable view posts nothing" and "… in a read-only view a plain click posts the web link" pass. |
| WEB-03 | | Claude (2026-09-16) | auto PASS — human pending. web-links "a relative, file:, javascript: or mailto: link is refused…" and `LinkOpeningTests.EverythingElseIsRefused` pass. |
| WEB-04 | | Claude (2026-09-16) | auto PASS — human pending. `LinkOpeningTests.WebLinksAreAccepted` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-05 | | Claude (2026-09-16) | auto PASS — human pending. `LinkOpeningTests.EverythingElseIsRefused`, `PaddingCannotHideAHostOrAnAttachment` and `HostsThatCannotShowInPunycodeAreRefused` pass. Link replay not re-run (see WEB-06). |
| WEB-06 | | Claude (2026-09-16) | auto BLOCKED — human pending. This test's only automated part is the link replay, which runs the links.md addresses through `LinkOpening.TryValidate`. It needs a new .NET harness, which this run doesn't build. The plan last records it as confirmed on master a4577ae. |
| MENU-01 | | Claude (2026-09-16) | auto PASS — human pending. `MenuAccessKeysTests.OnlyAMenusOwnOpeningIsItsOwn` and `ANestedSubmenuOpeningReachesTheParentButIsNotItsOwn` pass. The second is in the nested class `MenuAccessKeysTests+OnARealMenu`. |
| SPL-01 | | Claude (2026-09-16) | auto PASS — human pending. `SpellChunkTests.Chunks_BreakAtALineBreak_PreferringABlankLine`, `Chunks_KeepALongLineWhole_UseAbout16KB_AndLeaveNoEmptyChunk` and `CheckInChunks_GivesTheRangesOfOneWholeTextCall` pass. |
| PERF-01 | | Claude (2026-09-16) | auto PASS — human pending. Every test in load-state.test.mjs and parser-types.test.mjs passes, and so does marks "a keystroke keeps every ¶ node already drawn…". |
| PERF-02 | | Claude (2026-09-16) | auto PASS — human pending. `TimingLogTests.A_line_is_the_time_of_day_sequence_phase_and_whole_milliseconds_in_any_culture` passes. |
| SWT-01 | | Claude (2026-09-16) | auto PASS — human pending. `SwitchBusyTests.TheLightboxShowsOnlyOverAStillInstallingSourceViewNoOpenHasCovered` and `EditorScriptsTests.ASwitchsPaintWaitAsksForItsOwnLoadsPaintedAfterAFrame` pass. |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | Claude (2026-09-16) | auto PASS — human pending. `SwitchBusyTests.OnlyTheSwitchUnderWayOwnsTheIndicator` passes. |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | Claude (2026-09-16) | auto PASS — human pending. front-matter "front matter opens as no edit and saves byte for byte…" and "… its words are code to spell check" pass. |
| FM-02 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: front-matter.md (242 B), front-matter-blank-lines.md (77 B), front-matter-bom-crlf.md (103 B, BOM and CRLF kept) and front-matter-at-eof.md (72 B) are byte-identical after a no-edit save and stable on pass 2. Step 2 (`draft: true` → `draft: false`) changes only that line. Step 3 (`, edited` at the end of the `title:` line) changes only that line and adds a final newline (72 → 81 B), and a reopen and save changes nothing. The front-matter "opens as no edit and saves byte for byte…" test passes. |
| FM-03 | | Claude (2026-09-16) | auto PASS — human pending. front-matter "Enter and typing edit its text…" passes. |
| FM-04 | | Claude (2026-09-16) | auto PASS — human pending. front-matter "… input rules and the toolbar cannot make it another block…" passes. |
| FM-05 | | Claude (2026-09-16) | auto PASS — human pending. front-matter "its lines, and the lines of the blocks after it, are numbered and reached by Go to Line…" passes. |
| TBL-01 | | Claude (2026-09-16) | auto PASS — human pending. table-fidelity "…: byte-identical; edited, saved, reopened and saved again" (12 cases) pass. Fixture check: tables.md (1158 B) is byte-identical after a no-edit save, every table included, and stable on pass 2. |
| TBL-02 | | Claude (2026-09-16) | auto PASS — human pending. table-fidelity "aligned, a cell grows" and "Prettier, a cell grows past its width" pass. Fixture check: step 1 changes only the `apple` row. Step 2 widens that column in every row of **Prettier style** only, delimiter row included. Both are stable when reopened and saved. |
| TBL-03 | PASS | Claude (2026-09-16) | roundtrip InlineBreakSurvives "a cell holding only a break loads as an empty cell" passes. Fixture check: step 1 is byte-identical. Step 2 (`h2` → `h2X`) changes only that table's header row and its two `<br />` rows, to `\| h1 \| h2X \|`, `\| \| text \|` and `\| text \| \|`. A reopen and save changes nothing. |
| TBL-04 | | Claude (2026-09-16) | auto PASS — human pending. table-fidelity "a new table: aligned, columns at least 3 wide…" passes. |
| TBL-05 | PASS | Claude (2026-09-16) | table-fidelity "wide-cell repro: the save is the size of the file…" passes. Fixture check: large-600kb.md (generated, 619,574 B) is byte-identical after a no-edit save and stable on pass 2. |
| TBL-06 | PASS | Claude (2026-09-16) | Fixture check: step 1 leaves the table identical. Step 2 (`red` → `pink`) re-centres the header and the edited row: `\| Note   \|` becomes `\|  Note  \|`, and the cell saves as `\|  pink  \|`. The `fig` row is identical, and a reopen and save changes nothing. |
| TBL-07 | PASS | Claude (2026-09-16) | Fixture check: step 1 leaves both tables identical. In step 2, **No outer pipes** gains outer pipes (`\|a\|b\|`, `\|---\|---\|`, `\|1\|3\|`), and the edited **CJK** row `\| 林檎 \| 3  \|` saves as `\| 林檎 \| 4 \|`. A reopen and save changes nothing. |
| BR-01 | | Claude (2026-09-16) | auto PASS — human pending. roundtrip InlineBreakSurvives "an inline break renders as a line break" passes. |
| BR-02 | PASS | Claude (2026-09-16) | roundtrip InlineBreakSurvives "the line ending before an inline break is kept…" passes. Fixture check: br-forms.md (534 B) is byte-identical and stable on pass 2. With "Edited " typed at the first text, only that line differs. |
| BR-03 | PASS | Claude (2026-09-16) | roundtrip InlineBreakSurvives "guard: a whole-paragraph <br /> is still an empty line" passes. |
| LST-01 | PASS | Claude (2026-09-16) | roundtrip ListItemLeadingBlockSurvives (suite) passes. Fixture check: list-leading-blocks.md (530 B) is byte-identical after a no-edit save and stable on pass 2, so `- > quoted text` and the task checkboxes are kept. For information: with `X` typed at the end of the last task item, only that line changes, and the file still ends with one newline. |
| LST-02 | PASS | Claude (2026-09-16) | roundtrip ParagraphAfterNestedListKeepsItsBlankLine "inside a quote: the issue…", "outside a quote" and "inside a quote: ordered, deeper, loose…" pass. Fixture check: issue-11-list-paragraph.md (336 B) is byte-identical and stable on pass 2. |
| LST-03 | | Claude (2026-09-16) | auto PASS — human pending. roundtrip ParagraphAfterNestedListKeepsItsBlankLine "made in the editor outside a quote" passes. |
| OPN-01 | | Claude (2026-09-16) | auto PASS — human pending. `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` passes. |
| OPN-02 | | Claude (2026-09-16) | auto PASS — human pending. `OpenRoutingTests.A_window_has_no_document_only_when_nothing_is_in_it_or_on_its_way` passes. |
| OPN-03 | | Claude (2026-09-16) | auto PASS — human pending. `OpenRoutingTests.Only_dropped_documents_open_each_as_the_path_the_drop_carried_under_its_own_name` and file-drop "postWithFiles hands the host the dropped files…" pass. |
| OPN-04 | | Claude (2026-09-16) | auto PASS — human pending. `OpenRoutingTests.A_drop_starts_at_most_ten_instances_names_the_rest_and_a_failed_start_is_a_status_note` passes. |
| OPN-05 | | Claude (2026-09-16) | auto PASS — human pending. `DropRoutingTests.OtherAndUnreadableFilesAreRefused` and `DropRoutingTests.PicturesInsertInDropOrder` pass. |
| OPN-06 | | | |
| OPN-07 | | Claude (2026-09-16) | auto PASS — human pending. `OpenGuardTests.SecondAcquireSeesTheHolder` and `OpenRoutingTests.The_first_file_opens_here_only_when_the_window_has_no_document` pass. |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | Claude (2026-09-16) | auto PASS — human pending. `LargeDocumentTests.Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document` passes. |
| BIG-02 | | Claude (2026-09-16) | auto PASS — human pending. `LargeDocumentTests.Exactly_512_KB_follows_the_saved_settings` and `Turning_one_on_is_the_documents_own_choice_and_sticks…` pass. |
| BIG-03 | | Claude (2026-09-16) | auto PASS — human pending. `LargeDocumentTests.Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again` passes. |
| BIG-04 | PASS | Claude (2026-09-16) | `LargeDocumentTests.A_load_or_switch_over_5_seconds_turns_both_off_with_the_note` passes. |
| BIG-05 | | | |
| BIG-06 | | Claude (2026-09-16) | auto PASS — human pending. `LargeDocumentTests.Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings` passes. |
| BIG-07 | | Claude (2026-09-16) | auto PASS — human pending. `LargeDocumentTests.Keeping_the_document_under_a_new_name_keeps_its_choices` passes. |
| BIG-08 | | | |
| SRC-01 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: all 13 committed fixtures, front-matter-bom-crlf.md, large-600kb.md and a CRLF copy of source-keep-roundtrip.md are byte-identical after a no-edit save and stable on pass 2. That includes source-keep-roundtrip.md (1225 B), its CRLF copy (1281 B) and list-leading-blocks.md (530 B). |
| SRC-02 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: with no edit, `MDM.getMarkdown` (what Ctrl+E shows) returns source-keep-roundtrip.md's text exactly, including `* star bullet one`, the setext underline and `~tilde~`. |
| SRC-03 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: ` Edited.` typed at the end of the last paragraph. The save is the original with only that line changed (L56; 1225 → 1233 B), and a reopen and save is stable. |
| SRC-04 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: `now` → `today` in "Bold around code". Only L10 differs, rewritten in the app's conventions: the bold is split around the code span. The lists, rules, reference definitions and HTML comment are identical, and a reopen and save is stable. |
| SRC-05 | | Claude (2026-09-16) | auto PASS — human pending. Fixture check: `New paragraph.` inserted after "Rule written with dashes:", and the "Rule written with stars:" paragraph deleted. The save is exactly the original with that paragraph and its blank line added and that one removed (1225 → 1215 B). Blank lines between untouched blocks are kept, and a reopen and save is stable. |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |
| FND-01 | | Claude (2026-09-16) | auto PASS — human pending. `FindDialogFocusTests.CtrlHFocusesReplaceWithOnlyWhenFindWhatHasTextAndReplaceCanRun` passes. |
| FND-02 | | | |
| FND-03 | | Claude (2026-09-16) | auto PASS — human pending. `FindDialogFocusTests.CtrlHFocusesReplaceWithOnlyWhenFindWhatHasTextAndReplaceCanRun` and `ReplaceIsGreyedWhenReadOnlyOrNoDocument` pass. |
| FND-05 | | Claude (2026-09-16) | auto PASS — human pending. `FindEngineTests.TheSelectionFillsFindWhatOnlyWhenItIsOneLineOfAtMostTheLimit`, `TheSeededQueryFindsTheSelectedTextItselfInEveryMode` and `OnlyASelectionOverALineBreakLimitsReplaceAll` pass, and so do find "is the text the page shows over marks, links and code, and nothing for Find’s own current match" and "only matches wholly inside a selection over a line break are replaced …". The first title is written with a typographic apostrophe (Find’s) in find.test.mjs, and with a straight one in this plan. |
| VIEW-03 | | Claude (2026-09-16) | auto PASS — human pending. `ToolBarToggleTests.AGroupedButtonKeepsTheToolbarTemplateAndSize` passes. |
| TIP-01 | | | |
| INST-01 | | | |
| INST-02 | | | |
| INST-03 | | | |

### Build ___ (1.0.0-…+build.N) — date

| ID | Result | By | Note |
|---|---|---|---|
| LIN-01 | | | |
| LIN-02 | | | |
| LIN-03 | | | |
| LIN-04 | | | |
| LIN-05 | | | |
| LIN-06 | | | |
| VIEW-01 | | | |
| VIEW-02 | | | |
| ANC-01 | | | |
| ANC-02 | | | |
| ANC-03 | | | |
| ANC-04 | | | |
| LNK-01 | | | |
| LNK-02 | | | |
| LNK-03 | | | |
| LNK-04 | | | |
| LNK-05 | | | |
| LNK-06 | | | |
| LNK-07 | | | |
| WEB-01 | | | |
| WEB-02 | | | |
| WEB-03 | | | |
| WEB-04 | | | |
| WEB-05 | | | |
| WEB-06 | | | |
| MENU-01 | | | |
| SPL-01 | | | |
| PERF-01 | | | |
| PERF-02 | | | |
| SWT-01 | | | |
| SWT-02 | | | |
| SWT-03 | | | |
| SWT-04 | | | |
| SWT-05 | | | |
| SWT-06 | | | |
| SWT-07 | | | |
| FM-01 | | | |
| FM-02 | | | |
| FM-03 | | | |
| FM-04 | | | |
| FM-05 | | | |
| TBL-01 | | | |
| TBL-02 | | | |
| TBL-03 | | | |
| TBL-04 | | | |
| TBL-05 | | | |
| TBL-06 | | | |
| TBL-07 | | | |
| BR-01 | | | |
| BR-02 | | | |
| BR-03 | | | |
| LST-01 | | | |
| LST-02 | | | |
| LST-03 | | | |
| OPN-01 | | | |
| OPN-02 | | | |
| OPN-03 | | | |
| OPN-04 | | | |
| OPN-05 | | | |
| OPN-06 | | | |
| OPN-07 | | | |
| OPN-08 | | | |
| OPN-09 | | | |
| BIG-01 | | | |
| BIG-02 | | | |
| BIG-03 | | | |
| BIG-04 | | | |
| BIG-05 | | | |
| BIG-06 | | | |
| BIG-07 | | | |
| BIG-08 | | | |
| SRC-01 | | | |
| SRC-02 | | | |
| SRC-03 | | | |
| SRC-04 | | | |
| SRC-05 | | | |
| SRC-06 | | | |
| SRC-07 | | | |
| SRC-08 | | | |
| FND-01 | | | |
| FND-02 | | | |
| FND-03 | | | |
| FND-05 | | | |
| VIEW-03 | | | |
| TIP-01 | | | |
| TIP-02 | | | |
| THM-01 | | | |
| PRN-01 | | | |
| MRK-01 | | | |
| MRK-02 | | | |
| INST-01 | | | |
| INST-02 | | | |
| INST-03 | | | |

## 5. Known limitations not being fixed for 1.0

- An inline raw-HTML `<a>` isn't clickable and has no Copy Link (`links.md` case 66).
- Copy Link and Ctrl+click aren't available in the Markdown source view.
- Email (`mailto:`) and file links never open. Copy them instead.
- A table with no outer pipes gains them once that table is edited in the formatted view.
- A CJK table is tidied once that table is edited.
- A table whose pipes don't line up byte for byte (for example because a tool counted `\|` as one character) is saved unaligned once that table is edited; one that lines up byte for byte, as in `tables.md`, keeps its padding.
- A repeated word that straddles two spell-check chunks (about 16 KB each) isn't flagged.
- A list directly after another list, whose item starts with a rule (`- a` then `+ ***`), saves as `* ***`, which reads as a rule.
- A list item that starts with a block (a quote, fence or table) has no margin line number.
- A file that ends at front matter's closing `---` gains a final newline once its front matter is edited.
- Formatted → Source has no busy box. It measured 50–600 ms.
- The opening card can't draw over the formatted view, so there its phases go to the status bar.
- Candidate, not yet decided: a centred table column written left-justified has its cells re-centred once that table is edited in the formatted view. TBL-06 step 2 checks this behaviour.
- A save through the formatted view writes only the top-level blocks you edited in Markdown Midget's conventions, apart from the cases listed in Help, *Markdown conventions*.
