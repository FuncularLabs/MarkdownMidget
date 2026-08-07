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

const editorCss = read('styles', 'editor.css');
const defaultTheme = read('styles', 'theme-default.css');
const baseline = JSON.parse(read('test', 'fixtures', 'editor-css-baseline.json'));

test('the default theme resolves to the palette that shipped before it existed', () => {
  const resolved = declarations(editorCss, rootVariables(defaultTheme));

  assert.equal(resolved.length, baseline.length,
    'a declaration was added or lost; this refactor may only rename values');

  for (let i = 0; i < baseline.length; i++) {
    assert.deepEqual(resolved[i], baseline[i],
      `declaration ${i} changed:\n  was ${JSON.stringify(baseline[i])}\n  now ${JSON.stringify(resolved[i])}`);
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
