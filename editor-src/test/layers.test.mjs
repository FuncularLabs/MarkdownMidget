// The cascade order, checked against the artifact that ships rather than the
// source that describes it.
//
// Every way this arrangement breaks is silent. A layer name that appears only in an
// `@import layer()` call lands at the END of the order instead of erroring, so one
// typo moves chrome behind the theme and its !important protection evaporates. A
// `@layer` statement written after the imports parses fine and does nothing,
// because positions are already fixed by first appearance. Vendor CSS left
// unlayered outranks every layer for normal declarations, which turns the theme
// variables off. And esbuild will happily emit a stylesheet imported both layered
// and unlayered twice, once in each context.
//
// None of those change the source in a way a reader would notice. All of them are
// visible in the bundle.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const bundle = readFileSync(
  join(here, '..', '..', 'src', 'MarkdownMidget', 'wwwroot', 'editor.bundle.css'), 'utf8');
const entry = readFileSync(join(here, '..', 'styles', 'bundle.css'), 'utf8');

const EXPECTED = ['mdm-print', 'mdm-override', 'mdm-vendor', 'mdm-chrome',
                  'mdm-structure', 'mdm-base', 'mdm-theme'];

/**
 * Which of our layers a byte offset falls inside, by tracking braces.
 *
 * Strings and comments are blanked first, because this counts every brace it sees
 * and a brace inside a quoted value desynchronises it permanently — after which
 * `layerAt` returns a stale name forever and every check built on it silently
 * stops checking. That is not hypothetical: Nord's stylesheet is Tailwind v4
 * output, and Tailwind emits `content-['\{']` as `--tw-content:'{'` for a class
 * anyone can write. Blanking rather than deleting keeps every offset intact.
 */
const scannable = (css) => css
  .replace(/\/\*[\s\S]*?\*\//g, (m) => ' '.repeat(m.length))
  .replace(/"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'/g, (m) => ' '.repeat(m.length));

function scan(css, index) {
  const text = scannable(css);
  const stack = [];
  let depth = 0;
  const re = /@layer\s+([\w-]+)\s*\{|\{|\}/g;
  let m;
  while ((m = re.exec(text)) && m.index < index) {
    if (m[1]) { stack.push({ name: m[1], depth }); depth++; }
    else if (m[0] === '{') depth++;
    else { depth--; if (stack.length && stack[stack.length - 1].depth === depth) stack.pop(); }
  }
  return { name: stack.length ? stack[stack.length - 1].name : null, depth, stack };
}

const layerAt = (css, index) => scan(css, index).name;

test('the brace tracker these tests rely on is not desynchronised', () => {
  // Everything below asks `layerAt` where something sits. If its brace counting is
  // off by even one, it answers confidently and wrongly — a layer stays on the
  // stack forever, nothing is ever reported as top-level, and the typo guard in
  // particular passes no matter what. So check the arithmetic itself first: over
  // the whole file, every brace must close.
  const end = scan(bundle, bundle.length + 1);
  assert.equal(end.depth, 0, 'unbalanced braces — layerAt cannot be trusted on this bundle');
  assert.deepEqual(end.stack, [], 'a layer never closed');
});

test('the declared order is the first thing in the bundle', () => {
  // Not merely present: first. A statement after the imports is inert, and inert
  // looks exactly like working until something needs to win.
  const statement = bundle.match(/^\s*@layer\s+([^;{]+);/);
  assert.ok(statement, 'no @layer statement at the top of the bundle');
  assert.deepEqual(statement[1].split(',').map((s) => s.trim()), EXPECTED);
});

test('every layer the bundle actually uses was declared in that statement', () => {
  // The typo guard. `layer(mdm-cchrome)` creates a real layer in last position and
  // reports nothing; chrome's !important protection is simply gone.
  //
  // Matched on nesting, not on the `mdm-` prefix. A prefix filter only catches
  // typos that keep the prefix — `layer(mdm_chrome)` (one keystroke) and
  // `layer(chrome)` both slip past it, and both cost 61 computed declarations under
  // a hostile theme: squiggle destroyed, every mermaid frame hidden, the hidden
  // mermaid source revealed. Top-level layers are exactly ours; the vendor's own
  // `properties`/`theme`/`base`/`utilities` are nested inside mdm-vendor and so are
  // excluded by construction rather than by guessing at names.
  const topLevel = new Set(
    [...bundle.matchAll(/@layer\s+([\w-]+)\s*\{/g)]
      .filter((m) => layerAt(bundle, m.index) === null)
      .map((m) => m[1]));

  // Equality, not subset. A subset check only notices NEW names, so re-pointing an
  // existing import — `layer(mdm-chrome)` to `layer(mdm-theme)` — introduces no new
  // name and passes, while chrome moves behind the theme and loses every one of its
  // guarantees: 106 differing computed declarations under a hostile theme, squiggle
  // and mermaid frame both gone.
  // Every declared layer has a block in the bundle except mdm-theme, which is empty
  // until a theme is injected at runtime — so that one, and only that one, may be
  // absent. Naming it rather than allowing any gap is the point.
  assert.deepEqual([...topLevel].sort(), EXPECTED.filter((n) => n !== 'mdm-theme').sort());

  // ...and equality of the name set still doesn't say the right FILE went into each
  // layer. One marker per layer, chosen so it appears nowhere else.
  const markers = [
    ['mdm-print', '@media print'],
    ['mdm-override', 'padding-inline:6px'],
    ['mdm-vendor', '.ProseMirror-separator'],
    ['mdm-chrome', 'text-decoration-skip-ink'],
    ['mdm-structure', 'max-width:var(--mdm-page-width'],
    ['mdm-base', '--mdm-token-comment'],
  ];
  for (const [layer, marker] of markers) {
    const at = bundle.indexOf(marker);
    assert.notEqual(at, -1, `marker "${marker}" for ${layer} is not in the bundle`);
    assert.equal(layerAt(bundle, at), layer, `"${marker}" should identify ${layer}`);
  }
});

test('the vendor CSS is inside mdm-vendor, not loose', () => {
  // Anything unlayered beats every layer for normal declarations. Leave Milkdown's
  // Nord theme alone and the theme variables stop applying — measured at 105
  // differing computed declarations, including a light background behind near-white
  // code text and a mermaid frame stripped of its border and padding.
  // Markers that only the vendor emits. `.milkdown-theme-nord` alone is no good —
  // our own override rule names it too, and matches first.
  for (const marker of ['.ProseMirror-separator', '.milkdown-theme-nord pre']) {
    const at = bundle.indexOf(marker);
    assert.notEqual(at, -1, `${marker} is not in the bundle at all`);
    assert.equal(layerAt(bundle, at), 'mdm-vendor', `${marker} is not in mdm-vendor`);
  }

  // And exactly once — esbuild emits a stylesheet imported both ways twice.
  const vendorBlocks = [...bundle.matchAll(/@layer mdm-vendor\s*\{/g)].length;
  assert.ok(vendorBlocks >= 1 && vendorBlocks <= 2,
    `expected the two vendor imports, found ${vendorBlocks} blocks`);
});

test('the rule that must beat a vendor !important is in the first of our layers', () => {
  // Nord sets padding-block/padding-inline !important on its td/th at (0,2,1). Ours
  // wins today by out-specifying at (0,3,1) — but layered !important sorts by layer
  // before specificity, so once the vendor is in a layer this rule has to be in an
  // earlier one. Measured: leaving it behind changes 13 computed declarations, all
  // table-cell padding, 1px/6px becoming 12px/24px.
  const at = bundle.indexOf('padding-inline:6px');
  assert.notEqual(at, -1, 'the table-cell padding rule is gone');
  assert.equal(layerAt(bundle, at), 'mdm-override');
});

test('print is the earliest layer, so nothing a theme writes reaches paper', () => {
  assert.equal(EXPECTED[0], 'mdm-print');
  const at = bundle.indexOf('@media print');
  assert.notEqual(at, -1);
  assert.equal(layerAt(bundle, at), 'mdm-print');
});

test('chrome sits before structure, base and theme', () => {
  // The direction that matters is !important, which flows earliest-first: chrome's
  // squiggles and formatting marks cannot be removed by anything later.
  const i = (n) => EXPECTED.indexOf(n);
  assert.ok(i('mdm-chrome') < i('mdm-structure'));
  assert.ok(i('mdm-chrome') < i('mdm-base'));
  assert.ok(i('mdm-chrome') < i('mdm-theme'));
  // ...while for normal declarations the order runs the other way, so the theme
  // still wins every ordinary colour.
  assert.equal(EXPECTED[EXPECTED.length - 1], 'mdm-theme');
});

test('nothing of ours is left unlayered', () => {
  // A single stray rule outside a layer would outrank all of them.
  const ours = [...bundle.matchAll(/\.mdm-[\w-]+/g)];
  const loose = ours.filter((m) => layerAt(bundle, m.index) === null);
  assert.deepEqual(loose.map((m) => m[0]).slice(0, 5), []);
});

test("Tailwind's @property registrations survive being nested in a layer", () => {
  // They are not cascaded, so layering does not affect them — but dropping them
  // would break the vendor's custom-property fallbacks, and a wrapping scheme that
  // silently ate them would look identical here otherwise.
  assert.equal([...bundle.matchAll(/@property/g)].length, 23);
});

test('the entry file imports each of our stylesheets exactly once', () => {
  // Importing one of these from main.js as well would emit part of it unlayered,
  // with no warning from the build.
  for (const name of ['override', 'chrome', 'structure', 'base', 'theme-default', 'print']) {
    const hits = [...entry.matchAll(new RegExp(`\\./${name}\\.css`, 'g'))].length;
    assert.equal(hits, 1, `${name}.css is imported ${hits} times`);
  }
  const main = readFileSync(join(here, '..', 'src', 'main.js'), 'utf8');
  const cssImports = [...main.matchAll(/^import\s+['"][^'"]+\.css['"]/gm)].map((m) => m[0]);
  assert.equal(cssImports.length, 1, `main.js should import only bundle.css, found: ${cssImports}`);
});
