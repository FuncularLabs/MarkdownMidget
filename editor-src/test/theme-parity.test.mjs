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
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { declarations, rootVariables } from './css-declarations.mjs';

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
  const now = byKey(declarations(editorCss, rootVariables(defaultTheme)));
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
  const unresolved = declarations(editorCss).filter((d) => !d.where.includes('@media print'));

  // Anchored on a distinguishing selector fragment rather than the whole sorted
  // list, so reordering a selector doesn't fail the test — but the fragment must
  // still identify exactly one declaration, or the pin isn't pinning anything.
  const wiring = [
    ['.milkdown {', 'background', '--mdm-page-bg'],
    ['.mdm-prosemirror td {', 'background', '--mdm-td-bg'],
    ['tr:nth-child(odd) td {', 'background', '--mdm-row-alt-bg'],
    ['.mdm-prosemirror th {', 'background', '--mdm-th-bg'],
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
  ];

  for (const [marker, prop, expected] of wiring) {
    const matches = unresolved.filter(
      (d) => (d.where + ' {').includes(marker) && d.prop === prop);
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
  const orphans = [...rootVariables(defaultTheme).keys()].filter((v) => !used.has(v));

  assert.deepEqual(orphans, [], 'defined by the theme but read by nothing');
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
