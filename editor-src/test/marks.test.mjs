// Formatting marks (src/marks.js), through the shipped editor in jsdom: an edit keeps the ¶ already drawn.
import test from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';

test("a keystroke keeps every ¶ node already drawn, the edited paragraph's included", async () => {
  const ed = await mountEditor('# Title\n\nOne\n\nTwo\n\nThree\n');
  const marks = () => [...ed.view().dom.querySelectorAll('.mdm-mark-para')], before = marks();
  ed.view().dispatch(ed.view().state.tr.insertText('x', 9));   // One -> Oxne
  assert.equal(ed.view().state.doc.child(1).textContent, 'Oxne');
  assert.deepEqual(marks().map((n, i) => n === before[i]), [true, true, true, true]);
});
