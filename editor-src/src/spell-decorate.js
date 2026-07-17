// SPIKE (Stage 1/2): draw squiggles at host-supplied ranges.
//
// Mirrors code-spellcheck.js: decorations recomputed from plugin state and mapped
// through transactions so they survive edits.
//
// The subtle part is STALENESS. Checking is an async round-trip to the host, so
// ranges are computed against doc version N but can arrive after the user has typed
// (version N+1) — applying them raw lands squiggles mid-word on unrelated text.
// So from the moment a check is requested we accumulate a Mapping of every
// intervening change, and map late results through it before they're drawn.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { Mapping } from '@milkdown/kit/prose/transform';

export const SPELL_DECO_KEY = new PluginKey('mdmSpellDeco');

// Module scope so they survive EditorState rebuilds (setMarkdown re-creates state).
let currentRanges = [];
let pending = null; // Mapping of doc changes since the in-flight check was requested

function build(doc, ranges) {
  if (!ranges || !ranges.length) return DecorationSet.empty;
  const decos = [];
  for (const r of ranges) {
    if (r.from == null || r.to == null || r.to <= r.from) continue;
    if (r.to > doc.content.size) continue;
    decos.push(Decoration.inline(r.from, r.to, { class: 'mdm-misspelled' }));
  }
  return DecorationSet.create(doc, decos);
}

/**
 * Call immediately before handing text to the host to check. Starts recording the
 * edits that happen while the host is thinking.
 */
export function beginSpellCheck() {
  pending = new Mapping();
}

/**
 * Translate ranges computed against the snapshot at beginSpellCheck() into
 * positions valid for the current doc. Ranges whose text was deleted meanwhile are
 * dropped rather than collapsed onto whatever moved into their place.
 */
function rebase(ranges) {
  if (!pending) return ranges;
  const out = [];
  for (const r of ranges) {
    const from = pending.mapResult(r.from, 1);
    const to = pending.mapResult(r.to, -1);
    if (from.deleted || to.deleted) continue;   // the word itself was edited away
    if (to.pos > from.pos) out.push({ from: from.pos, to: to.pos });
  }
  return out;
}

export const spellDecorate = $prose(() => new Plugin({
  key: SPELL_DECO_KEY,
  state: {
    init(_, state) { return { ranges: currentRanges, deco: build(state.doc, currentRanges) }; },
    apply(tr, value, _old, newState) {
      // Record edits made while a check is in flight.
      if (pending && tr.docChanged) pending.appendMapping(tr.mapping);

      const meta = tr.getMeta(SPELL_DECO_KEY);
      if (meta && meta.ranges) {
        const ranges = rebase(meta.ranges);
        pending = null;
        currentRanges = ranges;
        return { ranges, deco: build(newState.doc, ranges) };
      }
      // Plain edits: map existing squiggles through the change so they stay glued
      // to their words until a fresh check lands.
      return { ranges: value.ranges, deco: value.deco.map(tr.mapping, tr.doc) };
    },
  },
  props: {
    decorations(state) { return SPELL_DECO_KEY.getState(state).deco; },
  },
}));

export function setSpellRanges(view, ranges) {
  if (view) view.dispatch(view.state.tr.setMeta(SPELL_DECO_KEY, { ranges: ranges || [] }));
}
