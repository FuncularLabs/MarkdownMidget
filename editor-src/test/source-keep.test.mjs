// Save fidelity (src/source-keep.js): what the formatted view saves keeps every top-level block it did not change, and the text
// around it, as it was read, through the shipped editor in jsdom. A load and a read go through beginLoad/endLoad and
// markdownWithLines, as main.js's setMarkdown and getMarkdown do; a save is a read followed by forgetLoad and a read
// (MDM.lineBaseSaved). Synthetic content only.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { replaceAll, getMarkdown } from '@milkdown/kit/utils';
import { serializerCtx, parserCtx } from '@milkdown/kit/core';
import { Selection, TextSelection } from '@milkdown/kit/prose/state';
import { joinBackward } from '@milkdown/kit/prose/commands';
import { undo } from '@milkdown/kit/prose/history';
import { mountEditor } from './jsdom-editor.mjs';
import { settleDocument } from '../src/settle.js';
import { beginLoad, endLoad, forgetLoad, markdownWithLines, settledMarkdown, ensureLines, lineStatus, lineTarget, pinLine } from '../src/line-map.js';

let ed;
before(async () => { ed = await mountEditor(); });

const v = () => ed.view();
const doc = () => v().state.doc;
const serialize = (d) => ed.editor.action(d ? (ctx) => ctx.get(serializerCtx)(d) : getMarkdown());   // as main.js
const parse = (md) => ed.editor.action((ctx) => ctx.get(parserCtx)(md));
const load = (md) => { beginLoad(); ed.editor.action(replaceAll(md, true)); settleDocument(v()); endLoad(doc(), md); return doc(); };
const read = () => markdownWithLines(doc(), serialize, parse);   // MDM.getMarkdown
const save = () => { const { markdown } = read(); forgetLoad(); read(); return markdown; };   // Save, then MDM.lineBaseSaved
const lines = (md) => md.split('\n').length;
const run = (tr) => v().dispatch(tr);
/** The top-level block whose text includes `text`. */
const top = (text) => { let hit; doc().forEach((node, pos, i) => { if (!hit && node.textContent.includes(text)) hit = { node, pos, i, end: pos + node.nodeSize }; }); assert.ok(hit, text); return hit; };
/** The position of the first text `text` begins in, at any depth. */
const textAt = (text) => { let at = -1; doc().descendants((n, pos) => { if (at < 0 && n.isText && n.text.includes(text)) at = pos + n.text.indexOf(text); }); assert.ok(at >= 0, text); return at; };
const typeAt = (text, what = 'X') => run(v().state.tr.insertText(what, textAt(text)));
/** The edited document, opened again from `md`, is the document that was edited. */
const reopens = (md, label) => { const edited = doc(); assert.ok(load(md).eq(edited), `${label}: ${JSON.stringify(md)} opens as another document`); };
/** The save opens as a whole serialisation of the edited document opens (which may not be the document: bold split around code, a list loosened by a dropped definition). */
const opensAsToday = (md, label) => { const today = serialize(); assert.ok(load(md).eq(load(today)), `${label}: ${JSON.stringify(md)} opens other than ${JSON.stringify(today)}`); };
/** `out` is `orig` with nothing changed but the block `block`: the text before it and after it are the original's. */
const onlyChanged = (orig, out, block, label) => {
  const at = orig.indexOf(block);
  assert.ok(at >= 0, `${label}: ${block}`);
  assert.ok(out.startsWith(orig.slice(0, at)) && out.endsWith(orig.slice(at + block.length)), `${label}: ${JSON.stringify(out)}`);
};
const caretLine = (text) => { run(v().state.tr.setSelection(TextSelection.create(doc(), textAt(text)))); return lineStatus(v().state).line; };
const goRead = (n) => { run(v().state.tr.setSelection(Selection.near(doc().resolve(lineTarget(doc(), n))))); pinLine(v().state, n); return lineStatus(v().state).line; };
const sweep = () => [...Array(ensureLines(doc(), serialize, parse).lines).keys()].map((i) => i + 1).filter((n) => goRead(n) !== n);   // lines Go to Line misses

// Every class the serialiser rewrites (escapes, `---` rules, a list straight after a paragraph, marks around inline code, bare
// URLs), and the constructs around them.
const CORPUS = [
  '---', 'title: Synthetic', 'tags: [a, b]', '---', '',
  'Price ~5 * 3 [approx] and snake _ case, *em* and _em_ and __strong__.', '',
  'Setext heading', '==============', '',
  'Para before a list', '- item one', '- item two', '  - nested', '    1. deeper', '',
  '+ plus one', '+ plus two', '',
  '* star one', '', '* star two', '',
  '---', '', '***', '',
  '| Aligned | Col |', '| ------- | :-: |', '| one     |  1  |', '',
  '|a|b|', '|-|-|', '|x|y|', '',
  'Line<br>break and <br/> here, and a hard\\', 'break.', '',
  '[ref]: https://example.com "Title"', '[other]: <https://other.example>', '',
  'A [ref] link, [other][], and https://bare.example.com bare.', '',
  '<!-- a comment -->', '',
  '<div align="center">', '  <b>html</b>', '</div>', '',
  '**bold `code` bold** and ~~strike `code` strike~~', '',
  '```js', 'const fence = 1;', '```', 'after a fence with no blank line', '',
  '    indented code', '',
  '> quote', 'lazy line', '',
  'Footnote[^1].', '',
  '[^1]: The note.', '', '', '',
  'three blank lines above, and no final newline',
].join('\n');

const VARIANTS = {
  corpus: CORPUS,
  'with a final newline': CORPUS + '\n',
  'with two final newlines': CORPUS + '\n\n',
  'with no blank line between blocks where one may go': CORPUS.replace('Price', '# ATX\nPrice'),
  'with two and three blank lines between blocks': CORPUS.replace('\n\nSetext', '\n\n\nSetext').replaceAll('\n\n+ plus', '\n\n\n\n+ plus'),
  empty: '',
  'one line': 'x',
  'a list last': '- a\n- b\n',
};

test('an untouched document is saved byte for byte, read, settled and saved again, and numbered by its own lines', () => {
  assert.notEqual(serialize(load(CORPUS)), CORPUS, 'the corpus must be one the serialiser rewrites');
  for (const [name, md] of Object.entries(VARIANTS)) {
    load(md);
    const first = read();
    assert.deepEqual([first.markdown, first.rebuilt, settledMarkdown(doc(), serialize, parse).markdown], [md, false, md], name);
    assert.equal(save(), md, `${name}: saved`);
    assert.equal(read().markdown, md, `${name}: after the save`);
    assert.equal(ensureLines(doc(), serialize, parse)?.lines, lines(md), name);
  }
});

const DOC = 'Alpha * one [a] _b_\n\n+ first\n+ second\n  + inner\n\nBravo ~ two\n\nBrio ~ three\n\n***\n\nCharlie https://c.example.com\n';
const [A, L, B, B2, , C] = DOC.split('\n\n');

test('typing in one paragraph changes that paragraph\'s lines and nothing else', () => {
  load(DOC);
  typeAt('Bravo');
  const out = save();
  onlyChanged(DOC, out, B, 'typing');
  assert.equal(lines(out), lines(DOC));
  assert.deepEqual(out.split('\n').filter((l, i) => l !== DOC.split('\n')[i]).length, 1);
  assert.match(out, /XBravo/);
  reopens(out, 'typing');
  load(CORPUS);   // beside front matter, with extra blank lines, definitions and no final newline elsewhere
  typeAt('Price');
  const corpus = save();
  onlyChanged(CORPUS, corpus, CORPUS.split('\n')[5], 'typing in the corpus');
  reopens(corpus, 'typing in the corpus');
  load(CORPUS);   // a table cell grown past its column: the table reads back wider than its node says, as serialising it whole would
  let cell = -1;
  doc().descendants((n, pos) => { if (cell < 0 && n.isText && n.text === 'Aligned') cell = pos; });
  run(v().state.tr.insertText('Wider', cell));
  const table = save();
  onlyChanged(CORPUS, table, '| Aligned | Col |\n| ------- | :-: |\n| one     |  1  |', 'a table cell');
  assert.match(table, /\| WiderAligned \|/);
  const RULED = 'Intro\n\n---\n\n## Section\n\nedit **bold `code` bold** me\n- item one\n  - nested\n\n| a | b   |\n| - | --- |\n| 1 | 2   |\n';
  load(RULED);   // the check on an edit two blocks below a `---` rule reads that rule as a rule, not as the top of a file
  typeAt('edit');
  const ruled = save();
  onlyChanged(RULED, ruled, 'edit **bold `code` bold** me\n', 'an edit below a rule');
  opensAsToday(ruled, 'an edit below a rule');
});

const STRUCTURAL = [
  ['a list item edited: the whole list is written again', () => typeAt('inner'), (out) => { onlyChanged(DOC, out, L, 'list'); assert.match(out, /^- first$/m); }],
  ['a block moved keeps its text', () => { const c = top('Charlie'); const tr = v().state.tr.delete(c.pos, c.end); run(tr.insert(0, c.node)); },
    (out) => { assert.ok(out.startsWith(C)); for (const b of [A, L, B, B2]) assert.ok(out.includes(b), b); }],
  ['a block duplicated: the original keeps its text', () => { const b = top('Bravo'); run(v().state.tr.insert(b.end, b.node)); },
    (out) => { for (const b of [A, L, B, B2, C]) assert.ok(out.includes(b), b); }],
  ['a paragraph split by Enter', () => { const a = top('Alpha'); run(v().state.tr.split(a.pos + 6)); }, (out) => onlyChanged(DOC, out, A, 'split')],
  ['two paragraphs joined', () => { const b = top('Brio'); run(v().state.tr.setSelection(TextSelection.create(doc(), b.pos + 1))); joinBackward(v().state, v().dispatch); },
    (out) => onlyChanged(DOC, out, `${B}\n\n${B2}`, 'join')],
  ['a block deleted', () => { const b = top('Bravo'); run(v().state.tr.delete(b.pos, b.end)); }, (out) => assert.equal(out, DOC.replace(`${B}\n\n`, ''))],
  ['an edit undone: the bytes are the original\'s again', () => { typeAt('Brio'); undo(v().state, v().dispatch); }, (out) => assert.equal(out, DOC)],
  ['new blocks pasted', () => { const a = top('Alpha'); run(v().state.tr.insert(a.end, parse('pasted *here*\n\n- new item\n').content)); },
    (out) => { assert.ok(out.startsWith(`${A}\n\n`) && out.endsWith(DOC.slice(DOC.indexOf(L)))); assert.match(out, /pasted \*here\*/); }],
];

for (const [name, act, expect] of STRUCTURAL) {
  test(`structural edit: ${name}; every other block keeps its text, and the save opens as the document`, () => {
    load(DOC);
    act();
    const out = save();
    expect(out);
    reopens(out, name);
  });
}

const HAZARDS = [
  ['a paragraph under a kept paragraph typed as a setext underline', 'Alpha\n\nBravo\n\nCharlie\n', () => { const b = top('Bravo'); run(v().state.tr.insertText('===', b.pos + 1, b.end - 1)); }],
  ['a paragraph typed as a rule under a kept paragraph', 'Alpha\n\nBravo\n', () => { const b = top('Bravo'); run(v().state.tr.insertText('---', b.pos + 1, b.end - 1)); }],
  ['a paragraph put between two lists', '- a\n- b\n\n* c\n* d\n', () => { const l = top('b'); run(v().state.tr.insert(l.end, v().state.schema.nodes.paragraph.create(null, v().state.schema.text('mid')))); }],
  ['the paragraph between two lists with the same marker deleted', '- a\n\nmid\n\n- c\n', () => { const m = top('mid'); run(v().state.tr.delete(m.pos, m.end)); }],
  ['a list moved next to a list with the same marker', '- a\n\npara\n\n* b\n\n- c\n', () => { const c = top('c'); const tr = v().state.tr.delete(c.pos, c.end); run(tr.insert(top('a').end, c.node)); }],
  ['the paragraph after a definition edited', '[ref]: https://e.example\n\nUses [ref] here.\n\nNext [ref]\n', () => typeAt('Uses'), (out) => assert.equal(out.split('[ref]: https://e.example').length, 2)],
  ['the paragraph after a definition deleted', 'Before [ref]\n\n[ref]: https://e.example\n\nUses it.\n\nNext [ref]\n', () => { const u = top('Uses'); run(v().state.tr.delete(u.pos, u.end)); },
    (out) => assert.equal(out.split('[ref]: https://e.example').length, 2)],
  ['the paragraph before a definition edited, the definition last', 'Uses [ref] here.\n\n[ref]: https://e.example\n', () => typeAt('here'), (out) => assert.equal(out.split('[ref]: https://e.example').length, 2)],
  ['the block before an html block edited', 'Lead *para*\n\n<div>\n*x*\n</div>\n\nafter\n', () => typeAt('para'), (out) => assert.ok(out.includes('\n\n<div>\n*x*\n</div>\n\nafter\n'))],
  ['the block before an open html comment edited', 'Lead\n\n<!-- open\n\nstill open -->\n\nafter\n', () => typeAt('Lead')],
  ['a fenced block edited to leave its fence open', 'Lead\n\n```\ncode\n```\n\nafter\n', () => { const f = top('code'); run(v().state.tr.insertText('```\nmore', f.pos + 1)); }],
  // The review's inputs: a definition inside an edited quote or list still serves the text after it; a rule that ends up first is
  // no front matter; a label defined twice keeps the definition that comes first.
  ['a definition inside an edited quote', '> quote [x][q]\n>\n> [q]: /q\n\nA\n\nB\n\nC\n\nTail [x][q].\n', () => typeAt('quote', 'Z')],
  ['a definition inside an edited list', '- item\n\n  [l]: /l\n\nA\n\nB\n\nC\n\nSee [y][l].\n', () => typeAt('item', 'Z')],
  ['a rule left first by deleting the block above it', 'Intro\n\n---\n\nmiddle\n\n---\n\nend\n', () => { const b = top('Intro'); run(v().state.tr.delete(b.pos, b.end)); },
    (out) => assert.equal(out, '***\n\nmiddle\n\n---\n\nend\n')],
  ['a rule left first by moving the block above it last', 'Intro\n\n---\n\nmiddle\n\n---\n\nend\n', () => { const b = top('Intro'); const tr = v().state.tr.delete(b.pos, b.end); run(tr.insert(tr.doc.content.size, b.node)); },
    (out) => assert.ok(out.startsWith('***\n\nmiddle\n\n---\n\nend\n'), out)],
  ['the paragraph after a reference edited, its definition far above', '[ref]: /r\n\nA\n\nA2\n\nB [ref]\n\nC\n', () => typeAt('C'), (out) => assert.ok(out.startsWith('[ref]: /r\n\nA\n\nA2\n\nB [ref]\n\n'), out)],
  // A label split over lines, which a definition may have: inside a quote or list item, and defined twice.
  ['a split-label definition inside an edited quote', '> quote\n>\n> [foo\n> bar]: /q\n\nA\n\nB\n\nC\n\nTail [x][foo bar].\n', () => typeAt('quote', 'Z')],
  ['a split-label definition inside an edited list', '- item\n\n  [foo\n  bar]: /l\n\nA\n\nB\n\nC\n\nSee [y][foo bar].\n', () => typeAt('item', 'Z')],
  ['a block moved above a label defined twice, once split', 'See [foo bar].\n\n[foo\nbar]: /one\n\nMiddle.\n\n[foo bar]: /two\n\nP1\n\nP2\n\nP3\n\nP4\n\nEnd [foo bar].\n',
    () => { const b = top('Middle'); const tr = v().state.tr.delete(b.pos, b.end); run(tr.insert(0, b.node)); }],
  ['a block moved above a label defined twice','See [a].\n\n[a]: /one\n\nMiddle.\n\n[a]: /two\n\nP1\n\nP2\n\nP3\n\nP4\n\nEnd [a].\n', () => { const b = top('Middle'); const tr = v().state.tr.delete(b.pos, b.end); run(tr.insert(0, b.node)); }],
];

for (const [name, md, act, expect = () => {}] of HAZARDS) {
  test(`neighbour hazard: ${name}; the save opens as a whole serialisation does`, () => {
    load(md);
    act();
    const out = save();
    expect(out);
    opensAsToday(out, name);
  });
}

test('save, edit, save, edit, save: each save changes only the block edited since the last', () => {
  load(DOC);
  typeAt('Alpha');
  const s1 = save();
  onlyChanged(DOC, s1, A, 'first save');
  typeAt('Bravo');
  const s2 = save();
  onlyChanged(s1, s2, B, 'second save');
  typeAt('Charlie');
  const s3 = save();
  onlyChanged(s2, s3, C, 'third save');
  assert.equal(save(), s3, 'a save with no edit since');
  reopens(s3, 'third save');
});

test('Go to Line and the status bar read the saved lines after a save that kept blocks of other line counts, and after edits', () => {
  const md = 'Title\n=====\n\n\n+ a\n\n+ b\n\nedit me\n\nlast\nline\n';   // a setext heading and a loose list kept; three blank lines
  load(md);
  typeAt('edit me');
  const out = save();
  const at = (text, of) => of.split('\n').findIndex((l) => l.includes(text)) + 1;
  assert.deepEqual([ensureLines(doc(), serialize, parse).lines, caretLine('last'), caretLine('b'), sweep()], [lines(out), at('last', out), at('b', out), []]);
  typeAt('Title');   // the heading is written again in one line: every block below it moves up
  const edited = read().markdown;
  assert.equal(at('Title', edited), 1);
  assert.deepEqual([ensureLines(doc(), serialize, parse).lines, caretLine('last'), sweep()], [lines(edited), at('last', edited), []]);
  const next = save();
  assert.deepEqual([next, caretLine('line'), sweep()], [edited, at('line', edited), []]);
});

test('cost: a second read of the same document serialises and parses nothing; a change too large to check cheaply is written whole, unparsed', () => {
  const calls = { serialize: 0, parse: 0 }, counted = (d) => (calls.serialize++, serialize(d)), parsed = (md) => (calls.parse++, parse(md));
  const reads = () => markdownWithLines(doc(), counted, parsed).markdown;
  load(`Intro *a*\n\n${Array.from({ length: 400 }, (_, i) => `- item ${i} lorem ipsum dolor sit amet`).join('\n')}\n\nOutro *b*\n`);   // one list of 400 items
  typeAt('item 200 ');
  const first = reads(), once = { ...calls }, again = reads();
  assert.deepEqual([once, calls, again, first], [{ serialize: 1, parse: 0 }, { serialize: 1, parse: 0 }, first, serialize()]);
  const notes = Array.from({ length: 60 }, (_, i) => `Para ${i} note[^n${i}] and [r${i}], lorem ipsum dolor sit amet consectetur adipiscing elit sed do.`).join('\n\n');
  load(`${notes}\n\n${Array.from({ length: 60 }, (_, i) => `[r${i}]: /r${i}`).join('\n')}\n\n${Array.from({ length: 60 }, (_, i) => `[^n${i}]: Note ${i}.`).join('\n\n')}\n`);
  const tr = v().state.tr;   // every third paragraph edited at once, as Replace All does
  for (let i = 57; i >= 0; i -= 3) tr.insertText('Z', textAt(`Para ${i} `));
  run(tr);
  calls.parse = 0;
  assert.deepEqual([reads(), calls.parse], [serialize(), 0]);
});

test('performance: an untouched document serialises nothing; a one-block edit costs about one serialisation', (t) => {
  const md = Array.from({ length: 60 }, (_, i) => `## S${i}\n\nText * ~ [x] ${i}\n\n- one\n- two\n  - nested ${i}\n\n| a | b |\n|---|---|\n| ${i} | x |\n\n---\n`).join('\n');
  load(md);
  const time = (fn) => { const t0 = performance.now(); fn(); return performance.now() - t0; };
  const fast = time(() => assert.equal(read().markdown, md));
  const ser = Math.min(...[1, 2, 3].map(() => time(() => serialize())));
  typeAt('Text * ~ [x] 30');
  const edit = time(() => read());
  t.diagnostic(`${md.length} chars: untouched ${fast.toFixed(1)} ms, serialise ${ser.toFixed(1)} ms, one-block edit ${edit.toFixed(1)} ms (${(edit / ser).toFixed(2)}x)`);
  assert.ok(fast < 50, `untouched: ${fast} ms`);
  assert.ok(edit < ser * 2.5 + 20, `edit ${edit} ms against serialise ${ser} ms`);
});
