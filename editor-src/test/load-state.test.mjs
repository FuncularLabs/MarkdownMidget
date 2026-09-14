// Whether the formatted view's document has changed since its load (line-map.js changedSinceLoad, MDM.changedSinceLoad): the
// host takes no clean baseline until it has, so nothing the load does by itself may say so — only an edit.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { replaceAll } from '@milkdown/kit/utils';
import { TextSelection } from '@milkdown/kit/prose/state';
import { mountEditor } from './jsdom-editor.mjs';
import { settleDocument } from '../src/settle.js';
import { setSpellRanges } from '../src/spell-decorate.js';
import { beginLoad, endLoad, showLineNumbers, changedSinceLoad } from '../src/line-map.js';

let ed;
before(async () => { ed = await mountEditor(); });

test('a load, its heading ids, a selection and decorations leave the document unchanged since the load; an edit changes it', () => {
  const md = '# Same\n\ntext\n\n# Same\n\n- a list, so settling adds a paragraph\n';   // two headings alike: ids to sync and de-duplicate
  beginLoad(); ed.editor.action(replaceAll(md, true)); settleDocument(ed.view()); endLoad(ed.view().state.doc, md);   // as MDM.setMarkdown
  const view = ed.view(), changed = () => changedSinceLoad(ed.view().state.doc);
  assert.equal(changed(), false);
  view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 3)));
  setSpellRanges(view, [{ from: 2, to: 5 }]);
  showLineNumbers(true); showLineNumbers(false);
  view.dispatch(view.state.tr.setMeta('addToHistory', false));
  assert.equal(changed(), false);
  view.dispatch(view.state.tr.insertText('x', 3));
  assert.equal(changed(), true);
});
