// Minimal table editing for the WYSIWYG view: a right-click context menu with the
// only structure-changing actions (insert/delete/select column/row/table), plus
// "clear cell content on Backspace/Delete" and "typing replaces selected cells".
// Anything more elaborate is left to direct markdown editing.

import { $prose, callCommand } from '@milkdown/kit/utils';
import { Plugin, TextSelection } from '@milkdown/kit/prose/state';
import {
  CellSelection, selectionCell, selectedRect, isInTable,
  addColumnBefore, addColumnAfter, addRowBefore, addRowAfter,
  deleteColumn, deleteRow, deleteTable, deleteCellSelection,
} from '@milkdown/kit/prose/tables';
import { insertTableCommand } from '@milkdown/kit/preset/gfm';

const selectCol = (state, dispatch) => {
  if (!isInTable(state)) return false;
  if (dispatch) dispatch(state.tr.setSelection(CellSelection.colSelection(selectionCell(state))));
  return true;
};
const selectRow = (state, dispatch) => {
  if (!isInTable(state)) return false;
  if (dispatch) dispatch(state.tr.setSelection(CellSelection.rowSelection(selectionCell(state))));
  return true;
};
const selectTable = (state, dispatch) => {
  if (!isInTable(state)) return false;
  const rect = selectedRect(state);
  const cells = rect.map.map;
  const first = rect.tableStart + cells[0];                 // top-left cell
  const last = rect.tableStart + cells[cells.length - 1];   // bottom-right cell
  if (dispatch) dispatch(state.tr.setSelection(CellSelection.create(state.doc, first, last)));
  return true;
};

const MENU = [
  ['Insert column left', addColumnBefore],
  ['Insert column right', addColumnAfter],
  ['Insert row above', addRowBefore],
  ['Insert row below', addRowAfter],
  ['—'],
  ['Delete column', deleteColumn],
  ['Delete row', deleteRow],
  ['Delete table', deleteTable],
  ['—'],
  ['Select column', selectCol],
  ['Select row', selectRow],
  ['Select table', selectTable],
];

function posInTable(view, clientX, clientY) {
  const at = view.posAtCoords({ left: clientX, top: clientY });
  if (!at) return null;
  const $p = view.state.doc.resolve(at.pos);
  for (let d = $p.depth; d > 0; d--) if ($p.node(d).type.spec.tableRole) return $p;
  return null;
}

export function installTableContextMenu(view) {
  const menu = document.createElement('div');
  menu.className = 'mdm-table-menu';
  menu.style.display = 'none';
  for (const [label, cmd] of MENU) {
    if (!cmd) { const s = document.createElement('div'); s.className = 'mdm-menu-sep'; menu.appendChild(s); continue; }
    const item = document.createElement('div');
    item.className = 'mdm-menu-item';
    item.textContent = label;
    item.addEventListener('mousedown', (e) => {
      e.preventDefault();
      hide();
      cmd(view.state, view.dispatch);
      view.focus();
    });
    menu.appendChild(item);
  }
  document.body.appendChild(menu);
  const hide = () => { menu.style.display = 'none'; };

  view.dom.addEventListener('contextmenu', (e) => {
    const $p = posInTable(view, e.clientX, e.clientY);
    if (!$p) { hide(); return; }
    e.preventDefault();
    // Target the clicked cell unless a multi-cell selection is already active.
    if (!(view.state.selection instanceof CellSelection)) {
      view.dispatch(view.state.tr.setSelection(TextSelection.near($p)));
    }
    menu.style.left = `${e.clientX}px`;
    menu.style.top = `${e.clientY}px`;
    menu.style.display = 'block';
  });
  document.addEventListener('mousedown', (e) => { if (!menu.contains(e.target)) hide(); });
  document.addEventListener('scroll', hide, true);
  window.addEventListener('blur', hide);
}

// Backspace/Delete clears selected cells; typing replaces their content.
export const tableCellEditing = $prose(() => new Plugin({
  props: {
    handleKeyDown(view, event) {
      if (event.key !== 'Backspace' && event.key !== 'Delete') return false;
      if (!(view.state.selection instanceof CellSelection)) return false;
      return deleteCellSelection(view.state, view.dispatch);
    },
    handleTextInput(view, _from, _to, text) {
      const sel = view.state.selection;
      if (!(sel instanceof CellSelection)) return false;
      const anchorPos = sel.$anchorCell.pos;
      const tr = view.state.tr;
      const cells = [];
      sel.forEachCell((node, pos) => cells.push({ node, pos }));
      const paragraph = view.state.schema.nodes.paragraph;
      for (let i = cells.length - 1; i >= 0; i--) {
        const { node, pos } = cells[i];
        tr.replaceWith(pos + 1, pos + node.nodeSize - 1, paragraph.create());
      }
      const inner = tr.mapping.map(anchorPos) + 2; // inside anchor cell's new paragraph
      tr.setSelection(TextSelection.create(tr.doc, inner));
      tr.insertText(text, inner);
      view.dispatch(tr.scrollIntoView());
      return true;
    },
  },
}));

// Insert a table. GFM tables always have a header row (row 0); when the caller does
// not want one we add an extra row so the requested body-row count still fits.
export function insertTableAction(editor, rows, cols, header) {
  const total = header ? rows : rows + 1;
  editor.action(callCommand(insertTableCommand.key, { row: Math.max(total, 2), col: Math.max(cols, 1) }));
}
