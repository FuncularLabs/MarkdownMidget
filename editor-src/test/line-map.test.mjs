// #10: the formatted view's markdown line numbers (src/line-map.js), through the
// shipped editor in jsdom. A load goes through beginLoad/endLoad and a read of the
// markdown through markdownWithLines — the calls main.js makes around setMarkdown
// and getMarkdown. The MDM wiring itself is on the manual-check list.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { replaceAll, getMarkdown, callCommand } from '@milkdown/kit/utils';
import { turnIntoTextCommand, liftListItemCommand, insertHardbreakCommand } from '@milkdown/kit/preset/commonmark';
import { newlineInCode, joinBackward, deleteSelection } from '@milkdown/kit/prose/commands';
import { Selection, TextSelection } from '@milkdown/kit/prose/state';
import { undo, undoDepth } from '@milkdown/kit/prose/history';
import { serializerCtx } from '@milkdown/kit/core';
import { mountEditor } from './jsdom-editor.mjs';
import { declarations } from './css-declarations.mjs';
import { settleDocument } from '../src/settle.js';
import { setSpellRanges } from '../src/spell-decorate.js';
import {
  beginLoad, endLoad, forgetLoad, markdownWithLines, ensureLines, lineStatus, lineTarget, pinLine, showLineNumbers, settledMarkdown,
} from '../src/line-map.js';

let ed;
before(async () => { ed = await mountEditor(); });

const doc = () => ed.view().state.doc;
const serialize = (d) => ed.editor.action(d ? (ctx) => ctx.get(serializerCtx)(d) : getMarkdown());   // as main.js
const saved = () => markdownWithLines(doc(), serialize);
const load = (md) => {
  beginLoad();
  ed.editor.action(replaceAll(md, true));
  settleDocument(ed.view());
  endLoad(doc(), md);
};
/** The caret after `after` characters of the first text containing `text`, as the status bar reads it. */
const caret = (text, after = 0) => {
  let at = -1;
  doc().descendants((n, pos) => { if (at < 0 && n.isText && n.text.includes(text)) at = pos + n.text.indexOf(text) + after; });
  ed.view().dispatch(ed.view().state.tr.setSelection(TextSelection.create(doc(), at)));
  return lineStatus(ed.view().state);
};
/** A position shown as its block's text with `|` where the position is. */
const at = (pos) => { const $p = doc().resolve(pos); const t = $p.parent.textContent; return `${t.slice(0, $p.parentOffset)}|${t.slice($p.parentOffset)}`; };
const shape = () => ensureLines(doc(), serialize).entries.map((e) => `${e.type}@${e.line}`);
/** Go to Line `n`, then the status bar where it landed. */
const goRead = (n) => {
  ed.view().dispatch(ed.view().state.tr.setSelection(Selection.near(doc().resolve(lineTarget(doc(), n)))));
  pinLine(ed.view().state, n);   // as main.js goToLine does
  return lineStatus(ed.view().state);
};
const sweep = () => [...Array(ensureLines(doc(), serialize).lines).keys()].map((i) => i + 1).filter((n) => goRead(n).line !== n);   // lines not read as themselves

/** The margin (View ▸ Line Numbers) as drawn: whether its class is on, then each numbered element and its number, or GAP and a label's lines. */
const margin = () => [ed.view().dom.classList.contains('mdm-line-numbers'), ...[...ed.view().dom.querySelectorAll('[data-line], [data-gap]')].map((el) => (el.dataset.gap ? `GAP:${el.dataset.gap}` : `${el.tagName}:${el.dataset.line}`))];

const DISK = 'Title\n=====\n\n[ref]: https://example.com\n\n    indented\n    code\n\nPara one\ntwo\n';
const SAVED = '# H\n\n- a\n  - b\n\n> q\n\n| x | y |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n\n```js\none\ntwo\n```\n';

test('the lines recorded while saving are the lines a parse of the saved markdown finds', () => {
  for (const file of ['../../HELP.md', '../../README.md', 'fixtures/roundtrip-audit.md']) {
    load(readFileSync(new URL(file, import.meta.url), 'utf8'));
    forgetLoad();
    const { markdown } = saved();
    assert.equal(markdown, serialize(), `${file}: recording changed the saved markdown`);
    const recorded = shape();
    assert.ok(recorded.length > 20, `${file}: ${recorded.length} blocks numbered`);
    load(markdown);
    assert.deepEqual(shape(), recorded, file);
  }
});

test('an untouched document is numbered by the text it was loaded from', () => {
  load(DISK);
  assert.deepEqual(caret('two', 2), { line: 10, col: 3 });
  assert.deepEqual(caret('code'), { line: 7, col: 1 });
  assert.equal(ensureLines(doc(), serialize).lines, 11);
  assert.equal(at(lineTarget(doc(), 7)), 'indented\n|code');
  assert.equal(at(lineTarget(doc(), 4)), 'Title|');   // a definition's line: the end of the block before it
});

test("after an edit, and after a save, the numbers are the saved markdown's", () => {
  load(DISK);
  ed.view().dispatch(ed.view().state.tr.insertText('!', 1));
  saved();
  assert.deepEqual(caret('two', 2), { line: 9, col: 3 });
  load(DISK);
  forgetLoad();   // MDM.lineBaseSaved
  saved();
  assert.deepEqual(caret('two', 2), { line: 9, col: 3 });
});

test("the caret's line in a nested list, a quote, a table and a code block, in both numberings", () => {
  for (const untouched of [true, false]) {
    load(SAVED);
    if (!untouched) { forgetLoad(); saved(); }
    assert.deepEqual([caret('b', 1), caret('q'), caret('x'), caret('3'), caret('two', 3)],
      [{ line: 4, col: 2 }, { line: 6, col: 1 }, { line: 8, col: 1 }, { line: 11, col: 1 }, { line: 15, col: 4 }]);
  }
});

test('Go to Line lands in the block that holds the line, clamped to the document', () => {
  load(SAVED);
  const go = (n) => at(lineTarget(doc(), n));
  assert.deepEqual([go(15), go(16), go(10), go(9), go(4), go(12), go(0)],
    ['one\n|two', 'one\ntwo|', '|1', '|x', '|b', '|3', '|H']);
  assert.equal(go(99), go(17));
});

test('the column counts text, not markdown, and an emoji as one character', () => {
  load('a 😀 **bold**\\\nnext\n');
  assert.deepEqual(caret('bold', 4), { line: 1, col: 9 });
  assert.deepEqual(caret('next', 1), { line: 2, col: 2 });
});

test("an edit above the caret keeps its block, moves its line at the next read, and undo moves it back", () => {
  load(SAVED);
  const { state } = ed.view();
  ed.view().dispatch(state.tr.insert(0, state.schema.nodes.paragraph.create(null, state.schema.text('new'))));
  assert.deepEqual(caret('q'), {});   // not read yet: no line rather than the old one
  saved();
  assert.equal(caret('q').line, 8);
  undo(ed.view().state, ed.view().dispatch);
  saved();
  assert.equal(caret('q').line, 6);
});

test("a block made since the last read has no line until the next read, rather than the block above's", () => {
  load('one line\n\ntwo line\n');
  caret('one line', 8);
  ed.view().dispatch(ed.view().state.tr.split(ed.view().state.selection.head).insertText('x'));   // Enter, then typing
  assert.deepEqual(lineStatus(ed.view().state), {});
  saved();
  assert.deepEqual(lineStatus(ed.view().state), { line: 3, col: 2 });
});

test("a block whose type changed since the last read has no line until the next read; Go to Line rebuilds first", () => {
  load('one\n\nzz\n\nthree\n');
  const v = ed.view(), h = (caret('zz'), v.state.selection.head);
  v.dispatch(v.state.tr.setSelection(TextSelection.create(doc(), h, h + 2)).insertText('```'));
  v.someProp('handleTextInput', (f) => f(v, h + 3, h + 3, ' '));   // the code-block input rule
  v.dispatch(v.state.tr.insertText('let a = 1;'));
  const reads = [doc().child(1).type.name, lineStatus(v.state)];
  ensureLines(doc(), serialize);   // main.js goToLine
  reads.push(goRead(4));
  ed.editor.action(callCommand(turnIntoTextCommand.key));
  reads.push(doc().child(1).type.name, lineStatus(v.state));
  saved();
  reads.push(lineStatus(v.state));
  assert.deepEqual(reads, ['code_block', {}, { line: 4, col: 1 }, 'paragraph', {}, { line: 3, col: 1 }]);
});

test('a paragraph joined to a list, or lifted out of one, has no line until the next read; the blocks above keep theirs', () => {
  load('intro\n\n- a item\n- b item\n\nzz words\n');
  const v = ed.view(), h = (caret('zz words'), v.state.selection.head);
  v.dispatch(v.state.tr.insertText('-', h));
  v.someProp('handleTextInput', (f) => f(v, h + 1, h + 1, ' '));   // the bullet-list input rule
  const reads = [doc().child(1).childCount, caret('zz words', 2), caret('b item')];
  saved();
  reads.push(caret('zz words', 2));
  ed.editor.action(callCommand(liftListItemCommand.key));   // Shift+Tab
  reads.push(caret('zz words', 2));
  saved();
  reads.push(caret('zz words', 2));
  assert.deepEqual(reads, [3, {}, { line: 4, col: 1 }, { line: 5, col: 3 }, {}, { line: 6, col: 3 }]);
});

test("typing, Enter in a code block and a line break keep the caret's line before the read; only the blocks below wait", () => {
  load('# H\n\npara\n\n```\none\n```\n\nbelow\n');
  const v = ed.view();
  caret('H', 1);
  v.dispatch(v.state.tr.insertText('ead'));
  const reads = [lineStatus(v.state)];
  caret('para', 4);
  v.dispatch(v.state.tr.insertText(' more'));
  reads.push(lineStatus(v.state), caret('below'));
  caret('one', 3);
  newlineInCode(v.state, v.dispatch);   // Enter in a code block
  reads.push(lineStatus(v.state), caret('para'), caret('below'));
  saved();
  reads.push(caret('below'));
  caret('more');
  ed.editor.action(callCommand(insertHardbreakCommand.key));   // Shift+Enter
  reads.push(lineStatus(v.state), caret('below'));
  saved();
  reads.push(caret('below'));
  assert.deepEqual(reads, [{ line: 1, col: 5 }, { line: 3, col: 10 }, { line: 9, col: 1 }, { line: 7, col: 1 }, { line: 3, col: 1 },
    {}, { line: 10, col: 1 }, { line: 4, col: 1 }, {}, { line: 11, col: 1 }]);
});

test("paragraphs merged by Backspace or by deleting across them have no line until the next read, not a deleted one's", () => {
  const md = 'aaa first\n\nbbb second\n\nccc third\n', v = ed.view();
  load(md);
  caret('bbb');
  joinBackward(v.state, v.dispatch);   // Backspace at the start of a paragraph
  v.dispatch(v.state.tr.insertText('x'));
  const reads = [lineStatus(v.state)];
  saved();
  reads.push(lineStatus(v.state));
  load(md);
  const from = (caret('first'), v.state.selection.head), to = (caret('third'), v.state.selection.head);
  v.dispatch(v.state.tr.setSelection(TextSelection.create(doc(), from, to)));
  deleteSelection(v.state, v.dispatch);   // Delete
  reads.push(lineStatus(v.state));
  saved();
  reads.push(lineStatus(v.state));
  assert.deepEqual(reads, [{}, { line: 1, col: 11 }, {}, { line: 1, col: 5 }]);
});

test('an HTML block or comment over several lines counts its lines, and Go to Line inside it lands right after it', () => {
  for (const untouched of [true, false]) {
    load('# T\n\n<div align="center">\nhello world\n</div>\n\n<!-- a\nb\nc -->\n\nend\n');
    if (!untouched) { forgetLoad(); saved(); }
    assert.deepEqual([goRead(3), goRead(4), goRead(5), goRead(7), goRead(8), goRead(9), caret('end')],
      [{ line: 3, col: 1 }, { line: 4, col: 1 }, { line: 5, col: 7 }, { line: 7, col: 1 }, { line: 8, col: 1 }, { line: 9, col: 6 }, { line: 11, col: 1 }]);
  }
});

test('inline HTML broken over lines counts its lines, and the line it ends on counts its characters', () => {
  load('text <span\nclass="x">more\nline3\n');
  assert.deepEqual([caret('more'), caret('line3', 5), goRead(2), goRead(3)],
    [{ line: 2, col: 11 }, { line: 3, col: 6 }, { line: 2, col: 11 }, { line: 3, col: 1 }]);
});

test('the margin numbers top-level blocks and list items, not one starting on the line numbered above it, and nothing when off', () => {
  load('# H\n\n- a\n  - b\n- c\n\n> q\n> - d\n\n| x |\n| - |\n| 1 |\n\n```mermaid\ngraph TD\n```\n');
  const reads = [margin()];
  showLineNumbers(true);
  reads.push(margin());
  showLineNumbers(false);
  reads.push(margin());   // a mermaid block's number is on its code and on a span before its diagram, which shows while the code is hidden
  assert.deepEqual(reads, [[false], [true, 'H1:1', 'GAP:2', 'UL:3', 'LI:4', 'LI:5', 'GAP:6', 'BLOCKQUOTE:7', 'LI:8', 'GAP:9', 'TABLE:10–12', 'PRE:13–16', 'SPAN:13–16', 'P:17'], [false]]);
});

test('a block a structural edit may have moved has no margin number until the next read, whose redraw is not an edit', () => {
  load('one\nuno\n\ntwo\n\nthree\n');   // one's range may have grown too: only its first line shows
  showLineNumbers(true);
  const v = ed.view(), { state } = v;
  v.dispatch(state.tr.insert(doc().child(0).nodeSize, state.schema.nodes.paragraph.create(null, state.schema.text('new'))));
  const reads = [margin(), undoDepth(v.state)], edited = doc();
  saved();
  reads.push(margin(), doc() === edited, undoDepth(v.state));   // the same document, no history: nothing for the host to call a change
  showLineNumbers(false);
  assert.deepEqual(reads, [[true, 'P:1'], 1, [true, 'P:1–2', 'GAP:3', 'P:4', 'GAP:5', 'P:6', 'GAP:7', 'P:8', 'GAP:9'], true, 1]);
});

test('the margin redraws for a setting or a numbering that changed, and dispatches nothing for one that did not', () => {
  load('One\n\nTwo\n');
  showLineNumbers(true);
  const v = ed.view(), reads = [], dispatched = (act) => { const before = v.state; act(); reads.push(v.state !== before); };
  dispatched(() => showLineNumbers(true));   // a redraw is a transaction: a new state
  v.dispatch(v.state.tr.insertText('x', 2));   // Oxne: the same blocks on the same lines
  dispatched(saved);
  v.dispatch(v.state.tr.split(3));   // Ox|ne: a block more
  dispatched(saved);
  dispatched(() => showLineNumbers(false));
  assert.deepEqual(reads, [false, false, true, true]);
});

test('a document whose blocks do not pair with its parse gets no numbers rather than wrong ones', () => {
  load('# one\n\ntwo\n');
  assert.equal(caret('two').line, 3);
  const other = doc();
  beginLoad();
  ed.editor.action(replaceAll('- a\n- b\n', true));
  endLoad(other, '# one\n\ntwo\n');
  pinLine(ed.view().state, 2);   // nothing to pin a line to
  assert.deepEqual(lineStatus(ed.view().state), {});
  assert.equal(lineTarget(doc(), 1), null);
});

const KINDS = 'Title\n=====\n\n\n[ref]: https://example.com\n\n    indented\n    code\n\n```js\na\n```\n\n| x |\n| - |\n| 1 |\n\n***\n\n<div>\nhi\n</div>\n\n<!-- a\nb -->\n\n- one\n\n  more\n\n- two\n\n```mermaid\ngraph TD\n```\n\nwrapped\nline\n';

test('Go to Line N reads Ln N for every line, the ones with no place of their own included, in both numberings', () => {
  load(KINDS);
  const untouched = sweep();
  forgetLoad(); saved();
  assert.deepEqual([untouched, sweep()], [[], []]);
});

for (const file of ['../../HELP.md', '../../CHANGELOG.md', 'fixtures/roundtrip-audit.md']) {
  test(`Go to Line N reads Ln N for every line of ${file}, untouched`, () => { load(readFileSync(new URL(file, import.meta.url), 'utf8')); assert.deepEqual(sweep(), []); });
}

test('a line with no place of its own reads as itself where the caret went, until the caret moves, you type or undo; nothing is edited', () => {
  load('one\n\n\ntwo\n');
  const v = ed.view(), loaded = doc(), reads = [goRead(3), at(v.state.selection.head), doc() === loaded, undoDepth(v.state), caret('one', 2), goRead(2)];
  v.dispatch(v.state.tr.insertText('x'));
  reads.push(lineStatus(v.state), goRead(3), (undo(v.state, v.dispatch), lineStatus(v.state)));
  assert.deepEqual(reads, [{ line: 3, col: 1 }, 'one|', true, 0, { line: 1, col: 3 }, { line: 2, col: 1 }, { line: 1, col: 5 }, { line: 3, col: 1 }, { line: 1, col: 4 }]);
});

test('Go to Line on a rule selects the rule and reads its line', () => {
  load('above\n\n***\n\nbelow\n');
  assert.deepEqual([goRead(3), ed.view().state.selection.node?.type.name], [{ line: 3, col: 1 }, 'hr']);
});

test('the margin labels the lines between top-level blocks above the next number: two listed, three as a range, none when off', () => {
  showLineNumbers(true);
  load('# Heading\n\n\nParagraph\n\n```js\nlet a = 1;\n```\n\n| x | y |\n| - | - |\n| 1 | 2 |\n\n[ref]: https://example.com\n');
  const reads = [margin(), (load('a\n\n\n\nb\n\n- c\n\n  d\n'), margin())];   // no label for the blank line inside the list item
  showLineNumbers(false);
  assert.deepEqual([...reads, margin()], [[true, 'H1:1', 'GAP:2 3', 'P:4', 'GAP:5', 'PRE:6–8', 'GAP:9', 'TABLE:10–12', 'P:13–15'],
    [true, 'P:1', 'GAP:2–4', 'P:5', 'GAP:6', 'UL:7–9', 'P:10'], [false]]);
});

test('the labels are worked out once per numbering, and typing keeps their elements', () => {
  load('one\n\ntwo\n\nthree\n');
  showLineNumbers(true);
  const v = ed.view(), proto = Object.getPrototypeOf(doc()), real = proto.textBetween, label = v.dom.querySelector('[data-gap]');
  let reads = 0;   // a block's text read with line-map's leaf text, as working out where its lines end does
  proto.textBetween = function (...args) { if (this.type.name !== 'doc' && typeof args[3] === 'function') reads++; return real.apply(this, args); };
  try {
    v.dispatch(v.state.tr.setSelection(TextSelection.create(doc(), 7)));
    for (const ch of 'abc') v.dispatch(v.state.tr.insertText(ch));
    const typed = reads, read = (saved(), reads);
    v.dispatch(v.state.tr.insertText('d'));
    assert.deepEqual([typed, reads > read, v.dom.querySelector('[data-gap]') === label], [0, true, true]);
  } finally { proto.textBetween = real; showLineNumbers(false); }
});

test("a save forgets a pinned line: the status reads the caret's line in the saved markdown", () => {
  load('Title\n=====\n\n\nbody\n');
  const reads = [goRead(3)];
  forgetLoad(); saved();   // MDM.lineBaseSaved: # Title\n\nbody\n
  assert.deepEqual([...reads, lineStatus(ed.view().state)], [{ line: 3, col: 1 }, { line: 1, col: 6 }]);
});

test('Enter or Shift+Enter in the last block hides the label after it until the next read', () => {
  const v = ed.view(), reads = [];
  showLineNumbers(true);
  try {
    for (const brk of [() => v.dispatch(v.state.tr.split(v.state.selection.head)), () => ed.editor.action(callCommand(insertHardbreakCommand.key))]) {
      load('one\n\ntwo\n');
      caret('two', 3);
      brk();
      reads.push(margin(), (v.dispatch(v.state.tr.insertText('x')), margin()), (saved(), margin()));
    }
  } finally { showLineNumbers(false); }
  const numbered = [true, 'P:1', 'GAP:2', 'P:3'];
  assert.deepEqual(reads, [numbered, numbered, [...numbered, 'GAP:4', 'P:5', 'GAP:6'], numbered, numbered, [true, 'P:1', 'GAP:2', 'P:3–4', 'GAP:5']]);
});

const layoutReads = async (fn, reads = []) => {   // what `fn` calls of getBoundingClientRect, on any element, and posAtCoords, on any view
  const spied = [[ed.window.Element.prototype, 'getBoundingClientRect'], [ed.view().constructor.prototype, 'posAtCoords']].map(([o, name]) => [o, name, o[name]]);
  spied.forEach(([o, name, f]) => { o[name] = function (...a) { reads.push(name); return f.apply(this, a); }; });
  try { await fn(); } finally { spied.forEach(([o, name, f]) => { o[name] = f; }); }
  return reads;
};

test('creating the editor, or loading a document with line numbers off or on, reads no layout; the squiggles measure when drawn', async () => {
  const { createEditor } = await import('../src/editor-factory.js'), md = readFileSync(new URL('../../HELP.md', import.meta.url), 'utf8');
  let other;
  assert.deepEqual(await layoutReads(async () => { other = await createEditor({ root: ed.window.document.body.appendChild(ed.window.document.createElement('div')), initialMarkdown: md }); }), []);
  await other.destroy();
  for (const on of [false, true]) { showLineNumbers(on); assert.deepEqual(await layoutReads(() => load(md)), [], `line numbers ${on}`); }
  showLineNumbers(false);
  assert.ok((await layoutReads(() => setSpellRanges(ed.view(), [{ from: 3, to: 7 }]))).includes('getBoundingClientRect'));
  assert.ok(ed.view().dom.querySelector('.mdm-misspelled'));
});

test("the settled markdown is the first read after a load, needing no rebuild for Go to Line; after a keystroke it is still the loaded text, and the live numbering stands", () => {
  for (const file of ['../../CHANGELOG.md', '../../HELP.md', 'fixtures/roundtrip-audit.md']) {
    load(readFileSync(new URL(file, import.meta.url), 'utf8'));
    const settled = settledMarkdown(doc(), serialize).markdown, live = markdownWithLines(doc(), serialize), v = ed.view();
    assert.deepEqual([settled, live.rebuilt], [live.markdown, false], file);
    v.dispatch(v.state.tr.setSelection(Selection.near(doc().resolve(doc().content.size >> 1))));
    v.dispatch(v.state.tr.insertText('x', Selection.atStart(doc()).from));
    markdownWithLines(doc(), serialize);   // the host's dirty check, which may come first: numbered by the saved markdown now
    const { line } = lineStatus(v.state);
    assert.deepEqual([settledMarkdown(doc(), serialize).markdown, serialize() !== settled, line > 0, lineStatus(v.state).line], [settled, true, true, line], file);
  }
});

const USER_STORY = '# User story\n\nAs the owner, I want\neach range\nin the margin.\n\n\n---\n\n## Acceptance\n\n> Every number\n> once, in order,\n> never overlapping.\n\n## Notes\n\nOne\ntwo\nthree\nfour\nfive\nsix\n\n```js\nconst a = 1;\nconst b = 2;\n```\n\n| Name | Value |\n| ---- | ----- |\n| one  | 1     |\n| two  | 2     |\n\n[spec]: https://example.com/spec\n';
/** The line numbers the margin shows, top to bottom, a range as all its lines, without a mermaid diagram's copy of its block's. */
const numbers = () => margin().slice(1).filter((m) => !m.startsWith('SPAN')).flatMap((m) => { const [a, b = a] = m.split(':')[1].split(/–| /).map(Number); return [...Array(b - a + 1).keys()].map((i) => a + i); });

test('a block shows the lines it covers as a range, and a gap between blocks its own label, as on a user story; nothing when off', () => {
  load(USER_STORY);
  const reads = [margin(), (showLineNumbers(true), margin())];
  showLineNumbers(false);
  assert.deepEqual([...reads, margin()], [[false], [true, 'H1:1', 'GAP:2', 'P:3–5', 'GAP:6 7', 'HR:8', 'H2:9–10', 'GAP:11', 'BLOCKQUOTE:12–14', 'GAP:15',
    'H2:16', 'GAP:17', 'P:18–23', 'GAP:24', 'PRE:25–28', 'GAP:29', 'TABLE:30–33', 'P:34–36'], [false]]);
});

test("a gap with no room for a label under the block above it (a rule, a table, a list item, a diagram) goes to the next block's range", () => {
  showLineNumbers(true);
  load('a\n\n***\n\nb\n\n| x |\n| - |\n| 1 |\n\n- d\n\n- e\n\n```mermaid\ngraph TD\n```\n\nf\n');
  const read = margin();
  showLineNumbers(false);
  assert.deepEqual(read, [true, 'P:1', 'GAP:2', 'HR:3', 'P:4–5', 'GAP:6', 'TABLE:7–9', 'UL:10–11', 'LI:12–13', 'GAP:14', 'PRE:15–17', 'SPAN:15–17', 'P:18–19', 'GAP:20']);
});

test("a block after a nested list in a quote or a list item has its own range: the nested item's stops at its own last line", () => {
  showLineNumbers(true);
  const reads = [(load('> quote\n> - a\n> - b\n>\n> tail\n'), margin()), (load('- a\n  - b\n\n  tail\n- c\n'), margin())];
  showLineNumbers(false);
  assert.deepEqual(reads, [[true, 'BLOCKQUOTE:1', 'LI:2', 'LI:3', 'P:4–5', 'P:6'], [true, 'UL:1', 'LI:2', 'P:3–4', 'LI:5', 'P:6']]);
});

test('a block that took in the one after it, by Backspace or a delete across blocks, shows only its first line until the next read', () => {
  const v = ed.view(), reads = [], head = (text, after) => (caret(text, after), v.state.selection.head);
  showLineNumbers(true);
  load('a\nb\n\nc\nd\ne\n'); caret('c'); joinBackward(v.state, v.dispatch);   // Backspace at the start of c
  reads.push(margin(), (saved(), margin()));
  load('a\nb\nc\n\nd\n\nz\n'); v.dispatch(v.state.tr.setSelection(TextSelection.create(doc(), head('a', 1), head('d', 1)))); deleteSelection(v.state, v.dispatch);
  reads.push(margin(), (saved(), margin()));
  showLineNumbers(false);
  assert.deepEqual(reads, [[true, 'P:1'], [true, 'P:1–4', 'GAP:5'], [true, 'P:1'], [true, 'P:1', 'GAP:2', 'P:3', 'GAP:4']]);
});

test('a table or a code fence ending a file with no final newline, and an empty code fence, count their own lines, before and after a read', () => {
  showLineNumbers(true);
  const reads = [(load('a\n\n| x |\n| - |\n| 1 |'), margin()), (load('a\n\n```\nx\n```'), margin()), (load('a\n\n```\n```\n\nb\n'), margin())];
  ed.view().dispatch(ed.view().state.tr.insertText('!', 1));
  reads.push((saved(), margin()));
  showLineNumbers(false);
  const empty = [true, 'P:1', 'GAP:2', 'PRE:3–4', 'GAP:5', 'P:6', 'GAP:7'];
  assert.deepEqual(reads, [[true, 'P:1', 'GAP:2', 'TABLE:3–5'], [true, 'P:1', 'GAP:2', 'PRE:3–5'], empty, empty]);
});

// Every block, every container a number can sit in, and a list at each depth: in a list, a quote, a quote in a quote,
// a footnote, a footnote in a quote, a footnote in a list item and a task list. (A list's first item is on its list's
// line, so each holder has a second one.) A footnote is a `dl` holding a `dd`, and neither carries a number of its own
// when it is not at the top: the one in a quote is `blockquote > dl > dd > ul > li[data-line]`.
const HOLDERS = '# H\n\n- a\n  - b\n    - c\n- d\n\n> q\n>\n> - e\n>   - f\n>\n> after\n\n> > - deep\n> > - deeper\n\n1. one\n   1. nested\n\n'
  + '- [ ] task\n  - [x] done\n\nText[^n].\n\n[^n]: note\n\n    - in a footnote\n\n| x |\n| - |\n| 1 |\n\n```js\na\n```\n\n'
  + '> Quoted[^q].\n>\n> [^q]: in a quote\n>\n>     - listed\n>     - again\n\n'
  + '- item[^i]\n\n  [^i]: in an item\n\n      - listed\n      - again\n\n'
  + '```mermaid\ngraph TD\n```\n\n<div>\nhtml\n</div>\n\n***\n\n\nlast\n';

for (const [name, md] of [['every kind of holder', HOLDERS], ...['../../HELP.md', 'fixtures/roundtrip-audit.md']
  .map((file) => [file, readFileSync(new URL(file, import.meta.url), 'utf8')])]) {
  test(`with numbers on, nothing between a number and the editor can be positioned by a theme: ${name}`, () => {
    // A number is an absolute ::before at left 0, drawn against its nearest positioned ancestor, which has to be the
    // editor (structure.css). So every element from a numbered one up to the editor is pinned static, !important, by
    // ONE rule of structure.css - read out of the file rather than restated here, so the rule this checks is the rule
    // that ships. Any element left out is one a theme's `position: relative` still turns into the anchor, dragging
    // the numbers inside it in over the text. Checked on the real editor's DOM, so a holder nobody thought to list -
    // a footnote, a quote in a quote - shows up here rather than in a theme.
    const pins = declarations(readFileSync(new URL('../styles/structure.css', import.meta.url), 'utf8'))
      .filter((d) => d.prop === 'position' && d.value === 'static');
    assert.deepEqual(pins.map((d) => d.important), [true], 'one static pin, and !important, or a later layer outranks it');
    const [media, selector] = pins[0].where.split(' > ');   // screen only: paper shows no numbers (layers.test.mjs)
    assert.equal(media, '@media screen');
    showLineNumbers(true);
    try {
      load(md);
      const root = ed.view().dom, pinned = new Set(root.querySelectorAll(selector));
      const numbered = [...root.querySelectorAll('[data-line], [data-gap]')], loose = new Set();
      for (const el of numbered) for (let n = el; n !== root; n = n.parentElement) if (!pinned.has(n)) loose.add(`${n.tagName} (holding ${el.tagName}:${el.dataset.line ?? el.dataset.gap})`);
      assert.ok(numbered.length > 20, `only ${numbered.length} numbers drawn: the premise failed`);
      assert.deepEqual([...loose], []);
    } finally { showLineNumbers(false); }
  });
}

for (const file of ['../../HELP.md', '../../CHANGELOG.md', 'fixtures/roundtrip-audit.md']) {
  test(`the margin shows every line of ${file} once and in order, untouched and after an edit and a read`, () => {
    const all = () => [...Array(ensureLines(doc(), serialize).lines).keys()].map((i) => i + 1), reads = [];
    showLineNumbers(true);
    load(readFileSync(new URL(file, import.meta.url), 'utf8'));
    const { schema, tr } = ed.view().state;
    reads.push(numbers(), all(), (ed.view().dispatch(tr.insert(0, schema.nodes.paragraph.create(null, schema.text('new')))), saved(), numbers()), all());
    showLineNumbers(false);
    assert.deepEqual([reads[0], reads[2]], [reads[1], reads[3]]);
  });
}
