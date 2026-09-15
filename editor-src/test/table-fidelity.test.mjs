// A table is saved in the layout it was read in, and an empty cell stays empty (conventions.js, tableFidelity). Synthetic content.
import test, { before } from 'node:test';
import assert from 'node:assert/strict';
import { Selection } from '@milkdown/kit/prose/state';
import { callCommand } from '@milkdown/kit/utils';
import { insertTableCommand } from '@milkdown/kit/preset/gfm';
import { mountEditor } from './jsdom-editor.mjs';

let ed;
before(async () => { ed = await mountEditor(); });
// The diagnosis's repro: 2 columns, 20 rows, one 2,000-character cell.
const WIDE = `| r | x |\n| - | - |\n${Array.from({ length: 20 }, (_, i) => `| r${i} | ${i === 9 ? 'w'.repeat(2000) : 'x'} |`).join('\n')}\n`;
const ALIGNED = '| Name  | Qty |\n| :---- | --: |\n| apple |   3 |\n| fig   |  12 |\n';
const UNALIGNED = '| Name | Qty |\n| :- | -: |\n| kiwi | 3 |\n| pear | 12 |\n\n| h | i |\n| :-: | - |\n| a | b |\n';   // the second lines up but for its delimiter row
const PRETTIER = '| a   | b   |\n| --- | --- |\n| 1   | 2   |\n';
// [name, markdown; then, beside an edit outside the table, the text to type in front of, what to type, and the markdown after]
const CASES = [
  ['aligned', ALIGNED],
  ['aligned, a cell grows', ALIGNED, 'fig', 'big ', '| Name    | Qty |\n| :------ | --: |\n| apple   |   3 |\n| big fig |  12 |\n'],
  ['unaligned', UNALIGNED],
  ['unaligned, a hard break in a cell is a space as before', UNALIGNED, 'wi', (schema) => schema.nodes.hardbreak.create(), UNALIGNED.replace('kiwi', 'ki wi')],
  ['GitHub styles', '| a | b |\n| --- | --- |\n| 1 | 2 |\n\n|a|b|\n|---|---|\n|1|2|\n\n| a | b |\n| :-- | --: |\n| 1 | 2 |\n\n| a | b |\n|---|---|\n| c | d |\n'],
  ['wide-cell unaligned', WIDE],
  ['empty cells', '| h1 | h2 |\n| -- | -- |\n| 1  |    |\n\n| h1 | h2 | h3 |\n| - | :-: | - |\n| 1 | | x |\n| | | |\n'],
  ['unpadded aligned, a cell grows', '|aa|b|\n|--|-|\n|cc|d|\n', 'cc', 'c', '|aa |b|\n|---|-|\n|ccc|d|\n'],
  ['unpadded aligned, a space before a closing pipe', '|a |b|\n|--|-|\n|cc|d|\n'],
  ['Prettier', PRETTIER],
  ['Prettier, a cell grows past its width', PRETTIER, '1', 'wxyz', '| a     | b   |\n| ----- | --- |\n| wxyz1 | 2   |\n'],
  ['a column narrower than its delimiter', '| # | Step |\n| --- | ---- |\n| 1 | Go |\n'],
];
const wrap = (md) => `Intro.\n\n${md}\nEnd.\n`;
const typeAt = (find, text) => {   // in front of the first text holding `find`: a string, or a node made from the schema
  const { state } = ed.view(); let at = -1;
  state.doc.descendants((n, pos) => { if (at < 0 && n.isText && n.text.includes(find)) at = pos + n.text.indexOf(find); });
  ed.view().dispatch(typeof text === 'string' ? state.tr.insertText(text, at) : state.tr.insert(at, text(state.schema)));
};
for (const [name, md, find, text, edited = md] of CASES) {
  test(`${name}: byte-identical; edited, saved, reopened and saved again`, () => {
    assert.equal(ed.roundTrip(wrap(md)), wrap(md));
    typeAt('Intro', 'New ');
    if (find) typeAt(find, text);
    const saved = ed.markdown();
    assert.equal(saved, `New ${wrap(edited)}`);
    assert.equal(ed.roundTrip(saved), saved);
  });
}
test('wide-cell repro: the save is the size of the file (2,249 bytes; 44,242 before)', () => {
  assert.equal(Buffer.byteLength(ed.roundTrip(WIDE)), 2249);
});
test('a new table: aligned, columns at least 3 wide, up to 80 characters in a cell; unaligned past it; empty cells stay empty', () => {
  for (const [typed, table] of [   // new cells are left-aligned
    ['c', '| c   |     |\n| :-- | :-- |\n|     |     |\n'],
    ['c'.repeat(80), `| ${'c'.repeat(80)} |     |\n| :${'-'.repeat(79)} | :-- |\n| ${' '.repeat(80)} |     |\n`],
    ['c'.repeat(81), `| ${'c'.repeat(81)} | |\n| :-- | :-- |\n| | |\n`],
  ]) {
    ed.roundTrip('para\n');
    ed.view().dispatch(ed.view().state.tr.setSelection(Selection.atEnd(ed.view().state.doc)));
    ed.editor.action(callCommand(insertTableCommand.key, { row: 2, col: 2 }));
    const { state } = ed.view(); let at = -1;
    state.doc.descendants((n, pos) => { if (n.type.name === 'table') at = pos; });
    ed.view().dispatch(state.tr.setSelection(Selection.findFrom(state.doc.resolve(at), 1, true)).insertText(typed));
    assert.equal(ed.markdown(), `para\n\n${table}\n`);
  }
});
