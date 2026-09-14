// Markdown line numbers for the formatted view (#10): the caret's line and column for
// the status bar, and where Go to Line puts the caret.
//
// An untouched document is numbered by the text it was loaded from (the file on open,
// the source box's text after Ctrl+E), read from Milkdown's own parse (lineCapture).
// Once edited, or saved (forgetLoad), it is numbered by the markdown the editor would
// save, read from the serialiser's own line tracking (recordingRoot, in conventions.js).
// Either list of blocks is paired in order with the ProseMirror blocks, and a list that
// does not pair numbers nothing rather than something wrong. Between rebuilds lineMap
// keeps the positions on their blocks, so the column and the line inside a block stay
// live; block start lines catch up at the next markdownWithLines (main.js getMarkdown,
// which the host calls once typing pauses, not after every keystroke). An edit that can
// move lines (anything but typing inside one text block, or typing a line break) leaves
// the blocks from where it happened on without numbers until that read (staleFrom).
import { $prose, $remark } from '@milkdown/kit/utils';
import { Plugin, PluginKey, Selection } from '@milkdown/kit/prose/state';
import { ReplaceStep, AddMarkStep, RemoveMarkStep } from '@milkdown/kit/prose/transform';

// The mdast blocks that hold blocks, and the ProseMirror block each mdast block becomes.
const CONTAINERS = new Set(['root', 'blockquote', 'list', 'listItem', 'footnoteDefinition']);
const BLOCK = {
  paragraph: 'paragraph', heading: 'heading', code: 'code_block', html: 'html', thematicBreak: 'hr', table: 'table',
  blockquote: 'blockquote', list: '_list', listItem: 'list_item', footnoteDefinition: 'footnote_definition',
};
const PM_CONTAINERS = new Set(['blockquote', 'bullet_list', 'ordered_list', 'list_item', 'footnote_definition']);
const segmenter = new Intl.Segmenter();

let recording = null;   // blocks recorded while the serialiser runs
let capturing = false;  // a document is being loaded, and
let captured = null;    // the blocks its parse found
let current = null;     // { doc, entries: [{ pos, type, line, skip }], lines }: the numbering in force
let staleFrom = null;   // blocks starting here or later may have moved since it was built

const linesIn = (text) => text.split(/\r\n|\r|\n/).length;
const chars = (s) => (/^[\x00-\x7f]*$/.test(s) ? s.length : [...segmenter.segment(s)].length);
// An inline item's text for counting lines: a break's newline; raw HTML from its first line
// break on, so its lines count and the line it ends on counts its characters (one line: none).
const leafText = (n) => (n.type.name === 'html' ? n.attrs.value.slice(n.attrs.value.search(/\n|$/)) : n.type.spec.leafText?.(n) ?? '');

/** The serialiser's root handler, wrapped: while recording, each block is recorded with the line it is written on. */
export const recordingRoot = (base) => (node, parent, state, info) => {
  if (!recording) return base(node, parent, state, info);
  const handlers = { ...state.handlers };
  for (const [type, handle] of Object.entries(handlers)) {
    if (type === 'root' || typeof handle !== 'function') continue;
    const wrapped = (n, p, s, i) => {
      if (p && CONTAINERS.has(p.type)) recording.push({ type: n.type, line: i.now.line, skip: n.type === 'code' ? 1 : 0 });
      return handle(n, p, s, i);
    };
    wrapped.peek = handle.peek;
    state.handlers[type] = wrapped;
  }
  try { return base(node, parent, state, info); } finally { Object.assign(state.handlers, handlers); }
};

/** Milkdown's parse of a document being loaded: each block with the line it starts on. */
export const lineCapture = $remark('mdmLineCapture', () => () => (tree) => {
  if (!capturing) return;
  captured = [];
  const walk = (node) => {
    for (const c of node.children || []) {
      if (!BLOCK[c.type]) continue;   // a link definition, say: no block of its own
      const { start, end } = c.position || {};
      const fenced = c.type === 'code' && start && end.line - start.line + 1 > c.value.split('\n').length;
      captured.push({ type: c.type, line: start?.line, skip: fenced ? 1 : 0 });
      if (CONTAINERS.has(c.type)) walk(c);
    }
  };
  walk(tree);
});

export function beginLoad() { capturing = true; captured = null; }

/** The document is installed: number it by the text it was loaded from. */
export function endLoad(doc, text) { capturing = false; staleFrom = null; current = pair(doc, captured, linesIn(text)); }

/** A save has made the file the saved markdown: number by that from the next read (MDM.lineBaseSaved). */
export function forgetLoad() { current = null; }

function pair(doc, records, lines) {
  if (!records) return null;
  const entries = [];
  let ok = true;
  const walk = (node, start) => node.forEach((child, offset) => {
    const r = records[entries.length], type = child.type.name;
    ok &&= r ? r.line >= 1 && type.endsWith(BLOCK[r.type]) : type === 'paragraph' && !child.content.size;
    entries.push({ pos: start + offset, type, line: Math.min(r?.line ?? lines, lines), skip: r?.skip ?? 0 });
    if (PM_CONTAINERS.has(type)) walk(child, start + offset + 1);
  });
  walk(doc, 0);
  return ok && entries.length >= records.length ? { doc, entries, lines } : null;
}

/** The markdown the editor would save, rebuilding the numbering from it when the document has changed since. */
export function markdownWithLines(doc, serialize) {
  if (current?.doc === doc) return { markdown: serialize(), rebuilt: false };
  recording = [];
  try {
    const markdown = serialize();
    current = pair(doc, recording, linesIn(markdown)); staleFrom = null;
    return { markdown, rebuilt: true };
  } finally {
    recording = null;
  }
}

/** The numbering for `doc`, rebuilt first when it is stale (Go to Line), or null. */
export function ensureLines(doc, serialize) {
  if (current?.doc !== doc) markdownWithLines(doc, serialize);
  return current;
}

/** Where `step` may move lines, in the doc before it: its start; its text block's end for typing with a line break; null for none. */
function moved(step, doc) {
  const { slice } = step, n = doc.nodeAt(step.from ?? 0), $from = doc.resolve(step.from ?? 0);
  if (step instanceof AddMarkStep || step instanceof RemoveMarkStep) return null;
  if (n?.isTextblock && step.gapFrom - step.from === 1 && step.to - step.gapTo === 1 && n.nodeSize === step.to - step.from
    && step.insert === 1 && slice.size === 2 && slice.content.firstChild.type === n.type) return null;   // attributes only: a heading's id
  if (!(step instanceof ReplaceStep)) return step.from ?? step.pos ?? 0;
  let f = slice.content, depth = 0;   // a slice open at both ends around one text block (a paste) is inline too
  while (depth < slice.openStart && f.childCount === 1) { f = f.firstChild.content; depth++; }
  if (depth !== slice.openStart || depth !== slice.openEnd || f.firstChild?.isBlock
    || !$from.parent.isTextblock || !$from.sameParent(doc.resolve(step.to))) return step.from;
  const text = doc.textBetween(step.from, step.to, '', leafText) + f.textBetween(0, f.size, '', leafText);
  return text.includes('\n') ? $from.end() : null;
}

/** Keeps the numbering's positions on their blocks through each edit, until the next rebuild. */
export const lineMap = $prose(() => new Plugin({
  key: new PluginKey('mdmLineMap'),
  state: {
    init: () => null,
    apply(tr) {
      if (tr.docChanged && current) for (const e of current.entries) e.pos = tr.mapping.map(e.pos);
      if (current) tr.steps.forEach((step, i) => {
        const at = moved(step, tr.docs[i]), map = step.getMap();
        if (staleFrom !== null) staleFrom = map.map(staleFrom, -1);
        if (at !== null) staleFrom = Math.min(staleFrom ?? Infinity, map.map(at, -1));
      });
      return null;
    },
  },
}));

/** The last entry `test` accepts: entries are in document order, so positions and lines only grow. */
function last(test) {
  let lo = 0, hi = current.entries.length - 1, found = null;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (test(current.entries[mid])) { found = current.entries[mid]; lo = mid + 1; } else hi = mid - 1;
  }
  return found;
}

/** The caret's markdown line and column, or {} when the document or the caret's block has no numbering. */
export function lineStatus(state) {
  const $head = state.selection.$head;
  const e = current && last((en) => en.pos <= $head.pos);
  let d = $head.depth;   // the caret's text block, or its table, numbered only when no edit since the rebuild could have moved it
  if (e?.type === 'table') while (d > 0 && $head.node(d).type.name !== 'table') d--;
  if (!e || !d || $head.before(d) >= (staleFrom ?? Infinity)) return {};
  let text = $head.parent.isTextblock ? $head.parent.textBetween(0, $head.parentOffset, undefined, leafText) : '';
  let line = e.line;
  if (e.type === 'table') {   // a table row is a line; the delimiter row follows the header
    for (let d = $head.depth; d > 0; d--) {
      if ($head.node(d).type.name.endsWith('_row')) { line += $head.index(d - 1) && $head.index(d - 1) + 1; break; }
    }
  } else {
    const rows = text.split('\n');   // a break, soft or hard, or a code line
    line += rows.length - 1 + (e.type === 'code_block' ? e.skip : 0);
    text = rows[rows.length - 1];
  }
  return { line: Math.min(line, current.lines), col: chars(text) + 1 };
}

/** Where Go to Line puts the caret for `want`, clamped to the document: in the deepest block starting on or before it. */
export function lineTarget(doc, want) {
  if (!current) return null;
  const line = Math.max(1, Math.min(Math.trunc(want) || 1, current.lines));
  const e = last((en) => en.line <= line) || current.entries[0];
  const node = doc.nodeAt(e.pos);
  let left = line - e.line;
  if (e.type === 'table') {
    const row = Math.min(Math.max(left - 1, 0), node.childCount - 1);
    let p = e.pos + 1;
    for (let i = 0; i < row; i++) p += node.child(i).nodeSize;
    return Selection.near(doc.resolve(p + 1)).head;
  }
  if (!node.isTextblock) return Selection.near(doc.resolve(e.pos + 1)).head;
  left -= e.type === 'code_block' ? e.skip : 0;
  let p = left > 0 ? e.pos + node.nodeSize - 1 : e.pos + 1;   // past its last line: the end of the block
  node.forEach((child, offset) => {
    const text = child.isText ? child.text : leafText(child);
    for (let i = text.indexOf('\n'); left > 0 && i >= 0; i = text.indexOf('\n', i + 1)) {
      if (--left === 0) p = e.pos + 1 + offset + Math.min(i + 1, child.nodeSize);   // a line inside raw HTML: right after it
    }
  });
  return p;
}
