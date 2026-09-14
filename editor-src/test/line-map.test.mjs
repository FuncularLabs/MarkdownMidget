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
import { TextSelection } from '@milkdown/kit/prose/state';
import { undo, undoDepth } from '@milkdown/kit/prose/history';
import { mountEditor } from './jsdom-editor.mjs';
import { settleDocument } from '../src/settle.js';
import {
  beginLoad, endLoad, forgetLoad, markdownWithLines, ensureLines, lineStatus, lineTarget, showLineNumbers,
} from '../src/line-map.js';

let ed;
before(async () => { ed = await mountEditor(); });

const doc = () => ed.view().state.doc;
const serialize = () => ed.editor.action(getMarkdown());
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
  ed.view().dispatch(ed.view().state.tr.setSelection(TextSelection.create(doc(), lineTarget(doc(), n))));
  return lineStatus(ed.view().state);
};

/** The margin (View ▸ Line Numbers) as drawn: whether its class is on, then each numbered element and its number. */
const margin = () => [ed.view().dom.classList.contains('mdm-line-numbers'), ...[...ed.view().dom.querySelectorAll('[data-line]')].map((el) => `${el.tagName}:${el.dataset.line}`)];

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
      [{ line: 3, col: 1 }, { line: 5, col: 7 }, { line: 5, col: 7 }, { line: 7, col: 1 }, { line: 9, col: 6 }, { line: 9, col: 6 }, { line: 11, col: 1 }]);
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
  assert.deepEqual(reads, [[false], [true, 'H1:1', 'UL:3', 'LI:4', 'LI:5', 'BLOCKQUOTE:7', 'LI:8', 'TABLE:10', 'PRE:14', 'SPAN:14', 'P:17'], [false]]);
});

test('a block a structural edit may have moved has no margin number until the next read, whose redraw is not an edit', () => {
  load('one\n\ntwo\n\nthree\n');
  showLineNumbers(true);
  const v = ed.view(), { state } = v;
  v.dispatch(state.tr.insert(doc().child(0).nodeSize, state.schema.nodes.paragraph.create(null, state.schema.text('new'))));
  const reads = [margin(), undoDepth(v.state)], edited = doc();
  saved();
  reads.push(margin(), doc() === edited, undoDepth(v.state));   // the same document, no history: nothing for the host to call a change
  showLineNumbers(false);
  assert.deepEqual(reads, [[true, 'P:1'], 1, [true, 'P:1', 'P:3', 'P:5', 'P:7'], true, 1]);
});

test('a document whose blocks do not pair with its parse gets no numbers rather than wrong ones', () => {
  load('# one\n\ntwo\n');
  assert.equal(caret('two').line, 3);
  const other = doc();
  beginLoad();
  ed.editor.action(replaceAll('- a\n- b\n', true));
  endLoad(other, '# one\n\ntwo\n');
  assert.deepEqual(lineStatus(ed.view().state), {});
  assert.equal(lineTarget(doc(), 1), null);
});
