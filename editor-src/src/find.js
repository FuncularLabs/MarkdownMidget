// Find, and since #5 Replace, for the WYSIWYG view.
//
// The host builds a JS RegExp source string (and flags) from its FindEngine and
// calls findReset(...), which scans the .mdm-prosemirror text nodes, builds an
// index, and returns the total match count. findNext()/findPrev() cycle through
// matches, scrolling and highlighting via the browser selection.
//
// findReplace / findReplaceAll turn a match's DOM range into ProseMirror
// positions and change the document through the view, so a replacement is a real
// transaction: undoable, and a whole Replace All is one transaction and so one
// undo step. The host hands over the replacement already prepared for the search
// mode — verbatim text, or a template for Regex mode (expandTemplate).

import { TextSelection } from '@milkdown/kit/prose/state';

let matches = [];     // [{startNode, startOffset, endNode, endOffset, start, end, exec}]
let cursor = -1;      // index into matches

// What the index was built from, so findReset can tell "the same search again"
// from a new search or a changed document: the host re-issues findReset on every
// change message, including the ones raised by the replacements made here, and
// those must not lose the place.
let indexed = { source: null, flags: null, doc: null, regex: null };

// The selection captured when the Find dialog opened (findCaptureScope), as
// ProseMirror positions mapped through the replacements made here. Replace All
// falls back to it once Find's own selection has taken the place of the user's.
let capturedScope = null;   // { from, to, doc }

function getRoot() {
  return document.querySelector('.mdm-prosemirror');
}

function buildIndex() {
  const root = getRoot();
  if (!root) return { text: '', nodes: [] };
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode(n) {
      const parent = n.parentElement;
      if (!parent) return NodeFilter.FILTER_REJECT;
      // Injected widgets / marks / our print-source pre — never search.
      if (parent.closest('.mdm-mark, .mdm-mermaid, .mdm-print-source-pre'))
        return NodeFilter.FILTER_REJECT;
      // Mermaid source <pre> is hidden by default but its text nodes are still in
      // the DOM. Reject anything inside an element with display:none (also covers
      // collapsed details, hidden draft regions, etc.).
      let el = parent;
      while (el && el !== root) {
        const style = window.getComputedStyle(el);
        if (style.display === 'none' || style.visibility === 'hidden')
          return NodeFilter.FILTER_REJECT;
        el = el.parentElement;
      }
      return NodeFilter.FILTER_ACCEPT;
    },
  });
  let text = '';
  const nodes = [];
  let node;
  while ((node = walker.nextNode())) {
    nodes.push({ node, start: text.length, end: text.length + node.nodeValue.length });
    text += node.nodeValue;
  }
  return { text, nodes };
}

// The text node holding a character offset. A match START wants the node that
// holds the character at the offset (so a match beginning where one node ends
// and the next starts lands in the next one); a match END is fine at the end of
// the node before it.
function locate(nodes, offset, atStart) {
  // Binary search would be nice, but linear is fine for a one-shot find.
  for (let i = 0; i < nodes.length; i++) {
    if (atStart ? offset < nodes[i].end : offset <= nodes[i].end)
      return { node: nodes[i].node, offset: offset - nodes[i].start };
  }
  const last = nodes[nodes.length - 1];
  return last ? { node: last.node, offset: last.node.nodeValue.length } : null;
}

/// Returns { total, current }. Resets the search using a regex described as
/// (source, flags). If query is empty, clears matches. With the view given, the
/// same source and flags on an unchanged document keep the index and the current
/// match instead of starting over.
export function findReset(source, flags, view) {
  if (view && source && source === indexed.source && flags === indexed.flags && view.state.doc === indexed.doc)
    return { total: matches.length, current: cursor + 1 };
  matches = [];
  cursor = -1;
  indexed = { source: null, flags: null, doc: null, regex: null };
  if (!source) return { total: 0, current: 0 };
  let re;
  try { re = new RegExp(source, flags); }
  catch { return { total: 0, current: 0, error: 'Invalid pattern' }; }
  indexed = { source, flags, doc: view ? view.state.doc : null, regex: re };
  reindex();
  return { total: matches.length, current: 0 };
}

// Rebuild the match list from the DOM with the indexed regex. Leaves cursor at -1.
function reindex() {
  matches = [];
  cursor = -1;
  const re = indexed.regex;
  if (!re) return;
  re.lastIndex = 0;
  const { text, nodes } = buildIndex();
  if (!text) return;

  // Guard against zero-width matches infinite looping.
  let m;
  let safety = 0;
  while ((m = re.exec(text)) !== null) {
    if (safety++ > 50000) break;
    const start = m.index;
    const end = m.index + m[0].length;
    if (m[0].length === 0) { re.lastIndex = end + 1; continue; }
    const a = locate(nodes, start, true);
    const b = locate(nodes, end, false);
    if (a && b) matches.push({ startNode: a.node, startOffset: a.offset, endNode: b.node, endOffset: b.offset, start, end, exec: m });
  }
}

function applySelection(match) {
  if (!match) return;
  const sel = window.getSelection();
  const range = document.createRange();
  try {
    range.setStart(match.startNode, match.startOffset);
    range.setEnd(match.endNode, match.endOffset);
    sel.removeAllRanges();
    sel.addRange(range);
    const el = match.startNode.parentElement;
    if (el && el.scrollIntoView) el.scrollIntoView({ block: 'center', behavior: 'instant' });
  } catch (_) { /* ignore stale-DOM edge cases */ }
}

export function findNext(wrap) {
  if (matches.length === 0) return { total: 0, current: 0 };
  cursor = cursor + 1;
  if (cursor >= matches.length) cursor = wrap ? 0 : matches.length - 1;
  applySelection(matches[cursor]);
  return { total: matches.length, current: cursor + 1 };
}

export function findPrev(wrap) {
  if (matches.length === 0) return { total: 0, current: 0 };
  cursor = cursor - 1;
  if (cursor < 0) cursor = wrap ? matches.length - 1 : 0;
  applySelection(matches[cursor]);
  return { total: matches.length, current: cursor + 1 };
}

export function findClear() {
  matches = [];
  cursor = -1;
  indexed = { source: null, flags: null, doc: null, regex: null };
  capturedScope = null;
}

// ===== Replace (#5) =====

// A match's DOM range as ProseMirror positions, or null when the DOM has moved
// on (a node the index holds is no longer in the editor).
function pmRange(view, m) {
  const root = view.dom;
  if (!root.contains(m.startNode) || !root.contains(m.endNode)) return null;
  try {
    const from = view.posAtDOM(m.startNode, m.startOffset);
    const to = view.posAtDOM(m.endNode, m.endOffset);
    return from >= 0 && to >= from ? { from, to } : null;
  } catch (_) { return null; }
}

// The marks a replacement carries: those of the match's first character, the
// way typed text takes the formatting of what it replaces. A match that starts
// in plain text and runs into bold comes out plain; one starting inside a link
// or inline code stays in it.
function marksAt($from) {
  const after = $from.parent.maybeChild($from.index());
  return after ? after.marks : $from.marks();
}

function replaceRange(tr, from, to, text, marks) {
  if (text) tr.replaceWith(from, to, tr.doc.type.schema.text(text, marks));
  else tr.delete(from, to);
}

// Is this selection the one Find made by landing on the current match?
function isCurrentMatch(view, sel) {
  if (cursor < 0 || cursor >= matches.length) return false;
  const r = pmRange(view, matches[cursor]);
  return !!r && r.from === sel.from && r.to === sel.to;
}

// Bring the index up to date with a document changed behind the host's back
// (its own findReset normally does this first).
function ensureFresh(view) {
  if (indexed.doc && view.state.doc !== indexed.doc) {
    indexed.doc = view.state.doc;
    reindex();
  }
}

// After a transaction made here: the captured scope follows the change, and the
// index is rebuilt against the new document.
function afterChange(view, tr) {
  if (capturedScope) {
    capturedScope = {
      from: tr.mapping.map(capturedScope.from, -1),
      to: tr.mapping.map(capturedScope.to, 1),
      doc: view.state.doc,
    };
  }
  indexed.doc = view.state.doc;
  reindex();
}

// The Replace All scope. Selected text is the scope — unless it is Find's own
// selection of the current match, in which case the selection captured when the
// dialog opened is (if the document is still the one it was captured on). A
// caret, a node selection, or nothing captured means the whole document.
function resolveScope(view) {
  const sel = view.state.selection;
  if (sel.empty || sel.node) return null;
  if (!isCurrentMatch(view, sel)) return { from: sel.from, to: sel.to };
  if (capturedScope && capturedScope.doc === view.state.doc)
    return { from: capturedScope.from, to: capturedScope.to };
  return null;
}

/// Remember the current selection as the Replace All scope for this dialog
/// session (the host calls this when the Find dialog opens or is refocused).
/// A caret or a selected node drops what was kept; the selection Find itself
/// made keeps it (bringing the dialog back with Ctrl+F changes nothing). Returns
/// the range kept, or null.
export function findCaptureScope(view) {
  if (!view) { capturedScope = null; return null; }
  const sel = view.state.selection;
  if (sel.empty || sel.node) { capturedScope = null; return null; }
  if (isCurrentMatch(view, sel)) {
    return capturedScope && capturedScope.doc === view.state.doc
      ? { from: capturedScope.from, to: capturedScope.to }
      : null;
  }
  capturedScope = { from: sel.from, to: sel.to, doc: view.state.doc };
  return { from: sel.from, to: sel.to };
}

/// Expand a replacement template against one match, in the forms the host's
/// FindEngine documents for Regex mode: $1..$99 (two digits when that group
/// exists), ${name} and $<name>, $0 or $& for the whole match, $$ for a dollar
/// sign. A group that did not take part is empty; anything else stays literal.
export function expandTemplate(template, m) {
  let out = '';
  for (let i = 0; i < template.length; i++) {
    const c = template[i];
    if (c !== '$' || i + 1 >= template.length) { out += c; continue; }
    const n = template[i + 1];
    if (n === '$') { out += '$'; i++; continue; }
    if (n === '&') { out += m[0]; i++; continue; }
    if (n === '{' || n === '<') {
      const end = template.indexOf(n === '{' ? '}' : '>', i + 2);
      if (end > i + 2) {
        const name = template.slice(i + 2, end);
        if (m.groups && Object.prototype.hasOwnProperty.call(m.groups, name)) {
          out += m.groups[name] ?? '';
          i = end;
          continue;
        }
        if (/^\d+$/.test(name) && Number(name) < m.length) {
          out += m[Number(name)] ?? '';
          i = end;
          continue;
        }
      }
      out += c;
      continue;
    }
    if (n >= '0' && n <= '9') {
      const two = template.slice(i + 1, i + 3);
      if (/^\d\d$/.test(two) && Number(two) > 0 && Number(two) < m.length) {
        out += m[Number(two)] ?? '';
        i += 2;
        continue;
      }
      if (Number(n) < m.length) {
        out += m[Number(n)] ?? '';
        i += 1;
        continue;
      }
    }
    out += c;
  }
  return out;
}

/// Replace the current match, then move to the next one: { replaced, skipped,
/// total, current }. With no current match this is Find Next. A match that runs
/// across blocks is skipped (never joined); the following match is selected as
/// usual. `wrap` is Find's wrap-around: past the last match, the first.
export function findReplace(view, replacement, literal, wrap) {
  if (!view) return { replaced: 0, skipped: 0, total: matches.length, current: cursor + 1, error: 'no editor' };
  ensureFresh(view);
  if (cursor < 0 || cursor >= matches.length) return { replaced: 0, skipped: 0, ...findNext(!!wrap) };

  const m = matches[cursor];
  const range = pmRange(view, m);
  if (!range) {
    indexed.doc = view.state.doc;
    reindex();
    return { replaced: 0, skipped: 0, ...findNext(!!wrap) };
  }
  const { state } = view;
  const $from = state.doc.resolve(range.from);
  const $to = state.doc.resolve(range.to);
  if (!$from.sameParent($to)) return { replaced: 0, skipped: 1, ...findNext(!!wrap) };

  const text = literal ? replacement : expandTemplate(replacement, m.exec);
  const tr = state.tr;
  replaceRange(tr, range.from, range.to, text, marksAt($from));
  // A selection that was the match itself (F3 in the editor puts it there) would
  // be mapped onto the replacement text and pass for the user's own next time;
  // a caret after the replacement instead, as typing over a selection leaves.
  if (state.selection.from === range.from && state.selection.to === range.to)
    tr.setSelection(TextSelection.create(tr.doc, range.from + text.length));
  view.dispatch(tr);
  afterChange(view, tr);

  // Resume at the end of the replacement: the text before the match is as it
  // was, so its index offset still holds in the rebuilt index.
  const resume = m.start + text.length;
  cursor = matches.findIndex((x) => x.start >= resume);
  if (cursor < 0 && wrap && matches.length) cursor = 0;
  if (cursor >= 0) applySelection(matches[cursor]);
  return { replaced: 1, skipped: 0, total: matches.length, current: cursor + 1 };
}

/// Replace every match — within the scope (see resolveScope) — in ONE
/// transaction: { replaced, skipped, total, inSelection }. `total` is the match
/// count before replacing; `skipped` counts matches that run across blocks.
export function findReplaceAll(view, replacement, literal) {
  if (!view) return { replaced: 0, skipped: 0, total: matches.length, inSelection: false, error: 'no editor' };
  ensureFresh(view);
  const total = matches.length;
  const { state } = view;
  const selWasFinds = isCurrentMatch(view, state.selection);
  const scope = resolveScope(view);
  const plan = [];
  let skipped = 0;
  for (const m of matches) {
    const r = pmRange(view, m);
    if (!r) { skipped++; continue; }
    if (scope && (r.from < scope.from || r.to > scope.to)) continue;
    const $from = state.doc.resolve(r.from);
    const $to = state.doc.resolve(r.to);
    if (!$from.sameParent($to)) { skipped++; continue; }
    plan.push({
      from: r.from,
      to: r.to,
      text: literal ? replacement : expandTemplate(replacement, m.exec),
      marks: marksAt($from),
    });
  }
  if (plan.length === 0) return { replaced: 0, skipped, total, inSelection: !!scope };

  // Last to first, so every position planned against this document is still
  // right when its step is added.
  const tr = state.tr;
  for (let i = plan.length - 1; i >= 0; i--)
    replaceRange(tr, plan[i].from, plan[i].to, plan[i].text, plan[i].marks);
  if (selWasFinds) {
    // Find's selection of a match is now that match's replacement, which would
    // pass for the user's own next time. The kept range, selected outright, is
    // what a second Replace All should work on; failing that, a caret.
    tr.setSelection(scope
      ? TextSelection.create(tr.doc, tr.mapping.map(scope.from, -1), tr.mapping.map(scope.to, 1))
      : TextSelection.create(tr.doc, tr.mapping.map(state.selection.from, -1)));
  }
  view.dispatch(tr);
  afterChange(view, tr);
  return { replaced: plan.length, skipped, total, inSelection: !!scope };
}
