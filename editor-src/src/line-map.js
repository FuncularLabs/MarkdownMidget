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
// Go to Line reaches every line: one with no place of its own reads as itself where the caret went, until it moves or is saved (pinLine).
import { $prose, $remark } from '@milkdown/kit/utils';
import { Plugin, PluginKey, Selection } from '@milkdown/kit/prose/state';
import { ReplaceStep, AddMarkStep, RemoveMarkStep } from '@milkdown/kit/prose/transform';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';

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
let gutter = false;     // View ▸ Line Numbers: numbers in the margin (gutterNumbers)
let gutterView = null;  // the view they are drawn in
let drawn = {};         // the margin last built: { doc, current, staleFrom } it was built from, and its set
let pin = null;         // Go to Line's line for a caret on a line with no place of its own: { doc, head, line } (pinLine)
let settled = null;     // the document the last load installed (settledMarkdown)

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
export function endLoad(doc, text) { settled = doc; capturing = false; staleFrom = null; pin = null; current = pair(doc, captured, linesIn(text)); if (gutter) redraw(); }

/** View ▸ Line Numbers: show or hide the numbers in the margin. The setting it already has redraws nothing. */
export function showLineNumbers(on) { if (gutter === !!on) return; gutter = !!on; redraw(); }

// The margin follows a new numbering at once: a transaction with no steps is no edit, no history and no change for the host.
const redraw = () => gutterView?.dispatch(gutterView.state.tr);

/** A save has made the file the saved markdown: number by that from the next read (MDM.lineBaseSaved). */
export function forgetLoad() { current = null; pin = null; }

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

// The same blocks at the same positions on the same lines: the margin drawn from one is the margin drawn from the other.
const sameNumbers = (a, b) => a === b || (!!a && !!b && a.lines === b.lines && a.entries.length === b.entries.length
  && a.entries.every((e, i) => e.pos === b.entries[i].pos && e.type === b.entries[i].type && e.line === b.entries[i].line));

/** The markdown the editor would save, rebuilding the numbering from it when the document has changed since; the margin is
 *  redrawn only when that changed what it shows: blocks that were waiting for their numbers (staleFrom), or other numbers. */
export function markdownWithLines(doc, serialize) {
  if (current?.doc === doc) return { markdown: serialize(), rebuilt: false };
  recording = [];
  try {
    const markdown = serialize(), before = current, waiting = staleFrom !== null;
    current = pair(doc, recording, linesIn(markdown)); staleFrom = null;
    if (gutter && (waiting || !sameNumbers(before, current))) redraw();
    return { markdown, rebuilt: true };
  } finally {
    recording = null;
  }
}

/** The markdown of the document the last load installed (MDM.getSettledMarkdown, the host's clean baseline, read after the first paint):
 *  while untouched, the read above; once edited, that document's own markdown, leaving the live numbering to the next read. */
export const settledMarkdown = (doc, serialize) => (settled && settled !== doc ? { markdown: serialize(settled), rebuilt: false } : markdownWithLines(doc, serialize));

/** Whether `doc` differs, node for node, from the document the last load installed (MDM.changedSinceLoad, the host's dirty check): a selection,
 *  a decoration or a transaction with no steps makes no new document, and an edit undone makes an equal one. Nodes an edit left alone are shared,
 *  so the comparison stops at them: no serialising, and far less than the whole document. */
export const changedSinceLoad = (doc) => settled !== doc && !settled?.eq(doc);

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
    && step.insert === 1 && slice.size === 2 && slice.content.firstChild.type === n.type) return null;   // attributes only: a code block's language, say
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
      if (pin && (tr.docChanged || tr.selection.head !== pin.head)) pin = null;
      return null;
    },
  },
  view: (v) => { gutterView = v; return { destroy: () => { if (gutterView === v) gutterView = null; } }; },
  props: {
    attributes: () => (gutter ? { class: 'mdm-line-numbers' } : {}),   // list items give up the vendor's position (structure.css)
    decorations: (state) => gutterNumbers(state.doc),
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
  if (pin?.doc === state.doc && pin.head === $head.pos) return { line: pin.line, col: 1 };
  const e = current && last((en) => en.pos <= $head.pos);
  // The caret's text block, or its table, numbered only by its own entry (a merged-away block's entry maps
  // into the block that took its text) and only when no edit since the rebuild could have moved it (staleFrom).
  let d = $head.depth;
  if (e?.type === 'table') while (d > 0 && $head.node(d).type.name !== 'table') d--;
  if (!e || !d || $head.before(d) !== e.pos || $head.before(d) >= (staleFrom ?? Infinity)) return {};
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

/** After Go to Line `want`: a line with no place of its own (a blank line, a fence, a rule) reads as itself until the caret or the document moves, or a save or load renumbers it. */
export function pinLine(state, want) {
  pin = null;
  const line = current && Math.max(1, Math.min(Math.trunc(want) || 1, current.lines));
  if (line && lineStatus(state).line !== line) pin = { doc: state.doc, head: state.selection.head, line };
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
  if (node.isLeaf) return e.pos;   // a rule: Selection.near selects it
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

const run = (from, to) => (to - from > 1 ? `${from}–${to}` : from < to ? `${from} ${to}` : `${from}`);   // 2, 2 3, 2–4
const range = (from, to) => (from < to ? `${from}–${to}` : `${from}`);   // 7, 7–8
// A gap's label stands in the 16px margin under the block above it, clear of that block's numbers (structure.css). Under anything
// else (a rule, a table, a mermaid diagram, a list item in its list) there is no room: its lines lead the next block's range.
const roomy = (n) => !n || (/^(paragraph|heading|blockquote|bullet_list|ordered_list|code_block)$/.test(n.type.name) && !/^mermaid$/i.test(n.attrs.language ?? ''));

/** Once per numbering, while shown, for each block numbered (a top-level block, a list item, or the first block after a numbered item nested
 *  deeper in its container; not one on a line already numbered, nor the empty last paragraph of a file with no final newline):
 *  its lines, led by a gap with no room of its own; that gap's label when it has room (null: after the last block); its first line
 *  alone, for while an edit inside it waits for the read; and the entry numbered next. Every line is in one label, in order. */
function labelsOf(doc) {
  if (current.labels) return current.labels;
  const labels = new Map(), { entries, lines } = current;
  const lastLine = (e, n = doc.nodeAt(e.pos)) => e.line + (n?.type.name !== e.type ? 0 : e.type === 'table' ? n.childCount   // as lineStatus counts
    : n.isTextblock ? n.textBetween(0, n.content.size, undefined, leafText).split('\n').length - 1 + (e.type === 'code_block' ? e.skip + (n.content.size ? e.skip : 0) : 0) : 0);   // and a closing fence after any code
  let open = null, above = null;   // the label being built; the top-level block above
  entries.forEach((e, i) => {   // the entry before a block is the last block inside the one above it
    const depth = doc.resolve(e.pos).depth, top = !depth, after = i && lastLine(entries[i - 1]);
    if ((!top && e.type !== 'list_item' && !(depth < open?.depth)) || e.line === open?.line || (e.line <= after && !doc.nodeAt(e.pos)?.content.size)) return;
    const room = top && roomy(above);
    if (open) Object.assign(open, { text: range(open.from, Math.min(after, e.line - 1)), next: e });
    labels.set(e, open = { line: e.line, depth, from: room ? e.line : Math.min(after + 1, e.line), gap: room && e.line > after + 1 ? run(after + 1, e.line - 1) : null });
    open.cut = range(open.from, e.line);
    if (top) above = doc.nodeAt(e.pos);
  });
  const end = Math.min(lines, entries.length ? lastLine(entries[entries.length - 1]) : lines);
  if (end < lines && roomy(above)) labels.set(null, { gap: run(end + 1, lines) });
  if (open) open.text = range(open.from, roomy(above) ? end : lines);
  return (current.labels = labels);
}

/** The margin's numbers: the blocks labelsOf numbers, where lineStatus would number them (before staleFrom, by their
 *  own entry), none on a line already numbered, as a list's first item is, and none while a load is pairing. */
function gutterNumbers(doc) {
  if (!gutter || !current || capturing) return null;
  if (drawn.doc === doc && drawn.current === current && drawn.staleFrom === staleFrom) return drawn.set;   // nothing moved
  const decos = [], labels = labelsOf(doc);
  // A label is a widget of no height the margins collapse through, after any other widget there (a mermaid diagram's is side 1).
  const label = (pos, text) => Decoration.widget(pos, () => { const d = document.createElement('div'); d.dataset.gap = text; return d; }, { side: 2, key: `mdm-gap:${text}` });
  for (const e of current.entries) {
    const node = e.pos < Math.min(staleFrom ?? Infinity, doc.content.size) ? doc.nodeAt(e.pos) : null;
    if (node?.type.name !== e.type || !labels.has(e)) continue;
    // The next block's start mapped onto staleFrom (it was merged into this one) moved this one's end too: its first line alone.
    const end = e.pos + node.nodeSize, l = labels.get(e), text = staleFrom !== null && staleFrom <= (l.next?.pos ?? Infinity) ? l.cut : l.text;
    if (l.gap) decos.push(label(e.pos, l.gap));
    decos.push(Decoration.node(e.pos, end, { 'data-line': text }));
    // A mermaid block's code is hidden until the caret is in it, so its number also stands before its diagram.
    if (/^mermaid$/i.test(node.attrs.language ?? '')) decos.push(Decoration.widget(end, () => { const s = document.createElement('span'); s.dataset.line = text; return s; }, { side: -1, key: `mdm-line:${text}` }));
  }
  if (labels.has(null) && staleFrom === null) decos.push(label(doc.content.size, labels.get(null).gap));
  drawn = { doc, current, staleFrom, set: DecorationSet.create(doc, decos) };
  return drawn.set;
}
