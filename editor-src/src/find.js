// Find for the WYSIWYG view.
//
// The host builds a JS RegExp source string (and flags) and calls findReset(...)
// which scans the .mdm-prosemirror text nodes, builds an index, and returns the
// total match count. findNext()/findPrev() cycle through matches, scrolling and
// highlighting via the browser selection.

let view = null;
let matches = [];     // [{startNode, startOffset, endNode, endOffset}]
let cursor = -1;      // index into matches

export function setEditorView(v) { view = v; }

function getRoot() {
  return document.querySelector('.mdm-prosemirror');
}

function buildIndex() {
  const root = getRoot();
  if (!root) return { text: '', nodes: [] };
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode(n) {
      // Skip text inside our injected widgets / marks / decorations that aren't
      // part of the editable content (best-effort).
      if (n.parentElement && n.parentElement.closest('.mdm-mark, .mdm-mermaid, .mdm-print-source-pre'))
        return NodeFilter.FILTER_REJECT;
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

function locate(nodes, offset) {
  // Binary search would be nice, but linear is fine for a one-shot find.
  for (let i = 0; i < nodes.length; i++) {
    if (offset <= nodes[i].end) return { node: nodes[i].node, offset: offset - nodes[i].start };
  }
  const last = nodes[nodes.length - 1];
  return last ? { node: last.node, offset: last.node.nodeValue.length } : null;
}

/// Returns { total, current }. Resets the search using a regex described as
/// (source, flags). If query is empty, clears matches.
export function findReset(source, flags) {
  matches = [];
  cursor = -1;
  if (!source) return { total: 0, current: 0 };
  let re;
  try { re = new RegExp(source, flags); }
  catch { return { total: 0, current: 0, error: 'Invalid pattern' }; }

  const { text, nodes } = buildIndex();
  if (!text) return { total: 0, current: 0 };

  // Guard against zero-width matches infinite looping.
  let m;
  let safety = 0;
  while ((m = re.exec(text)) !== null) {
    if (safety++ > 50000) break;
    const start = m.index;
    const end = m.index + m[0].length;
    if (m[0].length === 0) { re.lastIndex = end + 1; continue; }
    const a = locate(nodes, start);
    const b = locate(nodes, end);
    if (a && b) matches.push({ startNode: a.node, startOffset: a.offset, endNode: b.node, endOffset: b.offset });
  }
  return { total: matches.length, current: 0 };
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
}
