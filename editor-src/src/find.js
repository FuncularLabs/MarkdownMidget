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
let indexed = { source: null, flags: null, doc: null, regex: null, groupMap: null };

// The view findReset was given, so findNext/findPrev — which the host calls
// without one — can move the editor's own selection onto a match.
let boundView = null;

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

// The unicode flag, always, whatever the host sent (#5 F-6). It is what turns an
// escape .NET has and JavaScript does not — \A, \Z, \a — into an error we can
// report rather than a silently different search, and what makes \p{L} mean a
// Unicode category here as it does there. The host's FindEngine refuses the
// constructs the two engines read differently before calling in; this is the
// other half of the same decision, and the reason the host's escaper no longer
// writes "\ " for a space (unicode mode has no such escape).
// Normalised here, not at the call site, so the "same search again" comparison
// below sees one spelling of the flags.
function withUnicode(flags) {
  const f = flags || '';
  return f.includes('u') ? f : `${f}u`;
}

/// Returns { total, current } — or { total: 0, current: 0, error: 'Invalid
/// pattern' } for a source this engine will not compile, which the host shows as
/// its usual "Invalid pattern." Resets the search using a regex described as
/// (source, flags). If query is empty, clears matches. With the view given, the
/// same source and flags on an unchanged document keep the index and the current
/// match instead of starting over.
export function findReset(source, flags, view) {
  if (view) boundView = view;
  const f = withUnicode(flags);
  if (view && source && source === indexed.source && f === indexed.flags && view.state.doc === indexed.doc)
    return withTruncation({ total: matches.length, current: cursor + 1 });
  matches = [];
  cursor = -1;
  truncated = false;
  indexed = { source: null, flags: null, doc: null, regex: null, groupMap: null };
  if (!source) return { total: 0, current: 0 };
  let re;
  try { re = new RegExp(source, f); }
  catch { return { total: 0, current: 0, error: 'Invalid pattern' }; }
  indexed = { source, flags: f, doc: view ? view.state.doc : null, regex: re, groupMap: dotnetGroupMap(source) };
  reindex();
  return withTruncation({ total: matches.length, current: 0 });
}

// The most matches one scan will index. A pattern that matches at every position —
// `x*`, `\b` — would otherwise build a list as long as the document. Reaching it
// means the index describes PART of the document, and the two things that must not
// then happen quietly are a count the user reads as "all of them" and a Replace All
// that changes only what the index reached: so the flag travels out with every
// answer that carries a count, the host says so in the status line, and Replace All
// refuses (#5 NF-10). FindEngine.WysiwygMatchLimit is the host's copy of the number,
// for the message it shows.
const MATCH_LIMIT = 50000;
let matchLimit = MATCH_LIMIT;
let truncated = false;   // the last scan stopped at matchLimit

/// Lower the cap so a test can reach the truncation path without a document of
/// 50 000 matches. Returns the limit that was in force — pass that back (or
/// nothing) to restore it. Nothing in the app calls this.
export function findMatchLimit(limit) {
  const was = matchLimit;
  matchLimit = typeof limit === 'number' && limit > 0 ? limit : MATCH_LIMIT;
  return was;
}

// Every answer that carries a count says when the index behind it is short.
function withTruncation(result) {
  return truncated ? { ...result, truncated: true } : result;
}

// Rebuild the match list from the DOM with the indexed regex. Leaves cursor at -1.
function reindex() {
  matches = [];
  cursor = -1;
  truncated = false;
  const re = indexed.regex;
  if (!re) return;
  re.lastIndex = 0;
  const { text, nodes } = buildIndex();
  if (!text) return;

  // The cap bounds the loop whatever the pattern does, and says so when it bites.
  let m;
  let safety = 0;
  while ((m = re.exec(text)) !== null) {
    if (++safety > matchLimit) { truncated = true; break; }
    const start = m.index;
    const end = m.index + m[0].length;
    const empty = end === start;
    // A zero-width match — '^', '(?=cat)' — is a position, not a run: its range is
    // [pos, pos), its Replace inserts there and its highlight is a caret. That is
    // what the source view does with one, and skipping them here meant the same
    // query did nothing at all in this view (#5 F-5).
    const a = locate(nodes, start, true);
    const b = empty ? a : locate(nodes, end, false);
    if (a && b) matches.push({ startNode: a.node, startOffset: a.offset, endNode: b.node, endOffset: b.offset, start, end, exec: m });
    // Step past an empty match or exec would return it for ever — by a whole code
    // point, since the regex carries the unicode flag.
    if (empty) re.lastIndex = end + codeUnitsAt(text, end);
  }
}

// 2 when `text` holds a surrogate pair at `i`, otherwise 1 (including past the end).
function codeUnitsAt(text, i) {
  const c = text.charCodeAt(i);
  return c >= 0xd800 && c <= 0xdbff && i + 1 < text.length ? 2 : 1;
}

// Select the current match: in the editor, and in the browser (which is what
// scrolls it into view). Reads matches[cursor] rather than taking the match,
// because selecting can rebuild the index underneath it — see below.
function applySelection() {
  let match = matches[cursor];
  if (!match) return;
  // The editor's own selection, the way the source view selects in its document.
  // Replace asks "is the caret still on the match?" before it changes anything
  // (#5 F-9), and a browser highlight the editor never saw would answer no.
  if (boundView) {
    const r = pmRange(boundView, match);
    if (r) {
      const at = cursor;
      boundView.dispatch(boundView.state.tr
        .setSelection(TextSelection.create(boundView.state.doc, r.from, r.to))
        .setMeta('addToHistory', false));
      // A selection carries no steps of its own and adds no undo step, but a
      // plugin may append to the document on the back of it — the trailing one
      // does, for a document ending in anything but a paragraph or a heading
      // (a list, a table, a code block), unless setMarkdown has already settled
      // it (settle.js). Keep the index in step with what the editor now holds,
      // and keep the place, or the very next Replace finds the index stale and
      // degrades to Find Next.
      if (boundView.state.doc !== indexed.doc) {
        indexed.doc = boundView.state.doc;
        reindex();
        cursor = at < matches.length ? at : -1;
        match = matches[cursor];
        if (!match) return;
      }
    }
  }
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
  applySelection();
  return withTruncation({ total: matches.length, current: cursor + 1 });
}

export function findPrev(wrap) {
  if (matches.length === 0) return { total: 0, current: 0 };
  cursor = cursor - 1;
  if (cursor < 0) cursor = wrap ? matches.length - 1 : 0;
  applySelection();
  return withTruncation({ total: matches.length, current: cursor + 1 });
}

export function findClear() {
  matches = [];
  cursor = -1;
  truncated = false;
  indexed = { source: null, flags: null, doc: null, regex: null, groupMap: null };
  capturedScope = null;
  boundView = null;
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

// Only a selection over a line break limits Replace All; one on a single line is what fills Find what (FindEngine.IsReplaceScope).
const spansLines = (view, sel) => /[\r\n]/.test(view.state.doc.textBetween(sel.from, sel.to, '\n'));

// The Replace All scope. Selected text over a line break is the scope — unless it is Find's own
// selection of the current match, in which case the selection captured when the
// dialog opened is (if the document is still the one it was captured on). A
// caret, a selection on one line, a node selection, or nothing captured means the whole document.
function resolveScope(view) {
  const sel = view.state.selection;
  if (sel.node) return null;
  // "Is this Find's own?" first: its selection of a zero-width match is a caret,
  // and testing `sel.empty` first read that as the user having no selection and
  // widened Replace All to the whole document (#5 F-5). Mirrors the host's
  // FindEngine.ResolveScope.
  if (!isCurrentMatch(view, sel)) return spansLines(view, sel) ? { from: sel.from, to: sel.to } : null;
  if (capturedScope && capturedScope.doc === view.state.doc)
    return { from: capturedScope.from, to: capturedScope.to };
  return null;
}

/// Remember the current selection as the Replace All scope for this dialog
/// session (the host calls this when the Find dialog opens or is refocused).
/// A caret, a selection on one line or a selected node drops what was kept; the selection Find itself
/// made keeps it (bringing the dialog back with Ctrl+F changes nothing). Returns
/// the range kept, or null.
export function findCaptureScope(view) {
  if (!view) { capturedScope = null; return null; }
  const sel = view.state.selection;
  if (sel.node) { capturedScope = null; return null; }
  // Find's own selection first, empty or not — see resolveScope (#5 F-5).
  if (isCurrentMatch(view, sel)) {
    return capturedScope && capturedScope.doc === view.state.doc
      ? { from: capturedScope.from, to: capturedScope.to }
      : null;
  }
  if (!spansLines(view, sel)) { capturedScope = null; return null; }
  capturedScope = { from: sel.from, to: sel.to, doc: view.state.doc };
  return { from: sel.from, to: sel.to };
}

/// The selected text as shown (no markdown; '\n' between blocks and at a hard break), at most `limit` characters, for
/// FindEngine.SeedQuery. Nothing for a node or Find's own current match: re-seeding would turn a pattern into its match.
export function findSelectionText(view, limit) {
  const sel = view?.state.selection;
  if (!sel || sel.node || isCurrentMatch(view, sel)) return '';
  return view.state.doc.textBetween(sel.from, sel.to, '\n').slice(0, limit);
}

/// The map from a .NET group NUMBER to the JavaScript one, built from a pattern
/// source. The two engines number capture groups differently: .NET numbers the
/// unnamed groups in source order and then the named ones, JavaScript numbers
/// them all in source order — so `$1` against `(?<first>a)(b)` is 'b' in .NET and
/// 'a' in JavaScript. Everything the host accepts in Regex mode is numbered here
/// the .NET way, so one replacement template means one thing in both views.
///
/// Counts a capture for every '(' that is not escaped, not inside a character
/// class, and not the start of '(?:' '(?=' '(?!' '(?<=' '(?<!' '(?>' '(?#' or an
/// inline-option group; '(?<name>' and "(?'name'" are named captures.
/// Returns { count, toJs } where toJs[dotnetNumber] is the JavaScript index
/// (toJs[0] === 0: group 0 is the whole match in both).
export function dotnetGroupMap(source) {
  const named = [];        // JS indices of the named captures, in source order
  const unnamed = [];      // JS indices of the unnamed captures, in source order
  let inClass = false;
  let count = 0;
  for (let i = 0; i < source.length; i++) {
    const c = source[i];
    if (c === '\\') { i++; continue; }           // the escaped character, whatever it is
    if (inClass) { if (c === ']') inClass = false; continue; }
    if (c === '[') { inClass = true; continue; }
    if (c !== '(') continue;
    if (source[i + 1] !== '?') { unnamed.push(++count); continue; }
    const k = source[i + 2];
    const after = source[i + 3];
    if (k === '<' && after !== '=' && after !== '!') { named.push(++count); continue; }
    if (k === "'") { named.push(++count); continue; }
    // (?: (?= (?! (?<= (?<! (?> (?# (?i) (?(cond) — none of them capture.
  }
  return { count, toJs: [0, ...unnamed, ...named] };
}

/// Expand a replacement template against one match, in the one subset both
/// engines implement (editor-src/test/fixtures/replace-templates.json pins it):
/// `$$` a dollar sign, `$&` and `$0` the whole match, `$1`..`$99` a capture group
/// — two digits when they name a group that exists, otherwise one digit and the
/// rest literal — and `${name}` a named group (or a group number in braces).
/// Anything else after a `$` is literal, `$<name>`, `` $` ``, `$'`, `$+`, `$_` and
/// a trailing `$` included. A group that did not take part is empty.
///
/// `groupMap` comes from dotnetGroupMap(the pattern source): it is what makes
/// `$1` name the same group the source view's .NET engine would.
export function expandTemplate(template, m, groupMap) {
  // A caller with no pattern source to build a map from gets JavaScript's own
  // numbering. find.js never takes that path (findReset builds the map next to
  // the regex), and a test pins that it doesn't.
  const map = groupMap || { count: Math.max(0, m.length - 1), toJs: m.map((_, i) => i) };
  const byNumber = (n) => m[map.toJs[n]] ?? '';
  const byName = (name) => {
    if (!name) return null;
    if (m.groups && Object.prototype.hasOwnProperty.call(m.groups, name)) return m.groups[name] ?? '';
    if (/^\d+$/.test(name) && Number(name) <= map.count) return byNumber(Number(name));
    return null;
  };

  let out = '';
  for (let i = 0; i < template.length; i++) {
    const c = template[i];
    if (c !== '$' || i + 1 >= template.length) { out += c; continue; }
    const n = template[i + 1];
    if (n === '$') { out += '$'; i++; continue; }
    if (n === '&') { out += m[0]; i++; continue; }
    if (n === '{') {
      const end = template.indexOf('}', i + 2);
      if (end > i + 2) {
        const v = byName(template.slice(i + 2, end));
        if (v !== null) { out += v; i = end; continue; }
      }
      out += c;
      continue;
    }
    if (n >= '0' && n <= '9') {
      // Two digits when they name a group that exists, else one, else literal.
      const d2 = template[i + 2];
      if (d2 >= '0' && d2 <= '9' && Number(n + d2) <= map.count) {
        out += byNumber(Number(n + d2));
        i += 2;
        continue;
      }
      if (Number(n) <= map.count) {
        out += byNumber(Number(n));
        i += 1;
        continue;
      }
    }
    out += c;
  }
  return out;
}

/// Replace the current match, then move to the next one: { replaced, skipped,
/// total, current }. With no current match — or with the caret moved off it — this
/// is Find Next, as the button promises. A match that runs across blocks is
/// skipped (never joined); the following match is selected as usual. `wrap` is
/// Find's wrap-around: past the last match, the first.
export function findReplace(view, replacement, literal, wrap) {
  if (!view) return { replaced: 0, skipped: 0, total: matches.length, current: cursor + 1, error: 'no editor' };
  ensureFresh(view);
  // The selection has to still BE the current match. Between Find and Replace the
  // user may have clicked somewhere else entirely, and replacing the match they
  // can no longer see is not what the button says (#5 F-9). The source view has
  // always degraded to Find Next here.
  if (cursor < 0 || cursor >= matches.length || !isCurrentMatch(view, view.state.selection))
    return { replaced: 0, skipped: 0, ...findNext(!!wrap) };

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

  const text = literal ? replacement : expandTemplate(replacement, m.exec, indexed.groupMap);
  const tr = state.tr;
  replaceRange(tr, range.from, range.to, text, marksAt($from));
  // The selection IS the match (nothing else gets here), and mapped through the
  // change it would become a selection of the replacement text and pass for the
  // user's own next time; a caret after it instead, as typing over a selection
  // leaves.
  tr.setSelection(TextSelection.create(tr.doc, range.from + text.length));
  view.dispatch(tr);
  afterChange(view, tr);

  // Resume at the end of the replacement: the text before the match is as it
  // was, so its index offset still holds in the rebuilt index. Past it when the
  // match was empty, or the same empty match would be found there again for ever
  // — the rule the source view follows too (#5 F-5).
  const resume = m.start + text.length;
  const strictlyAfter = m.end === m.start;
  cursor = matches.findIndex((x) => (strictlyAfter ? x.start > resume : x.start >= resume));
  if (cursor < 0 && wrap && matches.length) cursor = 0;
  if (cursor >= 0) applySelection();
  // Replace changes the match Find is ON, which a short index still knows about, so
  // it runs; the flag goes with the count beside it (#5 NF-10).
  return withTruncation({ replaced: 1, skipped: 0, total: matches.length, current: cursor + 1 });
}

/// Replace every match — within the scope (see resolveScope) — in ONE
/// transaction: { replaced, skipped, moved, total, inSelection }. `total` is the
/// match count before replacing. The two reasons a match is left alone are counted
/// apart (#5 F-10): `skipped` ran from one block into the next, which a
/// replacement would have to join; `moved` is no longer where the search left it,
/// which is a different thing and is worded as one.
///
/// Refused outright — { replaced: 0, …, truncated: true } — when the index stopped
/// at the cap: "every match" is not what a part of the document can promise.
export function findReplaceAll(view, replacement, literal) {
  if (!view) return { replaced: 0, skipped: 0, moved: 0, total: matches.length, inSelection: false, error: 'no editor' };
  ensureFresh(view);
  const total = matches.length;
  // An index that stopped at the cap describes the start of the document and
  // nothing past it. Working from it changed that much and reported the number as
  // though it were all of them (#5 NF-10); the host turns this into a refusal the
  // user can act on.
  if (truncated) return { replaced: 0, skipped: 0, moved: 0, total, inSelection: false, truncated: true };
  const { state } = view;
  const selWasFinds = isCurrentMatch(view, state.selection);
  const scope = resolveScope(view);
  const plan = [];
  let skipped = 0;
  let moved = 0;
  for (const m of matches) {
    const r = pmRange(view, m);
    if (!r) { moved++; continue; }
    if (scope && (r.from < scope.from || r.to > scope.to)) continue;
    const $from = state.doc.resolve(r.from);
    const $to = state.doc.resolve(r.to);
    if (!$from.sameParent($to)) { skipped++; continue; }
    plan.push({
      from: r.from,
      to: r.to,
      text: literal ? replacement : expandTemplate(replacement, m.exec, indexed.groupMap),
      marks: marksAt($from),
    });
  }
  if (plan.length === 0) return { replaced: 0, skipped, moved, total, inSelection: !!scope };

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
  return { replaced: plan.length, skipped, moved, total, inSelection: !!scope };
}

