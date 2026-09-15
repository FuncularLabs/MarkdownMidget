// YAML front matter (src/front-matter.js) in the shipped editor, loaded as MDM.setMarkdown loads a file and read as MDM.getMarkdown reads one.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { replaceAll, getMarkdown, callCommand } from '@milkdown/kit/utils';
import { wrapInHeadingCommand, wrapInBlockquoteCommand } from '@milkdown/kit/preset/commonmark';
import { Selection } from '@milkdown/kit/prose/state';
import { mountEditor } from './jsdom-editor.mjs';
import { settleDocument } from '../src/settle.js';
import { extractSpellText } from '../src/spell-extract.js';
import { beginLoad, endLoad, forgetLoad, markdownWithLines, ensureLines, changedSinceLoad, lineStatus, lineTarget, pinLine, showLineNumbers } from '../src/line-map.js';

let ed;
before(async () => { ed = await mountEditor(); });
const doc = () => ed.view().state.doc, serialize = () => ed.editor.action(getMarkdown()), saved = () => markdownWithLines(doc(), serialize).markdown;
const load = (md) => { beginLoad(); ed.editor.action(replaceAll(md, true)); settleDocument(ed.view()); endLoad(doc(), md); return doc().firstChild.type.name; };
const select = (pos) => ed.view().dispatch(ed.view().state.tr.setSelection(Selection.near(doc().resolve(pos))));
const caret = (text) => { let at = -1; doc().descendants((n, p) => { if (at < 0 && n.isText && n.text.includes(text)) at = p + n.text.indexOf(text) + text.length; }); select(at); return lineStatus(ed.view().state); };
const enter = (v) => v.someProp('handleKeyDown', (f) => f(v, new ed.window.KeyboardEvent('keydown', { key: 'Enter' })));
const FM = '---\ntitle: a\nstatus: b\n---\n\n# Heading\n', NESTED = '---\nx: |\n  ```\n  code\n  ```\n---\n# Heading\n';
const EDITS = {
  touch: (v) => { const p = doc().content.size - 1; v.dispatch(v.state.tr.insertText('x', p)); v.dispatch(v.state.tr.delete(p, p + 1)); },   // an edit elsewhere, taken back
  enter: (v) => { select(doc().firstChild.nodeSize - 1); enter(v); },   // at the end of the closing fence
  above: (v) => v.dispatch(v.state.tr.insert(0, v.state.schema.nodes.paragraph.create(null, v.state.schema.text('above')))),
  unfenced: (v) => v.dispatch(v.state.tr.delete(doc().firstChild.nodeSize - 4, doc().firstChild.nodeSize - 1)),
  deleted: (v) => v.dispatch(v.state.tr.delete(0, doc().firstChild.nodeSize)),
};
/** [file, edit, what its save writes (the file itself when absent)]. */
const CASES = [
  [FM, 'touch'], ['---  \ntitle: a  \n\n\nlist:\n  - x\n---\t \n\n\n# Two blank lines after\n', 'touch'], ['---\n---\n\ntext\n', 'touch'],
  ['---\n\n---\nno blank line after\n', 'touch'], ['---\r\na: 1\r\n---\n\ntext\n', 'touch'], ['---\n----\n---\n\n***\n\nend\n', 'touch'], [NESTED, 'touch'],
  ['---\na: 1\n---', 'touch', '---\na: 1\n---\n'], ['---\na: 1\n---  ', 'touch', '---\na: 1\n---  \n'],   // no final newline: one, as for any file
  ['\u{FEFF}---\na: 1\n---\n\nbody\n', 'touch', '---\na: 1\n---\n\nbody\n'],   // a second byte-order mark: dropped, as the parser drops one before any text
  [FM, 'enter', '---\ntitle: a\nstatus: b\n---\n\n\n# Heading\n'],   // the empty line under the fence: a blank line after it
  ['---\na: 1\n---\n\n\n# Heading\n', 'above', 'above\n\n```yaml\n---\na: 1\n---\n```\n\n# Heading\n'],
  [NESTED, 'above', 'above\n\n````yaml\n---\nx: |\n  ```\n  code\n  ```\n---\n````\n\n# Heading\n'],
  [FM, 'unfenced', '```yaml\n---\ntitle: a\nstatus: b\n\n```\n\n# Heading\n'], [FM, 'deleted', '# Heading\n'],
];

test('front matter opens as no edit and saves byte for byte, as a yaml code block where it would not read back, and a save saved again is the same', () => {
  for (const [md, edit, out = md] of CASES) {
    const opened = [load(md), changedSinceLoad(doc())];
    EDITS[edit](ed.view());
    const first = saved();
    assert.deepEqual([...opened, first, (load(first), saved())], ['front_matter', false, out, out], `${edit} ${JSON.stringify(md)}`);
  }
});

test('a --- pair later in the file is still a rule and a setext heading', () => {
  assert.deepEqual([load('x\n\n---\na\n---\n'), saved()], ['paragraph', 'x\n\n***\n\n## a\n']);
});

test('its lines, and the lines of the blocks after it, are numbered and reached by Go to Line, before and after an edit', () => {
  const md = '---\ntitle: a\n\nb: c\n---\n\n# Heading\n\ntext\n', go = (n) => { select(lineTarget(doc(), n)); pinLine(ed.view().state, n); return lineStatus(ed.view().state).line; };
  for (const untouched of [true, false]) {
    load(md);
    if (!untouched) { ed.view().dispatch(ed.view().state.tr.insertText('!', doc().content.size - 1)); forgetLoad(); saved(); }
    assert.deepEqual([caret('b: c'), caret('Heading'), caret('text')], [{ line: 4, col: 5 }, { line: 7, col: 8 }, { line: 9, col: 5 }]);
    assert.deepEqual([...Array(ensureLines(doc(), serialize).lines).keys()].map((i) => i + 1).filter((n) => go(n) !== n), []);
  }
  showLineNumbers(true);
  const margin = (load(md), [...ed.view().dom.querySelectorAll('[data-line], [data-gap]')].map((el) => el.dataset.gap ?? `${el.tagName}:${el.dataset.line}`));
  showLineNumbers(false);
  assert.deepEqual(margin, ['PRE:1–5', '6', 'H1:7', '8', 'P:9', '10']);
});

test('Enter and typing edit its text; input rules and the toolbar cannot make it another block; its words are code to spell check', () => {
  const v = (load(FM), ed.view());
  assert.doesNotMatch(extractSpellText(doc(), false).plain, /title/);
  caret('title: a'); enter(v);
  v.dispatch(v.state.tr.insertText('-')); v.someProp('handleTextInput', (f) => f(v, v.state.selection.head, v.state.selection.head, ' '));   // the bullet-list input rule
  v.dispatch(v.state.tr.insertText(' x')); ed.editor.action(callCommand(wrapInHeadingCommand.key, 1)); ed.editor.action(callCommand(wrapInBlockquoteCommand.key));
  assert.deepEqual([doc().firstChild.type.name, saved()], ['front_matter', '---\ntitle: a\n- x\nstatus: b\n---\n\n# Heading\n']);
});
