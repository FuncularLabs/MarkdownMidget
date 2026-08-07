# Implementation plan: themes

Status: **stage 1 landed; stages 2-6 planned.** Supersedes the ROADMAP "Selectable
CSS templates / themes" sketch, which this replaces with decisions.

Stage 1 extracted the 36 screen colours into 43 custom properties in
`editor-src/styles/theme-default.css`, with `editor-src/test/theme-parity.test.mjs`
holding the palette to what shipped before it. Three notes for whoever takes stage 2:

- Everything from section 1 down describes the file as it stood *before* stage 1,
  so its `editor.css` / `main.js` line numbers are pre-refactor — the extraction
  moved them by four or five, and a couple name literals that are now variables.
  The reasoning holds; treat the citations as orientation, not navigation.
- The parity tests forbid defining a variable nothing reads, so `--mdm-code-fg`
  cannot be added to a palette until the declaration that reads it exists. Inline
  code takes its foreground from Nord today, and adding our own is a rendering
  change, not a refactor — it belongs with the layer work.
- The resolved-declaration diff is blind to variables that share a value. Three
  groups do: the three `#ffffff` surfaces, the two `#569ad4` accents, and four of
  the nine syntax tokens at `#81a1c1`. The wiring test pins each site by name; keep
  it that way when the split into `structure.css` / `chrome.css` moves rules around.

## What we're building

**View ▸ Theme**, listing `.css` files found in a themes directory. Filename minus
extension is the display name. Six shipped themes — the current look as *Default*,
plus one dark, one slate, and three light. Users can drop their own file in a
custom directory and it appears in the menu. A file that isn't valid CSS is listed
but disabled, with a tooltip naming the first error. The choice survives updates.

---

## 1. The hard part is not the colours

`editor-src/styles/editor.css` is 418 lines with essentially every colour
hardcoded — 36 distinct colours on screen, plus 12 more that are print-only.
There is one existing custom property, `--mdm-page-width` (`editor.css:26`), set at
runtime from the host by `main.js:567` for the Document Width modes. **That is the
precedent to follow**: a host-driven variable seam already works here; themes
generalise it rather than introducing it.

Three complications:

**Milkdown's Nord theme is already in the cascade.** `main.js:54-56` imports, in
order: `prosemirror.css`, `@milkdown/theme-nord/style.css`, then `editor.css`, and
the built bundle preserves that order. Our stylesheet already fights Nord on
specificity — see the comment at `editor.css:159-160` ("Beat Nord's
`.milkdown-theme-nord.prose table` rules (0,2,1)"). The rules that fight are emitted
**unlayered**, so that contest is a genuine specificity one — but note the bundle
does contain cascade layers already (Tailwind v4 output from the Nord package), which
matters for how ours must be declared. See below.

**Some CSS is app behaviour, not decoration.** Spell squiggles (`.mdm-misspelled`),
formatting marks (`.mdm-mark`), the mermaid active/error states, `.selectedCell`
and `.column-resize-handle` are things the app depends on. A theme must not be able
to switch them off.

**Print is three blocks, not one.** `@media print` appears at `editor.css:252`
(mermaid), `294-376` (the main block) and `416` (squiggles). Consolidating print is
therefore a three-site job, and print must stay light whatever theme is selected —
a dark theme reaching print output produces unreadable pages and wastes toner.

### The seam

Three categories, not two. The middle one is the one that's easy to miss:

| Category | Contains | Themeable? |
|---|---|---|
| **Structure** | layout, spacing, sizing, `max-width`, flex | Only if it wants to; invariants carry `!important` |
| **Chrome** | squiggles, formatting marks, mermaid frame, `.selectedCell`, resize handle, print | **Colours yes, existence no** |
| **Decoration** | everything else — text, headings, links, quotes, code, tables, syntax | Yes |

The trap is treating chrome as un-themeable *wholesale*. Ten colours live in
chrome-owned rules — the mermaid frame is literally `background: #ffffff` with a
`#e2e4e8` border (`editor.css:232-233`), `.selectedCell` is a light blue
`rgba(86,154,212,.25)`, `.column-resize-handle` is `#569ad4`. If chrome is simply
un-overridable, then selecting a dark theme gives you a **white mermaid box on a
dark page** and a steel-blue selection on a dark table — exactly the failure §4
sets out to prevent. So chrome rules read their colours from variables like
everything else; what chrome refuses is *removal*, not recolouring.

### How chrome actually wins — declared layer order

The obvious construction is "load chrome last and it wins." **That is wrong, and so
is the next thing you'll reach for.** Both are worth stating, because this section
has now been got wrong twice — once per intuition.

*Load order alone* fails: within one origin and one layer, CSS sorts `!important`
by **specificity first**, source order only as a tiebreak. A `chrome.css` rule at
(0,1,0) loses to a theme's `.mdm-prosemirror .mdm-misspelled { … !important }` at
(0,2,0) no matter what loads last.

*"Leave chrome unlayered and layer the theme"* also fails, in the opposite
direction, because the `!important` rule inverts for layers
([MDN](https://developer.mozilla.org/en-US/docs/Web/CSS/@layer)):

| | Normal declarations | `!important` declarations |
|---|---|---|
| Unlayered vs layered | **unlayered wins** | **layered wins** |
| Among layers | **later** layer wins | **earlier** layer wins |

So unlayered `!important` is the *weakest* author `!important` there is. Leaving
chrome unlayered would mean a theme could override it *and* — because
`theme-default.css` would also be unlayered and therefore beat the layered theme's
normal declarations — the theme would be inert for everything else. Recolouring and
removal invert together, which is exactly why the arrangement reads as plausible.

**The construction that works is a declared layer order that includes the vendor
CSS**, at the top of the bundle:

```css
@layer mdm-override, mdm-vendor, mdm-chrome, mdm-structure, mdm-base, mdm-theme;
```

with Nord/ProseMirror in `mdm-vendor`, `chrome.css`, `structure.css` and
`theme-default.css` each in their layer, the injected theme wrapped in
`@layer mdm-theme { … }`, and chrome's must-never-break declarations carrying
`!important`. `mdm-override` is explained below — it holds the handful of our rules
that must out-rank a *vendor* `!important`. Read the table above against that order:

- **Normal** flows theme → base → structure → chrome, so the selected theme wins
  every variable and every ordinary colour. Themes work.
- **`!important`** flows chrome → structure → base → theme, so chrome's squiggles,
  formatting marks and print rules cannot be removed at any specificity. Containment
  works.

Both directions, from one declaration.

**Layering the vendor CSS is not optional, and this is the subtlest part of the
plan.** The moment our stylesheet moves into layers, anything left *unlayered*
outranks all of it for normal declarations — regardless of specificity, layer index
or source order. And the Nord package leaves plenty unlayered: it is Tailwind v4
output, emitting `@layer properties, theme, base, components, utilities` and then
roughly 55 **unlayered** rules after them, including
`.milkdown-theme-nord pre`, `blockquote`, `code`, `table`, `th/td`, and
`.ProseMirror .column-resize-handle`. Layer ours and leave those alone, and:

- `--mdm-pre-bg` silently stops working (Nord's light `--color-gray-100` wins) while
  `--mdm-pre-fg` still applies — light background, near-white text;
- `--mdm-resize-handle` never applies at all, breaking the "chrome recolours via
  variables" promise above;
- blockquote and inline code revert to Nord. (Table *borders* survive: our th/td
  rule at `editor.css:175` is `!important`, which as a layered `!important` still
  beats Nord's unlayered *normal* border — and Nord never sets `border-color`
  anyway. The failure is uneven, which is what makes it easy to miss.)

Declaring `mdm-vendor` early fixes all of it, and retires the specificity fight the
comment at `editor.css:159-160` documents: our rules win by layer rather than by
out-specifying.

**But putting the vendor in an early layer inverts `!important` in its favour, and
one existing rule depends on winning that fight.** Nord sets
`padding-inline`/`padding-block` `!important` on `.milkdown-theme-nord.prose td/th`
(0,2,1), and `editor.css:165-171` exists solely to beat it — its own comment says
so — with a higher-specificity `!important` at (0,3,1). Specificity decides that
today because both are unlayered. Move Nord into `mdm-vendor` and `!important` sorts
by layer first, earliest wins, so **Nord's padding wins regardless of specificity**
and table cells jump from 1px/6px to 12px/24px in every theme including Default.
That is why the order starts with `mdm-override`: our rules that must beat a vendor
`!important` go there. Exactly one qualifies today (`editor.css:168-171`), so the
cost is a few lines — but it has to be found deliberately, because nothing fails
loudly. Stage 2's acceptance tests include it.

Nord's four other `!important` sites are harmless: `[hidden]{display:none!important}`
(already outranks our unlayered `!important` today, so no change),
`img.ProseMirror-separator`, `.milkdown-theme-nord img{margin-block:0!important}`,
and `td/th p{margin:0!important}` which sets the value we set anyway.

The same reasoning applies to Tailwind's own layers: because layer order is
first-appearance, declaring ours at the top of the bundle would otherwise place
Tailwind's *after* ours, so its `@layer base` preflight
(`a{color:inherit}`, `h1..h6{font-size:inherit;font-weight:inherit}`,
`*{margin:0;padding:0;border:0}`) would beat every colour and every margin we set.
Folding the whole vendor import into `mdm-vendor` nests those as
`mdm-vendor.base` etc. and settles it.

**Build change this implies.** There is no `@import … layer()` seam today: the CSS
enters through JS (`main.js:54-56`) and esbuild concatenates it (`build.mjs`). So
stage 2 needs either a CSS entry file that does
`@import "…/style.css" layer(mdm-vendor);` and is imported instead of the three JS
imports, or a post-build step that wraps the vendor portion. Decide which before
starting stage 2 — it is the one piece of build work in this plan.

**Two consequences to carry into implementation:**

- **The `@layer` wrapper is textual, so brace balance is a containment control, not
  just a lint.** A theme with one unbalanced `}` closes the wrapper early and the
  remainder parses outside it. Unlayered is weaker *for `!important`*, so that alone
  is harmless —
  the risk is that escaped text can name a layer of ours: a stray
  `@layer mdm-chrome { .mdm-misspelled { … !important } }` lands in the **real**
  `mdm-chrome` and then wins on source order. Inside the wrapper the same text
  nests harmlessly as `mdm-theme.mdm-chrome`. So the §5 validator runs **before**
  injection, with no "warn but apply anyway" path.
- **Containment is partial, and should be described that way.** Layer order only
  protects properties something in an earlier layer actually declares. Nothing in
  `editor.css` sets `display` on `.mdm-prosemirror`, and `visibility`, `opacity: 0`,
  `font-size: 0` and `position: absolute; left: -99999px` are all still reachable. The
  guarantee is "a theme cannot remove the squiggle by restyling `.mdm-misspelled`",
  not "a theme cannot make the editor unusable". A user's own CSS file is a user's
  own problem past that line; the validator and the layer order are there to stop
  *accidents* and drive-by hostility, not a determined author.

Structure gets the same treatment for the same reason: `themes.md` used to assert
that themes "never touch" layout as though by construction, but nothing enforced it.
In the declared order above, a theme's normal declarations still outrank
`structure.css` — deliberately, so a theme can widen a blockquote — while any
structural invariant that must not move carries `!important` in `mdm-structure` and
therefore wins.

### Variable inventory

The refactor's rule: **distinct values stay distinct.** Collapsing "obviously the
same" colours is what turns stage 1 from a refactor into a rendering change.

| Group | Variables |
|---|---|
| Surface | `--mdm-app-bg` (`#e7e7e7`), `--mdm-page-bg`, `--mdm-text`, `--mdm-page-shadow` |
| Scheme | `--mdm-color-scheme` — see below; **required for dark themes** |
| Headings | `--mdm-heading` (`#4682b4`), and **three separate** muted values `--mdm-h4` (`#555`), `--mdm-h5` (`#606060`), `--mdm-h6` (`#707070`) |
| Links | `--mdm-link` (`#4582b4`), `--mdm-link-hover` |
| Quote | `--mdm-quote-bar`, `--mdm-quote-bg`, `--mdm-quote-text` |
| Code | `--mdm-code-bg`, `--mdm-pre-bg`, `--mdm-pre-fg` (`#d8dee9`, `editor.css:130`) and **`--mdm-code-fg`** — inline `code` gets its colour from Nord, not from us (`.milkdown-theme-nord code { color: var(--color-nord10) }` = `#5e81ac`), so every dark theme must override a colour our stylesheet never sets on screen (`editor.css:336-338` does set it, but only inside `@media print`). Two distinct foregrounds, hence not one `--mdm-code-text` |
| Tables | **two** border values — `--mdm-table-border` (`#d7dbe0`, the table) and `--mdm-cell-border` (`#dde1e6`, th/td) — plus `--mdm-th-bg`, `--mdm-th-text`, `--mdm-row-alt-bg`, and **`--mdm-td-bg`** — `editor.css:188` is `background:#ffffff !important` and is easy to miss because it reads like "the page colour"; leave it hardcoded and every dark theme gets white table cells |
| Rules | `--mdm-hr` |
| Syntax | 9 token variables mapping **6 distinct** Nord colours (`editor.css:379-407`: `#616e88 #81a1c1 #88c0d0 #a3be8c #b48ead #ebcb8b`). Nine names for six values is deliberate: it lets a theme differentiate tokens the Nord palette happens to share |
| Chrome | `--mdm-squiggle`, `--mdm-mark`, `--mdm-mermaid-bg`, `--mdm-mermaid-border`, `--mdm-mermaid-error-bg`, `--mdm-mermaid-error-border`, `--mdm-mermaid-error-text`, `--mdm-mermaid-empty`, `--mdm-cell-selected`, `--mdm-resize-handle` |
| Host | `--mdm-source-bg`, `--mdm-source-fg` (§4) |
| Mermaid | `--mdm-mermaid-theme` (a name, not a colour — §4) |

Roughly 40. Two specific traps for whoever does the refactor:

- **`#4682b4` (headings, line 73) and `#4582b4` (links, line 99) differ by one
  digit** and are almost certainly an original typo. Keep them as two variables
  anyway. Deciding they're "the same colour" mid-refactor is precisely how a no-op
  stops being a no-op; unify them later as a deliberate visual change if wanted.
- **`--mdm-color-scheme`.** `editor.css:3` sets `color-scheme: light`. Every dark
  theme must flip this to `dark` or the WebView's scrollbar (`#app` has
  `overflow:auto`), form controls and default canvas stay light-on-dark. It is not
  a colour, so it is easy to leave out of a colour inventory — and it is a visible
  bug in every dark theme on day one if you do.

---

## 2. Which six themes, and why

The brief asked for themes with measurable mindshare. VS Code Marketplace install
counts are the only large public signal; Notepad++ ships a fixed built-in set with
no per-theme telemetry, so it contributes *presence* rather than ranking.

| Theme | Signal | Licence |
|---|---|---|
| GitHub (Primer) | **19.7M installs** — the most-installed *colour* theme extension (several icon-theme and language extensions rank higher overall) | MIT |
| One Dark Pro | **12.6M** | MIT (Atom `one-dark-syntax`) |
| Dracula Official | **10.8M**, and runs an explicit port programme | MIT |
| Nord | 1.27M, plus it is *already* our syntax palette | MIT (see caveat) |
| Solarized | ships built-in with **both** VS Code and Notepad++ | MIT |

All five palettes are MIT, and each theme file must carry an attribution header
naming the original author and licence.

> **Nord caveat:** the palette and theme repos (`nordtheme/nord`,
> `nordtheme/visual-studio-code`) are MIT, but `nordtheme/assets` — logos, wordmark,
> banner artwork — is **CC BY-NC-SA 4.0**, i.e. non-commercial. Irrelevant to
> shipping the colours; relevant the moment any Nord *branding* is reached for in
> docs or an About screen. Use the name and the hex values, not the artwork.

### Recommendation

| Menu name | Kind | Palette basis |
|---|---|---|
| **Default** | light | Today's look, unchanged — the safe fallback |
| **Dracula** | dark | Dracula (MIT). Highest-install true-dark, and its port programme makes intent unambiguous |
| **GitHub Dark Dimmed** | slate / medium-dark | Primer (MIT). Canvas `#22272e` on `#adbac7` text — **7.6:1**, against Dark Default's 16.0:1 (`#0d1117`/`#e6edf3`). Purpose-built as a lower-contrast dark, and from the most-installed colour theme. **Decided over Nord.** |
| **GitHub Light** | light | Primer (MIT). From the most-installed colour theme; the look most people read markdown in |
| **Solarized Light** | light | Solarized (MIT). The warm/sepia option, low-glare, universally shipped |
| **One Light** | light | Atom One Light (MIT). Cooler and higher-contrast than the other two, so the three lights are genuinely different rather than three greys |

Nord was the alternative and was rejected: it would have been cheaper (our Prism
tokens are already its palette) but Dimmed is purpose-built for this slot and comes
from the same family as GitHub Light, so the two share a syntax vocabulary.

**Knock-on:** GitHub Dark Dimmed's nine syntax-token variables come from Primer, not
from the Nord values already in the tree — a little more work than Nord would have
been, and the one place stage 6 isn't just "type in the palette". Note also that
Nord doesn't leave the project: `editor.css:379-407` uses the Nord palette for
syntax in the **Default** theme, and `@milkdown/theme-nord` remains a dependency
whose CSS we ship. So the Nord MIT attribution is owed regardless — it belongs in
`theme-default.css`'s header, not in a theme we're no longer shipping. (The
CC BY-NC-SA caveat on `nordtheme/assets` is moot now that no theme carries the
name prominently, but the palette attribution still stands.)

Naming: use the original names — they're the reason a user recognises the theme,
and MIT with attribution covers it.

---

## 3. Where theme files live

Requirement: durable across updates; profile when installed, next to the exe when
portable.

```
installed  →  %LocalAppData%\MarkdownMidget\themes\          (built-in, refreshed on launch)
              %LocalAppData%\MarkdownMidget\themes\custom\   (yours, never touched)

portable   →  <exe dir>\themes\
              <exe dir>\themes\custom\
```

`UpdateService.IsInstalled()` (→ `RegistrationService.IsRunningFromAppDataInstall()`)
already makes this distinction; reuse it rather than inventing a second rule.

**Built-ins ship as embedded resources, extracted on launch, overwriting.**
`ExtractEmbeddedEditor()` (`MainWindow.xaml.cs:258`) establishes the
overwrite-on-launch mechanism — it uses `File.Create`, so it rewrites
unconditionally, which is what makes an update deliver theme fixes automatically.
The corollary — *edits to a built-in are lost on next launch* — is the first thing
the sample custom theme explains, along with "copy it to `custom\` and rename it".

Two things that precedent does **not** cover, and that need deciding here:

- **`ExtractEmbeddedEditor` always writes to `%LocalAppData%`**, never to the exe
  directory. Writing to `<exe dir>` is a new risk surface: read-only locations,
  network shares, AV interference, a portable copy on a USB stick. The portable
  path must tolerate extraction failing — fall back to `%LocalAppData%\themes` and
  say so once in the status bar, rather than starting with no themes.
- **Portable version thrash.** `ApplyPortableAndRestart` deliberately leaves the
  old exe in place, so two versions share one `<exe dir>\themes\` and each
  overwrites the built-ins with *its* embedded copy on launch. Run the old exe once
  and the new version's theme fixes silently revert. Fix by version-stamping the
  extraction (write built-ins only when the stamp differs) — the same shape as the
  `?v=` cache-bust the editor bundle already uses.

`custom\` is created once and never written to again. Filename collision between a
custom and a built-in: **custom wins**, and the menu marks it — the user put it
there deliberately.

---

## 4. Applying a theme at runtime

**In the WebView:** the host reads the file and calls `MDM.setTheme(css)`, which
sets the text of a `<style id="mdm-theme">` in `<head>`, wrapping the file in
`@layer mdm-theme { … }` per §1. CSS text crosses the bridge with `JsLiteral`
(which is `JsonSerializer.Serialize`, so CSS escapes safely). Cap the file at, say,
256 KB at enumeration time — the shipped six are a few KB each, and nothing else
in the pipeline bounds a user-supplied file before it is read, validated and
injected.

**Three things outside the WebView must move too**, or the result is half-themed:

- **The markdown source view.** `SourceBox` is a WPF `TextBox`; selecting a dark
  theme and hitting Ctrl+E onto a blinding white pane is an obvious bug. **Do not
  parse the CSS in C# to find the colours.** A regex over `:root` breaks on
  commented-out declarations, `var()` indirection, multiple `:root` blocks, `:root`
  nested in `@media`, and `html {}` instead of `:root` — and worst, on colour syntax
  `ColorConverter` cannot read. It takes `#RGB`/`#RRGGBB`/`#AARRGGBB`, `sc#` and
  named colours; it does **not** take `rgb()`, `hsl()`, `oklch()` or `color-mix()`.
  (Primer itself publishes hex, so the *first* ported palette would survive a regex —
  which is the trap: it works until someone writes `rgba()` for an overlay, which
  Primer does, or `oklch()`, which this repo's own bundle already contains.) Instead
  **ask the browser**: after `setTheme`, read the value back through a probe element
  with `getComputedStyle`, which resolves `var()`, cascade order, media queries and
  every colour syntax the engine supports. It returns a normalised `rgb()`/`rgba()`,
  which `ColorConverter` also cannot parse — so the host pulls the three or four
  numbers out with one regex that cannot be wrong about anything, and builds the
  `Color` itself.
- **Mermaid.** `mermaid.js:18` hardcodes `theme: 'default'`. A dark editor with a
  bright diagram looks broken. Each theme declares `--mdm-mermaid-theme` (one of
  mermaid's built-ins: `default`, `dark`, `neutral`, `forest`), read on the JS side
  and passed to `initialize`. Two traps: `initOnce()` returns early once
  `mermaidReady` is set (`mermaid.js:16-19`), so re-theming needs that guard
  lifted; and `svgCache` keys on source text alone (`mermaid.js:23`), so re-rendering
  without clearing the cache re-serves the previous theme's SVG.
- **Print.** Print ignores the theme entirely: the consolidated print block
  re-asserts the light palette with `!important` from `chrome.css`, which sits in
  `mdm-chrome` — an earlier layer than the theme, so its `!important` wins per §1.
  Worth stating in HELP.md, because it will otherwise read as a bug the first time
  someone prints from Dracula.

**Out of scope, decided:** the WPF menu bar, toolbar and status bar are not themed.
They are native Windows chrome, they look correct in the OS theme, and doing them
well is separate work with its own dark-title-bar problems. This is a settled
decision rather than a deferral — if it is ever revisited it should be because the
menu bar is being restyled for some other reason, not because a dark theme made it
look inconsistent. The source view is the exception and *is* themed (§4), because it
is the document, not chrome.

---

## 5. Validating theme CSS

Two jobs, and the ROADMAP sketch this plan replaces named both. Only one is about
syntax.

### Structural validity → enables/disables the menu entry

Catch what people actually get wrong by hand: unbalanced `{`/`}`, unterminated
`/* comment` or string, a declaration with no `:`, an `@media`/`@supports` that
never closes. Hand-written tokenizer, ~150 lines, no dependency, reporting
`line:col` plus a sentence. Pure function over a string, so it unit-tests the way
`TextStats`, `WindowPlacement` and `UpdateVersion` already do — malformed input in,
exact message out. Target ~25 cases.

Deliberately **not** validating that properties or values are meaningful. An
unknown property is the user being clever; the browser ignores what it can't use.

### Network references → a security decision, not a syntax one

Carried forward from the ROADMAP entry, which raised it and must not be dropped
silently: `index.html` ships **no CSP**, and `AddWebResourceRequestedFilter` covers
only `https://{DocHost}/*` (`MainWindow.xaml.cs:180`). So a theme containing
`@import url("https://…")` or `background: url("https://tracker/…")` makes a live
outbound request from inside the app. For a feature whose pitch is "drop someone
else's CSS file in a folder", that is a beacon and a fingerprinting vector.

**Decision: reject off-origin references at validation time**, and treat it as an
error that disables the entry with a specific message ("references a remote URL"),
not a silent strip — the user should know why their theme was refused rather than
wonder why half of it didn't apply. `@layer` already kills bare `@import`; `url()`
needs the explicit check. This is consistent with the DOMPurify posture for raw
HTML: permissive about what renders, strict about what reaches the network.

**Relative `url()` cannot be allowed as a consolation prize, because it does not
resolve where an author would expect.** The theme is injected into a `<style>`
element, so relative URLs resolve against the *document* base URL — and
`main.js:465` `setDocBase` installs `<base href="https://mdm-doc.invalid/">`
whenever a document is open. `url(texture.png)` therefore points into the open
markdown file's folder, or into the extracted editor folder when nothing is open;
never the themes directory. Either reject relative `url()` too, or (if background
textures are wanted later) have the host rewrite them to absolute virtual-host URLs
before injection. Rejecting is the v1 answer.

Third line of defence is **partial, deliberately**: the layering in §1 stops a theme
removing the things chrome declares — squiggles, formatting marks, print colours —
but it does not stop a determined author making the editor unusable by other means
(`display`, `visibility`, `opacity`, absolute positioning). See the containment note
in §1: the goal is to stop accidents and drive-by hostility in a file someone was
handed, not to sandbox a user's own CSS against themselves.

---

## 6. Menu, persistence, failure

- **View ▸ Theme** → one checkable item per file, built-ins first, separator, then
  custom. Radio behaviour. Default pinned first, rest alphabetical.
- Rebuilt on submenu-open, exactly as `FileMenu_Opened` → `BuildRecentMenu()`
  already does, so a file dropped in `custom\` appears without a restart.
- Invalid → `IsEnabled = false`, `ToolTip = "Invalid CSS (…)"`.
- Persist as `Theme` in `settings.json` by **filename**, not index. Missing next
  launch → fall back to Default and say so once in the status bar. Never silently.
- A theme that fails to read (locked, deleted between enumeration and click) is
  reported in the status bar and leaves the current theme applied — the same
  failure posture the update and backup paths take.

> **Menu placement reverses an earlier decision, deliberately.** The superseded
> ROADMAP entry said to expose this "in the new File ▸ Settings… dialog rather than
> adding another menu." The brief asks for View ▸ Theme, and that is the better
> call: theme is a *view* of the current document that users switch on a whim and
> want to see applied instantly, which is the same reason spell check, word wrap
> and document width live on View while the recent-files limit lives in Settings.
> Noting it so the reversal is a decision on the record.

Applying is instant — swapping `<style>` text re-renders, no reload.

---

## 7. Delivery, in order

1. **Refactor `editor.css` to variables.** No behaviour change; `theme-default.css`
   reproduces today's palette exactly. Land alone so the diff reads as a pure
   refactor. **Acceptance is a hex-literal diff, not screenshots**: extract every
   colour literal before and after and prove the rendered set is identical.
   Screenshots will not catch h5 and h6 collapsing into one value.
2. **Split `structure.css` / `chrome.css`, consolidate the three print blocks, and
   introduce the declared layer order — including `mdm-vendor`.** This is the stage
   with the build change (a CSS entry file, or a post-build wrap, so the vendor CSS
   can be imported *into* a layer). Three acceptance tests, all of which this plan
   got wrong at some point and would have caught it:
   - a hostile theme using `!important` at higher specificity against
     `.mdm-misspelled` must not remove the squiggle;
   - `--mdm-pre-bg` and `--mdm-resize-handle` must visibly take effect, which they
     do not if the vendor CSS is left unlayered;
   - table cells must still be 1px/6px, which they are not if `editor.css:168-171`
     fails to make it into `mdm-override`.

   Also make sure the chosen build option preserves the vendor's 23 unlayered
   `@property` registrations — they aren't cascaded, so layering doesn't affect
   them, but dropping them would break Tailwind's custom-property fallbacks.
3. **`CssValidator` + tests**, including the remote-URL rule. Pure, no UI.
4. **Theme store**: directory resolution (installed vs portable, with the fallback
   and the version stamp from §3), extraction, enumeration, custom-wins collision.
   Unit-testable against a temp directory the way `BackupStore` is.
5. **Menu + persistence + `MDM.setTheme`**, one theme file at a time. Source view
   read-back and mermaid re-theming follow here.
6. **The five palettes** with attribution headers, plus the commented sample custom
   theme. Cheap once the seam exists; expensive if attempted first.

Stages 1–2 carry the risk. Everything after is additive.

## Risks

- **The refactor is invisible-until-it-isn't.** 418 lines of hand-tuned colour with
  specificity fights against Nord. Mitigation is the hex-literal diff in stage 1,
  plus grepping the result for surviving `#` literals and justifying each — noting
  that legitimate survivors live in **three** print blocks, not one.
- **Contrast, per theme.** A ported palette can leave squiggles, formatting marks,
  selected cells or the resize handle invisible. Each shipped theme needs those four
  looked at, not just body text.
- **Nord paints a selected-cell overlay we don't own.** The vendor CSS carries its
  own `.milkdown-theme-nord.prose.ProseMirror .selectedCell:after` with a nord8
  tint, *in addition* to ours. `--mdm-cell-selected` does not reach it, so every
  dark theme keeps a light-blue wash on selected cells until that rule is overridden
  too. Worth finding during stage 2 rather than during theme six.
- **Scope creep into WPF chrome.** Say no in v1; revisit when the menu bar is
  restyled for other reasons.
