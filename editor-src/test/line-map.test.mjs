// #10: the formatted view's markdown line numbers (src/line-map.js), through the
// shipped editor in jsdom. A load goes through beginLoad/endLoad and a read of the
// markdown through markdownWithLines — the calls main.js makes around setMarkdown
// and getMarkdown. The MDM wiring itself is on the manual-check list.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { replaceAll, getMarkdown } from '@milkdown/kit/utils';
import { TextSelection } from '@milkdown/kit/prose/state';
import { undo } from '@milkdown/kit/prose/history';
import { mountEditor } from './jsdom-editor.mjs';
import { settleDocument } from '../src/settle.js';
import {
  beginLoad, endLoad, forgetLoad, markdownWithLines, ensureLines, lineStatus, lineTarget,
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
  assert.equal(caret('q').line, 6);   // not read yet: the old line, on the right block
  saved();
  assert.equal(caret('q').line, 8);
  undo(ed.view().state, ed.view().dispatch);
  saved();
  assert.equal(caret('q').line, 6);
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
