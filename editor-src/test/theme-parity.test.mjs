// The default theme must be a rename, not a redesign.
//
// Pulling 36 screen colours out of editor.css into named variables is the kind of
// change that looks finished long before it is correct. The two failure modes are
// both invisible: a variable that resolves to nothing at all (a typo'd name), and
// two near-identical values quietly collapsing into one — #606060 and #707070 as
// h5/h6, or #4682b4 and #4582b4, which differ by a single digit and read as the
// same blue to any eye and any screenshot.
//
// So the check is not "does it look right". It is: resolve every var() back to its
// literal, flatten the stylesheet to an ordered list of declarations, and require
// it to equal the list recorded before the refactor, exactly.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { JSDOM } from 'jsdom';
import { declarations, rootVariables } from './css-declarations.mjs';
import { mermaidConfig, mermaidLook } from '../src/mermaid-look.js';

const here = dirname(fileURLToPath(import.meta.url));
const read = (...p) => readFileSync(join(here, '..', ...p), 'utf8');

// Bundle order — the order bundle.css imports them, which is the order they reach
// the output. Vendor CSS is deliberately absent: these tests are about ours.
const LAYERS = ['override', 'chrome', 'structure', 'base', 'print'];
const editorCss = LAYERS.map((n) => read('styles', `${n}.css`)).join('\n');
const defaultTheme = read('styles', 'theme-default.css');
const baseline = JSON.parse(read('test', 'fixtures', 'editor-css-baseline.json'));
// Which layer each declaration went to, generated from the split rather than
// listed, so it cannot be short by four the way the hand-written pins were.
//
// THE TWO FIXTURES ARE NOT THE SAME KIND OF THING, and a future generator that
// refreshes both would quietly destroy the invariant they encode:
//
//   editor-css-baseline.json  a record of editor.css BEFORE it was ever split.
//                             Never regenerate it. Its ordering is what every
//                             order check is anchored to; regenerated, they
//                             would anchor to whatever the current files say
//                             and could no longer disagree with them.
//   layer-partition.json      a snapshot of the current split, and the only
//                             thing that notices a declaration moving between
//                             files — which is a move between LAYERS.
//
// Adding or moving a declaration on purpose means editing both by hand, inserting
// the key where it belongs. Rebuilding them from the current files instead makes
// the two fixture-anchored tests agree with whatever the files say, which is not
// the same as passing.
const partition = JSON.parse(read('test', 'fixtures', 'layer-partition.json'));

/** Every declaration keyed by where it applies, so a file split can't reorder it. */
function byKey(list) {
  const m = new Map();
  for (const d of list) m.set(`${d.where} { ${d.prop} }`, d);
  return m;
}

test('the default theme resolves to the palette that shipped before it existed', () => {
  // Keyed rather than index-by-index now: these declarations live in five files and
  // their concatenated order is not the order they had in one, so an index
  // comparison would fail on the move alone.
  //
  // That is a genuine weakening and the next test pays it back. It is NOT true that
  // order is meaningless here — two rules of equal specificity can hand the same
  // ELEMENT the same property, and then document order is the only thing deciding.
  // "No selector declares the same property twice" is a fact about selector strings
  // and says nothing about elements.
  // The theme's variables, plus the one JS-OWNED variable the stylesheet reads:
  // --mdm-page-width is set at runtime by MDM.setPageWidth (main.js), never by a
  // theme, and its baseline value is the 850px portrait default. Without this
  // entry the resolver now honestly keeps the var() text (it no longer
  // substitutes fallbacks for unknown names — see css-declarations.mjs), and the
  // comparison would fail on text the browser never renders.
  const vars = new Map(rootVariables(defaultTheme));
  vars.set('--mdm-page-width', '850px');
  const now = byKey(declarations(editorCss, vars));
  const was = byKey(baseline);

  assert.deepEqual([...now.keys()].sort(), [...was.keys()].sort(),
    'a declaration was added or lost; splitting the file may only move them');

  for (const [key, before] of was) {
    assert.equal(now.get(key).value, before.value, `${key} changed value`);
  }
});

test('the split partitions the original order without permuting it', () => {
  // What the keyed comparison above gives up, bought back — as an invariant rather
  // than a list, because listing the order-sensitive pairs by hand has now failed
  // three times running here. The first attempt named none, the second named two,
  // and a reviewer then found four more: h5 and h6 collapsing to steelblue exactly
  // like the h4 the list did name; `hr`'s `border` then `border-top`, which no
  // keyed comparison can EVER see because the two keys differ; the mermaid error
  // frame reverting to a plain one; and inline-HTML paragraph margins.
  //
  // None of that enumeration is needed. Two rules of equal specificity on the same
  // element are decided by document order alone — so if every layer keeps the
  // relative order its declarations had in the file they came from, no such contest
  // can change outcome. Checked as a subsequence, which needs no specificity
  // arithmetic and no guess about which selectors can match the same element.
  //
  // That is necessary and NOT sufficient, which was the next thing to get wrong.
  // Order within a layer is only half of it: moving a declaration to a different
  // file moves it to a different LAYER, reordering it against everything in both —
  // and its rank can still slot neatly into the destination's increasing sequence,
  // so a subsequence check waves it through. Moving `.mdm-prosemirror h4 { color }`
  // next to the `h4 { font-size }` rule in structure.css is the tidy-up any
  // refactor invites, and it turns h4 steelblue — the exact regression the
  // hand-written pin this replaced did catch.
  //
  // So the partition is pinned as well, and also generated. Between them: each
  // layer owns exactly these declarations, in exactly this order.
  const rank = new Map(baseline.map((d, i) => [`${d.where} { ${d.prop} }`, i]));

  for (const layer of LAYERS) {
    const keys = declarations(read('styles', `${layer}.css`))
      .map((d) => `${d.where} { ${d.prop} }`);

    let previous = -1;
    let previousKey = null;
    for (const key of keys) {
      assert.ok(rank.has(key), `${layer}.css declares ${key}, which the original never did`);
      assert.ok(rank.get(key) > previous,
        `${layer}.css: ${key} now follows ${previousKey}, but preceded it in the original — ` +
        'two rules of equal specificity on one element are decided by order alone');
      previous = rank.get(key);
      previousKey = key;
    }

    assert.deepEqual(keys, partition[layer],
      `${layer}.css no longer owns exactly the declarations it was split off with — ` +
      'a declaration in another file is a declaration in another layer');
  }

  // And nothing may quietly leave the split altogether.
  assert.equal(Object.values(partition).flat().length, baseline.length,
    'the partition no longer accounts for every original declaration — a layer key ' +
    'outside LAYERS, or a declaration added to a layer and to the fixture together');
});

test('print and chrome became authoritative, and nothing else changed importance', () => {
  // The one deliberate difference from the pre-split file, and it is forced by the
  // order rather than chosen. Both layers sit ahead of everything else, where a
  // NORMAL declaration is the weakest thing in the cascade — the old single file
  // won those contests by sitting last, which layering ends. So both carry
  // !important throughout, or their early position achieves the opposite of what
  // it is for. Measured: without it, a theme deleted the spell squiggle outright.
  //
  // It costs no theming. Every colour in chrome arrives through a var(), and a
  // theme changes the variable, not the declaration.
  // Parsed, not substring-matched: chrome.css's own comments name `.mdm-misspelled`
  // and `.mdm-mark` in prose, so a text search would accept a rule that had been
  // moved OUT of chrome purely because a comment still mentions it.
  const chromeKeys = new Set(declarations(read('styles', 'chrome.css'))
    .map((d) => `${d.where} { ${d.prop} }`));
  const isChrome = (key) => chromeKeys.has(key);
  const now = byKey(declarations(editorCss, rootVariables(defaultTheme)));

  for (const [key, before] of byKey(baseline)) {
    if (now.get(key).important === before.important) continue;
    assert.ok(key.startsWith('@media print') || isChrome(key),
      `${key} changed importance and is neither print nor chrome`);
    assert.equal(before.important, false, `${key} lost !important, which is never right here`);
  }

  // No exemptions. @page was exempted here originally, on the unexamined assumption
  // that page geometry was out of a theme's reach — it is not. A theme containing
  // only `@layer mdm-theme { @media print { @page { margin: 2in } } }` repaginated
  // the document from four pages to six, because a normal declaration in the first
  // layer is the weakest thing in the cascade.
  for (const file of ['print', 'chrome']) {
    const weak = declarations(read('styles', `${file}.css`)).filter((d) => !d.important);
    assert.deepEqual(weak.map((d) => `${d.where} { ${d.prop} }`), [],
      `a declaration in ${file}.css without !important loses to every later layer`);
  }
});

test('every var() the editor reads is one the default theme defines', () => {
  // Without this, a typo'd name still passes the comparison above whenever the
  // property it lands on is one the baseline happens not to record a colour for.
  const defined = rootVariables(defaultTheme);
  const used = new Set();
  for (const [, name] of editorCss.matchAll(/var\(\s*(--[\w-]+)/g)) used.add(name);

  // Set at runtime by the host for the Document Width modes, not by a theme.
  used.delete('--mdm-page-width');

  for (const name of used) {
    assert.ok(defined.has(name), `editor.css reads ${name}, which no theme defines`);
  }
});

test('no colour is left hardcoded on the editor surface', () => {
  // The whole point: a theme can only change what it can reach. Print is exempt
  // and stays literal — paper is light whatever theme is selected, because a dark
  // theme on paper is unreadable and empties a toner cartridge.
  const stranded = declarations(editorCss)
    .filter((d) => !d.where.includes('@media print'))
    .flatMap((d) => [...d.value.matchAll(/#[0-9a-fA-F]{3,8}\b|\brgba?\(/g)]
      .map(() => `${d.where} { ${d.prop}: ${d.value} }`));

  assert.deepEqual(stranded, [], 'these would ignore the selected theme');
});

test('print keeps its own colours, so paper stays readable under a dark theme', () => {
  const printColours = declarations(editorCss)
    .filter((d) => d.where.includes('@media print'))
    .flatMap((d) => [...d.value.matchAll(/#[0-9a-fA-F]{3,8}\b/g)].map((m) => m[0]));

  assert.ok(printColours.length > 0, 'print styles lost their explicit palette');
});

test('values that differ stay separate variables', () => {
  // Named individually rather than counted, because "distinct values stay
  // distinct" is a rule about specific pairs a tidying instinct wants to merge.
  const vars = rootVariables(defaultTheme);
  const mustDiffer = [
    ['--mdm-heading', '--mdm-link'],       // #4682b4 vs #4582b4 — one digit apart
    ['--mdm-h4', '--mdm-h5'],
    ['--mdm-h5', '--mdm-h6'],
    ['--mdm-table-border', '--mdm-cell-border'],
    ['--mdm-pre-bg', '--mdm-code-bg'],
    // Inline code and fenced code have two foregrounds, which is why there is no one
    // --mdm-code-text; merging them makes every fenced block take the inline colour.
    ['--mdm-code-fg', '--mdm-pre-fg'],
  ];

  for (const [a, b] of mustDiffer) {
    assert.ok(vars.has(a) && vars.has(b), `${a} / ${b} must both exist`);
    assert.notEqual(vars.get(a), vars.get(b), `${a} and ${b} collapsed into one value`);
  }

  // #ffffff appears as the page, a table cell and the mermaid frame. Three
  // variables, because a dark theme needs to move them independently — a shared
  // one gives you white table cells on a dark page.
  const white = ['--mdm-page-bg', '--mdm-td-bg', '--mdm-mermaid-bg'];
  assert.deepEqual(white.map((v) => vars.get(v)), ['#ffffff', '#ffffff', '#ffffff']);
});

test('each site reads its own variable, not a twin that matches today', () => {
  // The one regression the comparison above is blind to by construction. Wiring
  // table cells to --mdm-page-bg instead of --mdm-td-bg resolves to the same
  // #ffffff, so every resolved declaration is identical and the whole refactor
  // still passes — right up until someone selects a dark theme and gets white
  // cells on a dark page. Only the unresolved text can tell the two apart.
  // Print is IN this set since 0.8.1 — the three --mdm-print-* table variables
  // are the bounded exception to print-keeps-literals, and their pins below
  // need to see them. The media-context guard in the matcher keeps bare
  // (screen) markers from ever matching a print declaration.
  const unresolved = declarations(editorCss);

  // Anchored on a distinguishing selector fragment rather than the whole sorted
  // list, so reordering a selector doesn't fail the test — but the fragment must
  // still identify exactly one declaration, or the pin isn't pinning anything.
  const wiring = [
    ['.milkdown {', 'background', '--mdm-page-bg'],
    ['.mdm-prosemirror td {', 'background', '--mdm-td-bg'],
    ['tr:nth-child(odd) td {', 'background', '--mdm-row-alt-bg'],
    ['.mdm-prosemirror th {', 'background', '--mdm-th-bg'],
    // The screen th text pin, plus the three print-table variables — all newly
    // twinned in 0.8.1, when each print var was seeded with its screen value.
    ['.mdm-prosemirror th {', 'color', '--mdm-th-text'],
    ['@media print > .mdm-prosemirror th {', 'background', '--mdm-print-th-bg'],
    ['@media print > .mdm-prosemirror th {', 'color', '--mdm-print-th-text'],
    ['@media print > .mdm-prosemirror tbody tr:nth-child(odd) td {', 'background', '--mdm-print-row-alt-bg'],
    ['.mdm-mermaid {', 'background', '--mdm-mermaid-bg'],
    ['.mdm-mermaid-error {', 'background', '--mdm-mermaid-error-bg'],
    ['.mdm-prosemirror table {', 'border', '--mdm-table-border'],
    ['td, .mdm-prosemirror th {', 'border', '--mdm-cell-border'],
    ['blockquote {', 'border-left', '--mdm-quote-bar'],
    ['.column-resize-handle', 'background', '--mdm-resize-handle'],
    // The largest group sharing a value: four token variables all #81a1c1 today,
    // so swapping any two is invisible to every other check here — and the first
    // theme that gives keywords and punctuation different colours gets them
    // backwards. Distinguishing class per group.
    ['.token.comment', 'color', '--mdm-token-comment'],
    ['.token.punctuation {', 'color', '--mdm-token-punctuation'],
    ['.token.tag', 'color', '--mdm-token-property'],
    ['.token.number', 'color', '--mdm-token-number'],
    ['.token.string', 'color', '--mdm-token-string'],
    ['.token.operator', 'color', '--mdm-token-operator'],
    ['.token.keyword', 'color', '--mdm-token-keyword'],
    ['.token.function', 'color', '--mdm-token-function'],
    ['.token.regex', 'color', '--mdm-token-regex'],
    ['.mdm-prosemirror strong {', 'color', '--mdm-strong'],
    // The two newest twins: inline code and list markers are both Nord's #5e81ac in
    // Default, so swapping them is invisible to every resolved-value check here.
    ['li::marker', 'color', '--mdm-list-marker'],
    ['.mdm-prosemirror code {', 'color', '--mdm-code-fg'],
  ];

  for (const [marker, prop, expected] of wiring) {
    // A marker names its media scope: bare markers are screen rules, and a
    // marker that means the print variant says '@media print' itself. Without
    // this, '.mdm-prosemirror th {' also matches the print-layer pin of the
    // same element (its where is '@media print > .mdm-prosemirror th') and
    // every dual-declared site would 'match 2'.
    const matches = unresolved.filter(
      (d) => (d.where + ' {').includes(marker) && d.prop === prop
        && d.where.startsWith('@media print') === marker.startsWith('@media print'));
    assert.equal(matches.length, 1, `"${marker}" { ${prop} } should match one declaration, matched ${matches.length}`);
    assert.match(matches[0].value, new RegExp(`var\\(\\s*${expected}\\s*[,)]`),
      `${marker} { ${prop} } is wired to ${matches[0].value}, not var(${expected})`);
  }

  // The table also has to stay complete on its own, because keeping it complete by
  // hand has now failed twice: the first version of this test missed the shared
  // values entirely, and the second covered two of the three groups and left the
  // largest — four syntax tokens — unpinned. Anything sharing a value with another
  // variable is invisible to every other check here, so it has to appear above.
  const byValue = new Map();
  for (const [name, value] of rootVariables(defaultTheme)) {
    if (!byValue.has(value)) byValue.set(value, []);
    byValue.get(value).push(name);
  }
  const twins = [...byValue.values()].filter((names) => names.length > 1).flat();
  const pinned = new Set(wiring.map(([, , name]) => name));

  assert.deepEqual(twins.filter((t) => !pinned.has(t)), [],
    'these share a value with another variable, so only a by-name pin catches a swap');
});

test('the default theme defines nothing the editor never reads', () => {
  // An orphan variable is a promise to theme authors that nothing keeps: it shows
  // up in the sample file, gets set, and changes nothing.
  const used = new Set([...editorCss.matchAll(/var\(\s*(--[\w-]+)/g)].map((m) => m[1]));

  // Read by JavaScript instead of by a var() — and read out of getComputedStyle,
  // so it is a real declaration on :root and not somewhere a grep for var() will
  // ever find it. Mermaid draws its own SVG from its own palette; no CSS variable
  // of ours reaches inside one, so naming a mermaid built-in is the only lever a
  // theme has on it through a variable (the diagram is inline SVG, so selectors
  // reach it — against mermaid's own inline `<style>`, and nothing in there is
  // tested here or promised anywhere). Each entry names where it IS read, so an
  // orphan can't be parked here to silence the test.
  const readByScript = new Map([
    ['--mdm-mermaid-theme', 'src/main.js readThemeBack() -> mermaid.js setMermaidTheme()'],
  ]);
  for (const [name] of readByScript) {
    assert.ok(rootVariables(defaultTheme).has(name),
      `${name} is listed as read by script but the theme no longer defines it`);
  }

  const orphans = [...rootVariables(defaultTheme).keys()]
    .filter((v) => !used.has(v) && !readByScript.has(v));

  assert.deepEqual(orphans, [], 'defined by the theme but read by nothing');
});

test('a variable read by script is actually read by that script', () => {
  // The exemption above is only honest if the claim in it is checked. Otherwise
  // deleting the JS that reads a variable leaves the variable exempt forever, and
  // the sample file keeps advertising a setting that does nothing.
  const mainJs = readFileSync(new URL('../src/main.js', import.meta.url), 'utf8');
  assert.match(mainJs, /getPropertyValue\(\s*'--mdm-mermaid-theme'\s*\)/,
    'main.js no longer reads --mdm-mermaid-theme back out of computed style');
});

test('the nine syntax token names cover the six Nord colours', () => {
  const vars = rootVariables(defaultTheme);
  const tokens = [...vars.keys()].filter((k) => k.startsWith('--mdm-token-'));

  assert.equal(tokens.length, 9, 'one name per token group the editor styles');
  assert.equal(new Set(tokens.map((t) => vars.get(t))).size, 6,
    'Nord shares colours across groups; the names exist so a theme need not');
});

test('a dark theme can flip the colour scheme', () => {
  // Not a colour, so it falls out of a colour inventory — and leaving it behind is
  // a visible bug in every dark theme on day one: the WebView's scrollbar, its
  // form controls and its default canvas all stay light against a dark page.
  assert.equal(rootVariables(defaultTheme).get('--mdm-color-scheme'), 'light');
  // Fallback included: a theme file that fails to load leaves the seam with nothing
  // behind it, and `color-scheme` with no value is not the same as `light`.
  assert.match(editorCss, /color-scheme:\s*var\(\s*--mdm-color-scheme\s*,\s*light\s*\)/);
});

// ===== bold's own colour (--mdm-strong) =====
//
// jsdom can't cascade @layer or resolve var(), so these resolve declarations the way
// the parity tests above do: Default's variables with a theme's laid over them.

const builtinDir = join(here, '..', '..', 'src', 'MarkdownMidget', 'Themes', 'builtin');
const readTheme = (file) => readFileSync(join(builtinDir, file), 'utf8');
/** The two files a theme author actually reads, checked where a rule of ours can't hold. */
const sampleCss = readFileSync(join(builtinDir, '..', 'sample.css'), 'utf8');
const helpMd = readFileSync(join(here, '..', '..', 'HELP.md'), 'utf8');
const PREDATE_STRONG = ['Dracula.css', 'GitHub-Dark-Dimmed.css', 'GitHub-Light.css',
  'Midget-Solarized.css', 'One-Light.css', 'Solarized-Light.css'];
/** The palettes that arrived after --mdm-strong and still leave bold the colour of the
 *  text around it. Red Sparks has one hue, so a bold colour of its own could only be
 *  brighter or dimmer red; it sets --mdm-list-marker and --mdm-code-fg instead. */
const RED_SPARKS = ['Red-Sparks.css', 'Red-Sparks-2X.css'];
const STRONG_UNSET = [...PREDATE_STRONG, ...RED_SPARKS];

/** What `.mdm-prosemirror strong { color }` resolves to with this theme installed. */
function strongColour(themeCss) {
  const vars = new Map([...rootVariables(defaultTheme), ...rootVariables(themeCss)]);
  vars.set('--mdm-page-width', '850px');
  const hits = declarations(editorCss, vars)
    .filter((d) => d.where === '.mdm-prosemirror strong' && d.prop === 'color');
  assert.equal(hits.length, 1, 'expected exactly one screen rule colouring strong');
  return hits[0].value;
}

test('bold takes --mdm-strong when a theme sets one', () => {
  const theme = readTheme('Obsidiminutive.css');
  const strong = rootVariables(theme).get('--mdm-strong');
  assert.match(strong ?? '', /^#[0-9a-f]{6}$/, 'Obsidiminutive sets a hex --mdm-strong');
  assert.equal(strongColour(theme), strong);
});

test('a theme that leaves --mdm-strong unset keeps bold the colour of the text around it', () => {
  // Every built-in is either one that leaves the variable unset or the one that sets it,
  // so a new palette has to decide rather than slip past this.
  assert.deepEqual(readdirSync(builtinDir).filter((f) => f.endsWith('.css')).sort(),
    [...STRONG_UNSET, 'Obsidiminutive.css'].sort());

  // currentColor on `color` is the inherited colour: bold in a paragraph is the body
  // text, exactly as before the variable existed. Default ('' = no theme) included.
  for (const file of ['', ...STRONG_UNSET]) {
    const css = file ? readTheme(file) : '';
    assert.equal(rootVariables(css).has('--mdm-strong'), false, `${file} sets --mdm-strong`);
    assert.equal(strongColour(css).toLowerCase(), 'currentcolor', `${file || 'Default'}: bold gained a colour`);
  }
});

test('bold inside a heading or a link keeps that element\'s colour', () => {
  // As the source view does, where the heading and link spans consume the ** inside
  // them. More specific than the strong rule, in the same layer.
  const ours = declarations(read('styles', 'base.css'));
  const inside = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'a']
    .map((el) => `.mdm-prosemirror ${el} strong`).sort().join(', ');
  const hits = ours.filter((d) => d.where === inside && d.prop === 'color');
  assert.equal(hits.length, 1, `no single rule for ${inside}`);
  assert.equal(hits[0].value, 'inherit');
});

test('paper ignores the theme\'s bold colour, and only that', () => {
  // Pale bold on a dark screen is invisible on white paper, so print pins the
  // VARIABLE back to currentColor: bold prints as the text around it (#333 in a
  // quote, say). Not the colour itself. `color: inherit !important` in this first
  // layer also beat colours the document writes - <strong style="color:#00f">
  // printed black in every theme - and a custom theme's own strong rule.
  //
  // What this can't show is that outcome. jsdom doesn't cascade @layer or var(),
  // so an inline colour against print's !important isn't expressible here; the
  // proof is that print declares exactly the variable on strong and no colour.
  const print = declarations(read('styles', 'print.css'));
  assert.deepEqual(
    print.filter((d) => d.where === '@media print > .mdm-prosemirror strong')
      .map((d) => [d.prop, d.value, d.important]),
    [['--mdm-strong', 'currentColor', true]]);
  assert.deepEqual(
    print.filter((d) => d.prop === 'color' && /\bstrong\b/.test(d.where)).map((d) => d.where), [],
    'a print colour on strong outranks the document\'s own inline colour');
});

test('Obsidiminutive colours inline code and its markers through the variables', () => {
  // Both were colours it did not choose. Inline code was a rule - a
  // `.mdm-prosemirror code { color: var(--mdm-token-number) }` plus a `pre code { color:
  // inherit }` to keep fenced code out of it - because there was no variable for it. The
  // markers were worse: nothing at all, so the vendor's nord10 at 3.29:1 on this theme's
  // page, 3.07:1 on a striped row and 2.94:1 in a quote. Now both are lines in :root, the
  // two rules are gone (base.css hands fenced code back on its own), and what is pinned is
  // the values and the ABSENCE of the rules - a rule left behind would out-specify the
  // variable a contrast sweep measures, and the two could disagree for ever.
  const theme = readTheme('Obsidiminutive.css');
  const vars = rootVariables(theme);

  assert.equal(vars.get('--mdm-code-fg'), '#93c763');
  assert.equal(vars.get('--mdm-code-fg'), vars.get('--mdm-token-number'),
    'inline code is this theme\'s number green; if the palette moves, both move');
  assert.equal(vars.get('--mdm-list-marker'), '#8cafd2');
  assert.equal(vars.get('--mdm-list-marker'), vars.get('--mdm-token-property'),
    'the marker is this theme\'s own lifted steel blue, the colour its properties use');
  assert.notEqual(vars.get('--mdm-list-marker'), vendorNord10(),
    'the marker is no longer the vendor\'s blue-grey, which is the point of setting it');

  assert.deepEqual(
    declarations(theme).filter((d) => d.prop === 'color' && /\bcode$/.test(d.where))
      .map((d) => d.where), [],
    'a colour rule on code would out-specify the variable it is now supposed to read');
});

// ===== headings and links on paper =====
//
// A dark theme's heading and link colours are chosen for its dark page and can print too
// pale on white paper, so when the theme's --mdm-color-scheme is `dark` print.css pins
// their VARIABLES dark, with a style query, and keeps the dark page off paper when the
// print dialog's "Background graphics" is ticked. jsdom evaluates neither @container
// nor @layer nor var(), so this resolves declarations as the tests above do, and
// evaluates the query the way the browser does: the theme's declared value, compared
// exactly. A print condition it can't evaluate fails rather than being guessed at.
// That WebView2 does the same on paper is TEST-PLAN PRN-01.

const DARK = ['Dracula.css', 'GitHub-Dark-Dimmed.css', 'Obsidiminutive.css', ...RED_SPARKS];
const LIGHT = ['GitHub-Light.css', 'Midget-Solarized.css', 'One-Light.css', 'Solarized-Light.css'];
const HEADINGS = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6'];
const themeVariables = (themeCss) => new Map([...rootVariables(defaultTheme), ...rootVariables(themeCss)]);
/** Whether a declaration's rule lists exactly this selector. */
const names = (d, selector) => d.where.split(' > ').at(-1).split(', ').includes(selector);
/** The element each selector in a declaration's rule styles: its last compound, such as
 *  `a:hover` for `.mdm-prosemirror blockquote a:hover`. */
const subjects = (d) => d.where.split(' > ').at(-1).split(', ').map((s) => s.split(/[\s>+~]+/).at(-1));
const HEADING_OR_LINK = /^(a|h[1-6])(?![\w-])/;
/** A colour on a heading or a link itself, whatever its ancestors. */
const headingOrLink = (d) => d.prop === 'color' && subjects(d).some((s) => HEADING_OR_LINK.test(s));
/** A selector for the root element alone. */
const isRoot = (selector) => /^(:root|html)([.#:[][^\s>+~]*)?$/.test(selector);

/** The print declarations that reach paper with this theme installed over Default. */
function paperDeclarations(themeCss) {
  const vars = themeVariables(themeCss);
  return declarations(read('styles', 'print.css'), vars).flatMap((d) => {
    const parts = d.where.split(' > ');
    const applies = parts.filter((part) => part.startsWith('@')).every((at) => {
      if (at === '@media print' || at === '@page') return true;
      const query = at.match(/^@container style\((--[\w-]+):\s*([^()]*?)\s*\)$/);
      assert.ok(query, `print.css has a condition this test can't evaluate: ${at}`);
      return vars.get(query[1]) === query[2];
    });
    if (!applies) return [];
    if (!parts.some((part) => part.startsWith('@container'))) return [d];
    // A style query asks the element's parent, and the root element has none, so
    // Chromium never matches it there: a root selector inside one styles nothing.
    const kept = parts.at(-1).split(', ').filter((s) => !isRoot(s));
    return kept.length ? [{ ...d, where: [...parts.slice(0, -1), kept.join(', ')].join(' > ') }] : [];
  });
}

/** Print's own value for this selector's property, or undefined when print leaves it alone. */
function onPaper(themeCss, prop, selector) {
  const hits = paperDeclarations(themeCss).filter((d) => d.prop === prop && names(d, selector));
  assert.ok(hits.length <= 1, `${selector} { ${prop} } is set on paper ${hits.length} times`);
  return hits[0]?.value;
}

/** The last screen rule's value for the property on any of these selectors. */
const onScreen = (vars, prop, selectors) => declarations(editorCss, vars)
  .filter((d) => !d.where.startsWith('@') && d.prop === prop && selectors.some((s) => names(d, s)))
  .at(-1)?.value;

/**
 * The colour an element prints in: a colour print gives it, or else its screen rule
 * resolved with the variables print pins on it. `selectors` all match the element, least
 * specific first, so a later selector's pin wins. `currentColor` means its parent's colour.
 */
function printedColour(themeCss, selectors) {
  for (const s of [...selectors].reverse()) {
    const own = onPaper(themeCss, 'color', s);
    if (own) return own;
  }
  const vars = themeVariables(themeCss);
  for (const s of selectors) {
    for (const d of paperDeclarations(themeCss)) if (d.prop.startsWith('--') && names(d, s)) vars.set(d.prop, d.value);
  }
  return onScreen(vars, 'color', selectors);
}

/** What prints behind the text when Background graphics is ticked; unticked, it's white. */
const paperPage = (themeCss) => onPaper(themeCss, 'background', '.milkdown')
  ?? onScreen(themeVariables(themeCss), 'background', ['.milkdown']);
/** The colour scheme paper prints in, which colours the page margins. */
const paperScheme = (themeCss) => onPaper(themeCss, 'color-scheme', ':root')
  ?? onScreen(themeVariables(themeCss), 'color-scheme', [':root']);

/** WCAG contrast of two resolved #rrggbb colours; anything else fails. */
function contrast(a, b) {
  const luminance = (hex) => {
    assert.match(hex ?? '', /^#[0-9a-f]{6}$/, `${hex} is not a resolved colour`);
    const [r, g, bl] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255)
      .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
    return 0.2126 * r + 0.7152 * g + 0.0722 * bl;
  };
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

test('every built-in is sorted into dark or light by what it declares', () => {
  // So a new palette has to be placed, and a list can't disagree with its file.
  assert.deepEqual(readdirSync(builtinDir).filter((f) => f.endsWith('.css')).sort(),
    [...DARK, ...LIGHT].sort());
  for (const file of DARK) assert.equal(rootVariables(readTheme(file)).get('--mdm-color-scheme'), 'dark', file);
  for (const file of LIGHT) assert.equal(rootVariables(readTheme(file)).get('--mdm-color-scheme'), 'light', file);
});

test('a dark built-in prints every heading and its links dark enough for paper, with Background graphics on or off', () => {
  for (const file of DARK) {
    const css = readTheme(file);
    // Unticked, backgrounds are dropped and the page is white. Ticked, they print, so
    // the page and its margins must be light as well.
    const page = paperPage(css);
    assert.equal(paperScheme(css), 'light', `${file}: the margins print in a dark colour scheme`);
    const body = printedColour(css, ['.mdm-prosemirror']);
    const link = printedColour(css, ['.mdm-prosemirror a']);
    for (const ground of ['#ffffff', page]) {
      assert.ok(contrast(body, ground) >= 4.5, `${file}: body text prints ${body} on ${ground}`);
      for (const el of HEADINGS) {
        const colour = printedColour(css, [`.mdm-prosemirror ${el}`]);
        assert.ok(contrast(colour, ground) >= 4.5, `${file}: ${el} prints ${colour} on ${ground}`);
      }
      assert.ok(contrast(link, ground) >= 4.5, `${file}: a link prints ${link} on ${ground}`);
    }

    // Still a link on paper: its own dark colour, not the text around it, and print
    // leaves the underline the editor draws.
    assert.notEqual(link, body, `${file}: a link prints as body text`);
    assert.deepEqual(paperDeclarations(css)
      .filter((d) => d.prop.startsWith('text-decoration') && /(^|, )\.mdm-prosemirror (th )?a(,|$)/.test(d.where.split(' > ').at(-1)))
      .map((d) => d.where), [], `${file}: print takes the underline off links`);

    // The header row keeps its dark look on paper, where a dark link would vanish.
    const inHeader = printedColour(css, ['.mdm-prosemirror a', '.mdm-prosemirror th a']);
    const text = inHeader.toLowerCase() === 'currentcolor' ? printedColour(css, ['.mdm-prosemirror th']) : inHeader;
    const header = onPaper(css, 'background', '.mdm-prosemirror th');
    assert.ok(contrast(text, header) >= 4.5, `${file}: a link in a header row prints ${text} on ${header}`);
  }
});

test('paper pins a dark theme\'s heading and link variables, not their colours', () => {
  // As it does for bold. A colour print gives the element itself outranks a colour the
  // document writes on it - <h2 style="color:green"> printed #1f2328 - where a variable
  // only replaces the theme's. The accepted cost: a theme's own colour rule on a heading
  // or a link, not through a variable, reaches paper. No built-in writes one.
  for (const file of DARK) {
    assert.deepEqual(paperDeclarations(readTheme(file)).filter(headingOrLink).map((d) => d.where), [],
      `${file}: print colours a heading or a link itself`);
    assert.deepEqual(declarations(readTheme(file)).filter(headingOrLink).map((d) => d.where), [],
      `${file}: colours a heading or a link without a variable, which paper would keep`);
  }

  // Pinning variables is enough only while every rule of ours that colours a heading or
  // a link, with any ancestors (a link in a quote, say), reads one that paper pins on
  // every such element. Print's own rules are the checks above; screen-only rules
  // never reach paper.
  const pinned = (selector) => new Set(paperDeclarations(readTheme('Dracula.css'))
    .filter((d) => d.prop.startsWith('--') && names(d, selector)).map((d) => d.prop));
  const seen = new Set();
  for (const d of declarations(editorCss).filter((d) => !/^@media (print|screen)\b/.test(d.where) && headingOrLink(d))) {
    for (const subject of subjects(d).filter((s) => HEADING_OR_LINK.test(s))) {
      seen.add(subject);
      const pinnedOn = `.mdm-prosemirror ${subject.match(HEADING_OR_LINK)[1]}`;
      const variable = d.value.match(/^var\((--[\w-]+)\)$/)?.[1];
      assert.ok(variable && pinned(pinnedOn).has(variable),
        `${d.where} { color: ${d.value} } colours ${subject} with something paper doesn't pin on ${pinnedOn}`);
    }
  }
  for (const el of [...HEADINGS, 'a', 'a:hover']) assert.ok(seen.has(el), `no screen rule colours ${el}`);
});

test('Default and every light built-in print headings and links in their own colours, as before', () => {
  // Their colours already read on paper and are part of how they look there. Each
  // prints as its screen rule gives it, the page too, no dark-only rule reaches paper
  // at all, and paper's light colour scheme is the one they already have.
  for (const file of ['', ...LIGHT]) {
    const css = file ? readTheme(file) : '';
    const name = file || 'Default';
    const screen = themeVariables(css);
    assert.deepEqual(paperDeclarations(css).filter((d) => d.where.includes('@container'))
      .map((d) => `${d.where} { ${d.prop} }`), [], `${name}: dark-only print rules apply`);
    for (const el of [...HEADINGS, 'a']) {
      const selectors = [`.mdm-prosemirror ${el}`];
      assert.equal(printedColour(css, selectors), onScreen(screen, 'color', selectors), `${name}: ${el} prints differently`);
    }
    assert.equal(printedColour(css, ['.mdm-prosemirror a', '.mdm-prosemirror th a']),
      onScreen(screen, 'color', ['.mdm-prosemirror a']), `${name}: a link in a header prints differently`);
    assert.equal(paperPage(css), onScreen(screen, 'background', ['.milkdown']), `${name}: the page prints differently`);
    assert.equal(onScreen(screen, 'color-scheme', [':root']), 'light', `${name} is not light on screen`);
  }
});

test('a custom theme prints dark headings and links by declaring --mdm-color-scheme: dark, and only then', () => {
  const pale = '--mdm-page-bg: #202020; --mdm-heading: #eeeeee; --mdm-h4: #eeeeee; --mdm-h5: #eeeeee; --mdm-h6: #eeeeee; --mdm-link: #ddddff;';
  const dark = `:root { --mdm-color-scheme: dark; ${pale} }`;
  const page = paperPage(dark);
  for (const el of [...HEADINGS, 'a']) {
    for (const ground of ['#ffffff', page]) {
      assert.ok(contrast(printedColour(dark, [`.mdm-prosemirror ${el}`]), ground) >= 4.5, `dark custom theme: ${el} on ${ground}`);
    }
  }
  for (const css of [`:root { --mdm-color-scheme: light; ${pale} }`, `:root { ${pale} }`]) {
    assert.equal(paperPage(css), '#202020', `${css}: print changed the page`);
    for (const el of HEADINGS) assert.equal(printedColour(css, [`.mdm-prosemirror ${el}`]), '#eeeeee', `${css}: ${el}`);
    assert.equal(printedColour(css, ['.mdm-prosemirror a', '.mdm-prosemirror th a']), '#ddddff', `${css}: a link in a header`);
  }
});

// ===== list markers, inline code and text size =====
//
// --mdm-list-marker and --mdm-code-fg name two colours the editor never declared: both
// arrived from Milkdown's Nord palette, so no theme could reach them. --mdm-font-size
// names the one size the document derives from. All three are optional in the
// --mdm-strong sense — Default gives each the value the editor already produced, so a
// theme that says nothing renders exactly as before, which the baseline comparison at
// the top of this file is what actually holds.
//
// jsdom cascades neither @layer nor var(), so these tests do what the ones above do:
// resolve declarations with Default's variables and a theme's laid over them, and reason
// about specificity in numbers rather than in prose. Which ELEMENTS a selector reaches
// is a question jsdom can answer, and does below.

/** The editor's DOM as the vendor builds it: one element carries prose, mdm-prosemirror
 *  and milkdown-theme-nord (@milkdown/theme-nord/lib/index.js sets all three), and
 *  prosemirror-tables wraps every cell's content in a paragraph — which is why table
 *  text is a `p` and not the cell. Every place inline code and a list can appear. */
const DOCUMENT = `<div class="milkdown"><div class="prose mdm-prosemirror milkdown-theme-nord">
  <p>body <code id="inline">a</code></p>
  <h2>heading <code id="in-heading">b</code></h2>
  <ul><li id="item"><p>x <code id="in-item">c</code></p>
    <ul><li id="nested"><p>y</p></li></ul></li></ul>
  <ol><li id="numbered"><p>z</p></li></ol>
  <blockquote><p><code id="in-quote">d</code></p>
    <ul><li id="quoted-item"><p>q</p></li></ul></blockquote>
  <pre><code id="fenced">e <span class="token keyword" id="keyword">f</span></code></pre>
  <table><tbody>
    <tr><th><p><code id="in-header">g</code></p></th></tr>
    <tr><td><p><code id="in-cell">h</code></p></td></tr>
  </tbody></table>
</div></div>`;

const editorDom = () => new JSDOM(DOCUMENT).window.document;
const reaches = (doc, selector) => [...doc.querySelectorAll(selector)].map((el) => el.id).sort();
/** The one declaration in base.css whose value is this var(), and its rule. */
function soleSite(file, value) {
  const hits = declarations(read('styles', file)).filter((d) => d.value === value);
  assert.equal(hits.length, 1, `${file} should have exactly one ${value}, has ${hits.length}`);
  return hits[0];
}

test('--mdm-list-marker colours every list marker, nested or quoted, and nothing else', () => {
  const rule = soleSite('base.css', 'var(--mdm-list-marker)');
  assert.equal(rule.prop, 'color');

  // Every selector in it is a ::marker on a list's own item, so the declaration cannot
  // touch the item's text, a paragraph or a cell — and `> li` keeps it off a list
  // nested inside an item's paragraph structure.
  const selectors = rule.where.split(', ');
  for (const s of selectors) assert.match(s, /^\.mdm-prosemirror (ol|ul) > li::marker$/, s);

  // jsdom has no ::marker in querySelectorAll, and it doesn't need one: the pseudo
  // belongs to the element the rest of the selector names, so the owners are the
  // question. All four lists, including the nested one and the one in a quote.
  const owners = selectors.map((s) => s.replace('::marker', '')).join(', ');
  assert.deepEqual(reaches(editorDom(), owners), ['item', 'nested', 'numbered', 'quoted-item']);
});

test('--mdm-code-fg colours inline code in quotes, tables, lists and headings, not fenced code', () => {
  const inline = soleSite('base.css', 'var(--mdm-code-fg)');
  assert.equal(inline.prop, 'color');
  assert.equal(inline.where, '.mdm-prosemirror code');

  const doc = editorDom();
  assert.deepEqual(reaches(doc, inline.where),
    ['fenced', 'in-cell', 'in-header', 'in-heading', 'in-item', 'in-quote', 'inline']);

  // Fenced code is handed back to the block's own colour, in the SAME layer at a higher
  // specificity, so of the seven the variable paints the six that are inline.
  const back = declarations(read('styles', 'base.css'))
    .filter((d) => d.prop === 'color' && d.value === 'inherit' && d.where.includes('pre code'));
  assert.equal(back.length, 1, 'one rule hands fenced code back');
  assert.deepEqual(reaches(doc, back[0].where), ['fenced']);

  // ...and the syntax tokens are more specific still, so highlighting is untouched.
  assert.equal(declarations(read('styles', 'base.css'))
    .filter((d) => d.prop === 'color' && d.where.includes('.token.keyword')).length, 1);
  assert.deepEqual(reaches(doc, '.mdm-prosemirror .token.keyword'), ['keyword']);
});

/** The value a size site resolves to with this theme's variables over Default's. */
function sizeOf(themeCss, where, prop) {
  const hits = declarations(read('styles', 'structure.css'), themeVariables(themeCss))
    .filter((d) => d.where === where && d.prop === prop);
  assert.equal(hits.length, 1, `structure.css ${where} { ${prop} } matched ${hits.length}`);
  return hits[0].value;
}

/** The sites that read the variable, and what each is a multiple of. The two --text-*
 *  are the VENDOR's own tokens, redefined on `:root`: its `p` rule and its `pre` rule
 *  read them in `rem`, and this is what makes both follow the document instead. */
const DERIVED = [
  ['.mdm-prosemirror', 'font-size', 1],
  [':root', '--text-base', 1],
  [':root', '--text-sm', 0.875],
  ['.mdm-prosemirror td, .mdm-prosemirror th', 'font-size', 0.75],
  ['.mdm-prosemirror [data-line]::before', 'font-size', 0.75],
  ['.mdm-prosemirror [data-line]::before', 'line-height', 1.5],
];
/** The sites that must FOLLOW the container, in `em`. A `rem` here is the vendor's trap. */
const RELATIVE = [
  ...['h1', 'h2', 'h3', 'h4', 'h5', 'h6'].map((el) => `.mdm-prosemirror ${el}`),
  '.mdm-prosemirror code', '.mdm-prosemirror pre code',
];

test('--mdm-font-size set once scales body text, headings, markers, code, tables and the gutter', () => {
  // One line, and the theme needs nothing else — which is the claim, so the theme is
  // written out in full here rather than described.
  const theme = ':root { --mdm-font-size: 32px; }';

  for (const [where, prop, multiple] of DERIVED) {
    assert.equal(sizeOf(theme, where, prop),
      multiple === 1 ? '32px' : `calc(32px * ${multiple})`, `${where} { ${prop} }`);
    // Default, for the same sites, is the arithmetic that leaves today's rendering
    // alone: 16, 14, 12, 12 and a 24px line box.
    assert.equal(sizeOf('', where, prop), multiple === 1 ? '16px' : `calc(16px * ${multiple})`);
  }

  // Nothing else has to be set, because no derivation reads a second variable.
  for (const [where, prop] of DERIVED) {
    const raw = declarations(read('styles', 'structure.css'))
      .filter((d) => d.where === where && d.prop === prop)[0].value;
    assert.deepEqual([...new Set([...raw.matchAll(/var\(\s*(--[\w-]+)/g)].map((m) => m[1]))],
      ['--mdm-font-size'], `${where} { ${prop} } reads more than the one variable`);
  }

  // The headings and both code sizes carry no variable at all: they are `em`, so they
  // follow the container. A `rem` or a px here is exactly the vendor bug this fixes —
  // rem measures from the PAGE root, which no container font-size reaches.
  for (const where of RELATIVE) {
    const value = sizeOf('', where, 'font-size');
    assert.match(value, /^[\d.]+em$/, `${where} is sized ${value}, which does not follow the document`);
  }

  // List markers take the container's size by having none of their own, so nothing of
  // ours may give a marker or a list item a font-size.
  const sized = declarations(read('styles', 'structure.css'))
    .filter((d) => d.prop === 'font-size' && /(::marker|\bli\b)/.test(d.where));
  assert.deepEqual(sized.map((d) => d.where), []);
});

test('a theme can still scale a rem document itself, whether it sets the tokens on :root or on the editor', () => {
  // Two existing ways to scale a document predate --mdm-font-size, because redefining the
  // vendor's own two `rem` tokens was the only seam that ever worked. Both have to keep
  // working, and each fails differently if this is got wrong:
  //
  //   a rule of OURS on `p`/`pre`   beats a theme's token at any specificity, because ours
  //                                 would sit in mdm-structure and theirs in mdm-theme.
  //                                 Measured on the theme this came from: 32 computed
  //                                 declarations back at the vendor's rem sizes.
  //   ours on `.mdm-prosemirror`    beats a theme's `:root` token WITHOUT the cascade being
  //                                 consulted at all: a custom property is inherited per
  //                                 element, and the nearer ancestor's declaration is the
  //                                 one the subtree inherits. Layer order never gets a
  //                                 vote. Measured: 217 differing declarations on screen,
  //                                 216 on paper, for `:root { --text-base; --text-sm }`.
  //
  // So ours goes on `:root`: a theme's `:root` beats it by layer order, and a theme's
  // `.mdm-prosemirror` beats it by proximity. Anything deeper than `:root` breaks the
  // second case, and `!important` breaks both.
  const ours = declarations(read('styles', 'structure.css'));
  assert.deepEqual(
    ours.filter((d) => d.prop === 'font-size' && /^\.mdm-prosemirror (p|pre)$/.test(d.where))
      .map((d) => d.where), [],
    'a font-size of ours on p or pre outranks a theme that scales the vendor tokens');

  for (const token of ['--text-base', '--text-sm']) {
    const hits = declarations(editorCss).filter((d) => d.prop === token);
    assert.equal(hits.length, 1, `${token} should be redefined exactly once, is ${hits.length}`);
    assert.equal(hits[0].where, ':root',
      `${token} is redefined on ${hits[0].where}; anything nearer than :root is inherited ` +
      'by the document before a theme\'s own :root declaration can be considered');
    assert.equal(hits[0].important, false, `${token} must not be !important, or a theme can't win`);
  }
});

test('--mdm-font-size has to be declared on :root, and both files a theme author reads say so', () => {
  // The other side of deriving the two tokens once, on :root: a theme that declares
  // --mdm-font-size on `.mdm-prosemirror` instead gets HALF a document, silently.
  // Measured at 32px: headings 64px, list markers 32, cells 24 and the gutter 24 all
  // follow, while body text stays 16, inline code 14.72 and fenced code 14 — because the
  // two tokens are read at :root, where that theme's value never arrives.
  //
  // No CSS shape fixes it without giving back something worse: `:root, .mdm-prosemirror`
  // and `*` both reinstate the regression this scope exists to prevent, and
  // `--text-base: 1em` changes Default's table cells. So it is documented and pinned,
  // which needs saying twice over — main.js's probe comment tells theme authors that
  // `.mdm-prosemirror` is a fine place for their variables, and for this one it is not.
  //
  // The scope split IS the mechanism, so it is what this pins — read out of the CSS that
  // ships rather than out of a table in this file, which could only ever agree with
  // itself. Two scopes: the tokens at :root, everything else inside the document. Collapse
  // them either way and the placement stops mattering, which would make the two documents
  // below wrong.
  const scopes = new Set(declarations(editorCss)
    .filter((d) => d.value.includes('var(--mdm-font-size'))
    .map((d) => (d.where === ':root' ? ':root' : 'inside the document')));
  assert.deepEqual([...scopes].sort(), [':root', 'inside the document']);

  // Said next to the variable, and both placements said TOGETHER: the window is short
  // enough that the two names have to be in one passage rather than anywhere in the file.
  // Two facts rather than an English sentence, so rewording the prose doesn't fail the
  // build, while deleting the passage or moving it away from the variable still does —
  // all four checked by mutation. 900 characters and either name alone was vacuous: a
  // theme file mentions `:root` and `.mdm-prosemirror` all over it.
  for (const [name, text] of [['sample.css', sampleCss], ['HELP.md', helpMd]]) {
    const mentions = [...text.matchAll(/--mdm-font-size/g)].map((m) => m.index);
    assert.ok(mentions.length, `${name} no longer mentions --mdm-font-size at all`);
    assert.ok(
      mentions.some((at) => [':root', '.mdm-prosemirror']
        .every((needed) => text.slice(at, at + 300).includes(needed))),
      `${name} introduces --mdm-font-size without saying, in the same breath, that it ` +
      'belongs in :root and what happens on .mdm-prosemirror');
  }
});

test('a theme that only moves the page root\'s font-size no longer scales the document, by design', () => {
  // The cost of deriving the vendor's `rem` sizes from a variable, accepted rather than
  // fixed: `html { font-size: 20px }` in a theme used to scale body text and fenced code
  // (1rem and .875rem measured from the page root) while leaving the container, the
  // headings and the markers at 16px. Now it scales none of the document. That partial
  // effect was never documented, never tested and not a shape anyone should want - a
  // theme that wants a bigger document says --mdm-font-size once and gets all of it -
  // and no scoping of ours can restore it, because the document's sizes are now absolute
  // lengths off that variable. sample.css and HELP say so.
  //
  // Recorded as a test rather than a comment so that reintroducing a rem-sized
  // declaration - the only way the old behaviour comes back, and it would come back
  // partially again - fails here.
  const sizes = declarations(editorCss).filter((d) => /^(font-size|--text-)/.test(d.prop));
  assert.ok(sizes.length > 0, 'no size declarations found at all; this test has stopped looking');
  assert.deepEqual(sizes.filter((d) => /\d\s*rem\b/.test(d.value)).map((d) => `${d.where} { ${d.prop} }`), [],
    'a rem-sized declaration measures from the page root again, which scales half a document');
  assert.deepEqual(declarations(editorCss)
    .filter((d) => d.prop === 'font-size' && /^(html|:root)\b/.test(d.where)).map((d) => d.where), [],
    'nothing of ours sets the page root\'s own font-size');
});

test('--mdm-font-size is registered as a length, so a value that is not one degrades to 16px', () => {
  // Unregistered, a custom property is only tokens and a bad value is invalid at
  // computed-value time everywhere it is USED - which left the document looking fine and
  // everything derived from it broken. Measured with `banana`: fenced code 16px (from 14),
  // `pre code` 14px (from 12.25), cell text 12px (from 16), every gutter number 16px (from
  // 12). Typed, a non-length is invalid at the DECLARATION, which with inherits:true on
  // :root means the initial value - 16px - everywhere, and `em`/`%` resolve once here
  // instead of compounding (`2em` gave 64px body text). A negative length is still a
  // length and still clamps code and the gutter to 0px: sample.css says don't, nothing
  // stops it, and a syntax narrow enough to exclude it excludes calc() too.
  assert.deepEqual(
    declarations(read('styles', 'structure.css'))
      .filter((d) => d.where === '@property --mdm-font-size').map((d) => [d.prop, d.value]),
    [['syntax', '"<length>"'], ['inherits', 'true'], ['initial-value', '16px']]);
});

test('a table cell\'s text is the body size, and only the cell BOX is 0.75 of it', () => {
  // The one place this derivation does not reproduce what the custom double-size theme it
  // came from produced, so it is written down rather than left to be rediscovered. That
  // theme pinned `table` AND `table p`, so its cell TEXT was 24px at 2X. Here the cell BOX
  // is 0.75 of the document and the text inside it is a paragraph, which follows the
  // vendor's --text-base like every other paragraph: 32px at 2X, 16px at the default.
  //
  // That is not a gap to close. A `th > p, td > p` font-size of ours would match the old
  // theme and break the premise the whole contract rests on: at the default size it would
  // take every theme's cell text from 16px to 12px - a visible change to seven palettes
  // that set nothing at all. Cell text has always been the body size; it still is.
  const ours = declarations(read('styles', 'structure.css'));

  assert.deepEqual(
    ours.filter((d) => (d.prop === 'font-size' || d.prop === 'font')
      && /\b(th|td)\b[^,]*>\s*p\b/.test(d.where)).map((d) => d.where), [],
    'a font-size on a cell\'s paragraph would shrink every theme\'s cell text to 0.75');

  // What sizes it is the VENDOR's own `p` rule, reading the token the document redefines -
  // so it is read out of the built bundle, as vendorNord10() reads the palette, and not out
  // of our five layers, which is where the first draft of this test looked and found
  // nothing. A vendor bump that stops sizing paragraphs from --text-base fails here.
  const bundle = readFileSync(
    join(here, '..', '..', 'src', 'MarkdownMidget', 'wwwroot', 'editor.bundle.css'), 'utf8');
  assert.deepEqual(
    declarations(bundle).filter((d) => d.prop === 'font-size'
      && d.where === '@layer mdm-vendor > .milkdown-theme-nord p').map((d) => d.value),
    ['var(--text-base)'],
    'the vendor no longer sizes paragraphs from --text-base; cell text may have moved with it');

  // So at both sizes, measured through the token rather than through a selector, which is
  // what keeps this true if --text-base is ever declared somewhere else.
  for (const [size, theme] of [['16px', ''], ['32px', ':root { --mdm-font-size: 32px; }']]) {
    const resolved = declarations(read('styles', 'structure.css'), themeVariables(theme));
    const token = resolved.filter((d) => d.prop === '--text-base');
    assert.equal(token.length, 1, '--text-base is redefined exactly once');
    assert.equal(token[0].value, size, `a cell's paragraph at --mdm-font-size: ${size}`);

    const box = resolved.filter((d) => d.prop === 'font-size'
      && /\b(th|td)\b/.test(d.where) && !/>\s*p\b/.test(d.where));
    assert.equal(box.length, 1, 'one rule sizes the cell box');
    assert.equal(box[0].value, `calc(${size} * 0.75)`, `the cell box at --mdm-font-size: ${size}`);
  }
});

test('a diagram\'s labels take the size mermaid drew their boxes for, never the document\'s', () => {
  // Mermaid 11 puts each HTML label in a <p> inside the SVG's foreignObject, measures it
  // in a scratch element outside the document, and sizes the box to fit. Inside the
  // editor the vendor's `.milkdown-theme-nord p` reaches that <p> too, and its size is
  // --text-base, which IS the document's size: at 32px the text was twice its box. So
  // the label inherits from the SVG, which is what it did while mermaid measured it.
  const doc = new JSDOM(DOCUMENT.replace(/<\/div><\/div>$/, `
    <div class="mdm-mermaid" contenteditable="false"><svg><g class="node"><foreignObject>
      <div><span class="nodeLabel"><p id="label">Aerial</p></span></div>
    </foreignObject></g></svg></div>
  </div></div>`)).window.document;
  assert.ok(reaches(doc, '.milkdown-theme-nord p').includes('label'), 'the premise: the vendor\'s paragraph rule reaches a label');

  const ours = declarations(read('styles', 'structure.css'))
    .filter((d) => d.prop === 'font-size' && d.where.includes('.mdm-mermaid'));
  assert.deepEqual(ours.map((d) => [d.where, d.value, d.important]), [['.mdm-mermaid p', 'inherit', true]]);
  // The label, and not one paragraph of the document around it (theirs have no id).
  assert.deepEqual(reaches(doc, ours[0].where), ['label']);
  // !important because a theme is a later layer: its own paragraph size would otherwise
  // reach the label the same way the vendor's did. It is the one font-size of ours on a
  // `p`; "a theme can still scale a rem document itself" forbids one on the document's
  // paragraphs, for the same reason in reverse.

  // Inert where nothing was wrong: at Default's size the vendor gave a label the same
  // size mermaid is now handed, so no theme at 16px draws a diagram differently.
  const size = rootVariables(defaultTheme).get('--mdm-font-size');
  assert.equal(sizeOf('', ':root', '--text-base'), size);
  assert.equal(mermaidConfig(mermaidLook('default', size)).themeVariables.fontSize, size);
});

test('the numbers beside blank lines keep their own size', () => {
  // The one gutter number that cannot follow the variable, for the reason structure.css
  // gives beside it: its box has to fit the fixed 16px margin between two blocks, and its
  // negative margin-top IS its line box. Pinned so "scale everything" can't quietly be
  // extended to it without moving the block margins too.
  const gap = declarations(read('styles', 'structure.css'))
    .filter((d) => d.where === '.mdm-prosemirror [data-gap]::before');
  assert.deepEqual(gap.filter((d) => d.prop === 'font-size' || d.prop === 'line-height'), []);
  assert.deepEqual(gap.filter((d) => d.prop === 'margin-top').map((d) => d.value), ['-12px']);
  assert.match(gap.find((d) => d.prop === 'font').value, /\b12px\/12px\b/);
});

/** Nord's own nord10, read out of the built bundle rather than copied: it is the value
 *  Default has to keep reproducing, so a vendor bump has to show up as a failure here
 *  and not as inline code quietly changing colour for every theme. */
function vendorNord10() {
  const bundle = readFileSync(
    join(here, '..', '..', 'src', 'MarkdownMidget', 'wwwroot', 'editor.bundle.css'), 'utf8');
  const m = bundle.match(/--color-nord10:\s*(#[0-9a-f]{6})/i);
  assert.ok(m, 'the vendor no longer defines --color-nord10; Default\'s inert values need rechecking');
  return m[1].toLowerCase();
}

test('the three new variables are inert: only the palettes written for them set one, and Default is what the editor already drew', () => {
  const optional = ['--mdm-list-marker', '--mdm-code-fg', '--mdm-font-size'];
  for (const file of ['', ...readdirSync(builtinDir).filter((f) => f.endsWith('.css'))]) {
    const vars = rootVariables(file ? readTheme(file) : '');
    // Three cases, not two. Red Sparks was written against this contract and sets all
    // three. Obsidiminutive sets the two COLOURS and not the size: its markers were the
    // vendor's nord10 at 3.29:1 on its own page and its inline code was a rule, while 16px
    // is the size it has always rendered at. Everything else, Default included, renders
    // exactly as it did before the variables existed, and a file that dropped one of its
    // own would need a rule back to look the same.
    const sets = RED_SPARKS.includes(file) ? optional
      : file === 'Obsidiminutive.css' ? ['--mdm-list-marker', '--mdm-code-fg']
      : [];
    for (const name of optional)
      assert.equal(vars.has(name), sets.includes(name),
        `${file || 'Default'}: ${name} is ${sets.includes(name) ? 'unset' : 'set'}`);
  }

  // Both colours are the vendor's marker/inline-code colour, and the size is the 16px
  // .mdm-prosemirror has always carried. BuiltInThemeTests holds the same three values
  // from the other side, where the "optional" exemption is granted.
  const vars = rootVariables(defaultTheme);
  assert.equal(vars.get('--mdm-list-marker'), vendorNord10());
  assert.equal(vars.get('--mdm-code-fg'), vendorNord10());
  assert.equal(vars.get('--mdm-font-size'), '16px');
});

test('paper keeps the list marker it has always printed, and pins the variable to do it', () => {
  // A marker colour is chosen against the theme's own page; on white paper a pale one is
  // a smudge. So print pins the VARIABLE — as it does for bold — to the literal every
  // marker printed in before the variable existed, which is Nord's. The theme's palette
  // stops at the paper; a theme's own ::marker rule still reaches it.
  const print = declarations(read('styles', 'print.css'));
  assert.deepEqual(
    print.filter((d) => d.prop === '--mdm-list-marker').map((d) => [d.where, d.value, d.important]),
    [['@media print > .mdm-prosemirror', vendorNord10(), true]]);
  assert.deepEqual(print.filter((d) => d.prop === 'color' && d.where.includes('::marker')), [],
    'a print colour ON the marker would outrank a theme\'s own rule as well');

  // Every built-in and Default print the marker they print today, because none sets it
  // and print pins Default's value anyway.
  for (const file of ['', ...readdirSync(builtinDir).filter((f) => f.endsWith('.css'))]) {
    const vars = themeVariables(file ? readTheme(file) : '');
    for (const d of paperDeclarations(file ? readTheme(file) : '')) if (d.prop === '--mdm-list-marker') vars.set(d.prop, d.value);
    assert.equal(vars.get('--mdm-list-marker'), vendorNord10(), file || 'Default');
  }
});

test('paper colours inline code itself, so --mdm-code-fg cannot reach it', () => {
  // Unlike the marker, this one needs nothing new: print has always stated both ends of
  // the inline-code pair as its own literals, which outrank anything later at any
  // specificity. The test is that it still does — deleting that colour would hand paper
  // to every theme's screen palette.
  const code = declarations(read('styles', 'print.css'))
    .filter((d) => d.where === '@media print > .mdm-prosemirror code');
  assert.deepEqual(code.map((d) => [d.prop, d.value, d.important]),
    [['background', '#f6f8fa', true], ['color', '#24292e', true]]);
});

test('paper takes the document\'s text size from the theme, and keeps its own for the source view', () => {
  // The deliberate asymmetry, stated in print.css's header: colour is pinned on paper
  // because a dark page is unreadable there, and size is not, because large type is what
  // a large-type theme was chosen for. So print declares no size on the document...
  const print = declarations(read('styles', 'print.css'));
  const document = ['.mdm-prosemirror', '.mdm-prosemirror p', '.mdm-prosemirror pre',
    '.mdm-prosemirror code', '.mdm-prosemirror td, .mdm-prosemirror th'];
  assert.deepEqual(
    print.filter((d) => (d.prop === 'font-size' || d.prop === 'font' || d.prop === '--mdm-font-size')
      && document.some((s) => d.where.endsWith(s))).map((d) => `${d.where} { ${d.prop} }`), []);

  // ...and its own printout, which is not in the document, still states 10pt.
  assert.deepEqual(print.filter((d) => d.where === '@media print > .mdm-print-source-pre' && d.prop === 'font-size')
    .map((d) => [d.value, d.important]), [['10pt', true]]);
});

// ===== the README's theme list =====
//
// HELP's count and its table are both pinned in BuiltInThemeTests, because HELP is an
// embedded resource the test assembly can read. The README is not embedded and had no
// pin at all, so the same drift - a palette added, the docs left saying eight - could
// still land there. It is checked here instead, where the repository's own files are
// already what the tests read.

test('the README\'s theme count and list name every built-in that ships', () => {
  const readme = readFileSync(join(here, '..', '..', 'README.md'), 'utf8');
  const files = readdirSync(builtinDir).filter((f) => f.endsWith('.css')).sort();

  // The count is of THEMES, which is one more than the files: Default has no file.
  const words = ['zero', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight',
    'nine', 'ten', 'eleven', 'twelve'];
  const claim = readme.match(/\*\*Themes\*\* — ([a-z]+) built in/);
  assert.ok(claim, 'the README no longer says "**Themes** — N built in"');
  assert.equal(claim[1], words[files.length + 1],
    `the README says ${claim[1]} themes; ${files.length} files ship, plus Default`);

  // And every one is named, as the menu names it: `GitHub-Light.css` is "GitHub Light".
  //
  // Matched as a whole name, not as a substring, which is the same trap the body-text
  // exemption fell into from the other direction: `includes('Red Sparks')` is satisfied by
  // "Red Sparks 2X", so dropping the base theme from the list would have passed while the
  // README named only its double-size sibling. The boundary is the next character: end of
  // string, or anything that is not a letter, digit or space.
  const display = (f) => f.replace(/\.css$/, '').replace(/[-_]/g, ' ');
  const all = [...files.map(display), 'Default'];
  // An occurrence counts when it is not the middle of a longer word ("One Lighthouse" is
  // not "One Light") and is not the opening of a LONGER theme's name, which is the case
  // that matters here: the README says "Red Sparks and Red Sparks 2X", so "Red Sparks" is
  // named in its own right, while a README that had dropped it and kept only the 2X would
  // offer no occurrence that isn't the start of "Red Sparks 2X".
  const names = (name) => {
    const longer = all.filter((other) => other !== name && other.startsWith(name));
    for (let at = readme.indexOf(name); at !== -1; at = readme.indexOf(name, at + 1)) {
      const after = readme[at + name.length];
      if (after !== undefined && /[A-Za-z0-9]/.test(after)) continue;
      if (longer.some((other) => readme.startsWith(other, at))) continue;
      return true;
    }
    return false;
  };
  const missing = all.filter((name) => !names(name));
  assert.deepEqual(missing, [], 'themes that ship but the README does not name');
});
