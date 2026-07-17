// Draws squiggles at host-supplied misspelling ranges (ProseMirror positions).
//
// Two hard-won properties from the de-risk spike:
//
// STALENESS: checking is an async round-trip to the host, so ranges are computed
// against doc version N but can arrive after the user has typed (version N+1) —
// applied raw they land mid-word on unrelated text. From beginSpellCheck() we
// accumulate a Mapping of every intervening change and rebase late results
// through it; ranges whose text was edited away are dropped, not collapsed.
//
// VIEWPORT LIMITING: decorating an entire 50k-char document (~800 squiggles)
// costs ~+14ms per keystroke; a viewport's worth costs ~+1ms. So the full range
// list lives in plugin state, but only ranges near the visible viewport become
// live decorations, rebuilt on scroll (throttled) and on new results.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { Mapping } from '@milkdown/kit/prose/transform';

export const SPELL_DECO_KEY = new PluginKey('mdmSpellDeco');

const VIEWPORT_MARGIN = 2000; // positions beyond the visible edge kept decorated

// Module scope so they survive EditorState rebuilds (setMarkdown re-creates state).
let pending = null;         // Mapping of doc changes since the in-flight check began
let lastViewport = [0, 0];  // refreshed before every rebuild

export function beginSpellCheck() {
  pending = new Mapping();
}

function rebase(ranges) {
  if (!pending) return ranges || [];
  const out = [];
  for (const r of ranges || []) {
    const from = pending.mapResult(r.from, 1);
    const to = pending.mapResult(r.to, -1);
    if (from.deleted || to.deleted) continue;   // the word itself was edited away
    if (to.pos > from.pos) out.push({ from: from.pos, to: to.pos });
  }
  return out;
}

function mapAll(ranges, mapping) {
  const out = [];
  for (const r of ranges) {
    const from = mapping.mapResult(r.from, 1);
    const to = mapping.mapResult(r.to, -1);
    if (from.deleted || to.deleted) continue;
    if (to.pos > from.pos) out.push({ from: from.pos, to: to.pos });
  }
  return out;
}

function buildVisible(doc, ranges, viewport) {
  if (!ranges.length) return DecorationSet.empty;
  const [vFrom, vTo] = viewport;
  const decos = [];
  for (const r of ranges) {
    if (r.to <= vFrom || r.from >= vTo) continue;
    if (r.from == null || r.to == null || r.to <= r.from) continue;
    if (r.to > doc.content.size) continue;
    decos.push(Decoration.inline(r.from, r.to, { class: 'mdm-misspelled' }));
  }
  return DecorationSet.create(doc, decos);
}

function visiblePmRange(view) {
  const size = view.state.doc.content.size;
  // Degenerate viewport (minimized window, measurement failure): decorate around
  // the selection rather than the whole document — full-doc decoration is the
  // exact perf cliff this limiting exists to avoid.
  const nearSelection = () => {
    const head = view.state.selection.head;
    return [Math.max(0, head - 2 * VIEWPORT_MARGIN), Math.min(size, head + 2 * VIEWPORT_MARGIN)];
  };
  try {
    const rect = view.dom.getBoundingClientRect();
    const win = view.dom.ownerDocument.defaultView || window;
    const top = Math.max(rect.top, 0);
    const bottom = Math.min(rect.bottom, win.innerHeight);
    if (bottom <= top) return nearSelection();
    const a = view.posAtCoords({ left: rect.left + 8, top: top + 2 });
    const b = view.posAtCoords({ left: rect.right - 8, top: bottom - 2 });
    if (!a && !b) return nearSelection();
    return [
      Math.max(0, (a ? a.pos : 0) - VIEWPORT_MARGIN),
      Math.min(size, (b ? b.pos : size) + VIEWPORT_MARGIN),
    ];
  } catch (_) {
    return nearSelection();
  }
}

export const spellDecorate = $prose(() => new Plugin({
  key: SPELL_DECO_KEY,
  state: {
    init() { return { all: [], deco: DecorationSet.empty }; },
    apply(tr, value, _old, newState) {
      if (pending && tr.docChanged) pending.appendMapping(tr.mapping);

      const meta = tr.getMeta(SPELL_DECO_KEY);
      if (meta && meta.ranges) {
        const all = rebase(meta.ranges);
        pending = null;
        return { all, deco: buildVisible(newState.doc, all, lastViewport) };
      }
      if (meta && meta.viewport) {
        return { all: value.all, deco: buildVisible(newState.doc, value.all, lastViewport) };
      }
      if (tr.docChanged) {
        // Keep both the full list and the live decorations glued to their words
        // until the next check lands.
        return { all: mapAll(value.all, tr.mapping), deco: value.deco.map(tr.mapping, tr.doc) };
      }
      return value;
    },
  },
  props: {
    decorations(state) { return SPELL_DECO_KEY.getState(state).deco; },
  },
  view(editorView) {
    // Rebuild the visible window on scroll/resize, throttled: scrolling pays at
    // most one rebuild per interval, and an idle document costs nothing.
    let timer = null;
    const onScroll = () => {
      if (timer) return;
      timer = setTimeout(() => {
        timer = null;
        lastViewport = visiblePmRange(editorView);
        editorView.dispatch(editorView.state.tr.setMeta(SPELL_DECO_KEY, { viewport: true }));
      }, 150);
    };
    const win = editorView.dom.ownerDocument.defaultView || window;
    win.addEventListener('scroll', onScroll, true);   // capture: any scrolling ancestor
    win.addEventListener('resize', onScroll);
    lastViewport = visiblePmRange(editorView);
    return {
      // Belt-and-braces: if the caret moves outside the decorated window (paging
      // with the keyboard, jump-to-end, a host whose scroll events misbehave),
      // schedule the same throttled rebuild the scroll handler would.
      update(view) {
        const head = view.state.selection.head;
        if (head < lastViewport[0] || head > lastViewport[1]) onScroll();
      },
      destroy() {
        if (timer) clearTimeout(timer);
        win.removeEventListener('scroll', onScroll, true);
        win.removeEventListener('resize', onScroll);
      },
    };
  },
}));

/** Host entry point: apply fresh check results. */
export function setSpellRanges(view, ranges) {
  lastViewport = visiblePmRange(view);
  view.dispatch(view.state.tr.setMeta(SPELL_DECO_KEY, { ranges: ranges || [] }));
}

/** The misspelling range covering a document position, or null. */
export function misspellingAt(state, pos) {
  const st = SPELL_DECO_KEY.getState(state);
  if (!st) return null;
  for (const r of st.all) {
    if (pos >= r.from && pos <= r.to) return r;
  }
  return null;
}
