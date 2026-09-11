// The serialiser's conventions (release plan 0.12, R2).
//
// A document that passes through the formatted view is saved in these forms,
// whatever it was written in. Each one is pinned by a case in
// test/roundtrip.test.mjs (ConventionsArePinned); change a line here and that
// case goes red, which is the point of it.
//
// Two things live here: the remark-stringify options and handlers the editor
// serialises with (`conventions`, a Milkdown config), and the schema fix that
// keeps tight lists tight (`tightBulletList`, `tightListItem`). Both are wired
// in by editor-factory.js.

import { remarkStringifyOptionsCtx } from '@milkdown/kit/core';
import { bulletListSchema } from '@milkdown/kit/preset/commonmark';
import { extendListItemSchemaForTask } from '@milkdown/kit/preset/gfm';
import { defaultHandlers } from 'mdast-util-to-markdown';

/**
 * The hard-break form. CommonMark allows two spellings: a backslash before the
 * newline, or two trailing spaces. The backslash is remark's default and can be
 * seen; two spaces are what most hand-written markdown uses and what many
 * editors strip on save. A product decision is pending; until it lands this is
 * the ONE place the choice is made. The alternative is '  \n'.
 */
export const HARD_BREAK = '\\\n';

/** remark-stringify (mdast-util-to-markdown) options. */
export const SERIALIZER_OPTIONS = {
  bullet: '-',                // `-` bullets
  bulletOther: '*',           // …and `*` for a list DIRECTLY after another one: with the
                              // same marker, CommonMark would read the two as one list
  listItemIndent: 'one',      // `- one`, not `-   one`
  bulletOrdered: '.',         // `1.`
  incrementListMarker: true,  // `1.` `2.` `3.`
  setext: false,              // ATX headings…
  closeAtx: false,            // …without closing hashes
  fences: true,               // fenced code, never indented
  emphasis: '*',              // the marker for emphasis MADE IN THE EDITOR; emphasis
  strong: '*',                // read from a file keeps the marker it was written with
};

/**
 * mdast-util-to-markdown's `break` handler with the form swapped for HARD_BREAK.
 * Delegating keeps the default's other rule: a space where a newline cannot go
 * (an ATX heading, a table cell), which would otherwise end the construct.
 */
function hardBreak(node, parent, state, info) {
  const out = defaultHandlers.break(node, parent, state, info);
  return out === '\\\n' ? HARD_BREAK : out;
}

/** Milkdown config: install the options and handlers above. */
export function conventions(ctx) {
  ctx.update(remarkStringifyOptionsCtx, (prev) => ({
    ...prev,
    ...SERIALIZER_OPTIONS,
    handlers: { ...prev.handlers, break: hardBreak },
  }));
}

// ---- tight lists stay tight ------------------------------------------------
//
// Milkdown's parser stores a list's `spread` as the STRING "true" / "false"
// (bullet_list, ordered_list and list_item alike), and the bullet_list and
// list_item serialisers pass that string straight into mdast. There
// mdast-util-to-markdown honours `spread` only when it is a boolean — its join
// rule tests `typeof parent.spread === 'boolean'` — and otherwise falls back to
// a blank line between items, so every bullet list read from a file was saved
// loose. ordered_list coerces (`=== "true"`) and was never affected; the GFM
// task item coerces too but hands plain items back to the string path. The two
// extensions below give mdast a real boolean. Registered after the presets so
// theirs is the definition the schema keeps (last one wins).
const isSpread = (v) => v === true || v === 'true';

export const tightBulletList = bulletListSchema.extendSchema((prev) => (ctx) => {
  const base = prev(ctx);
  return {
    ...base,
    toMarkdown: {
      match: base.toMarkdown.match,
      runner: (state, node) => {
        state.openNode('list', undefined, { ordered: false, spread: isSpread(node.attrs.spread) })
          .next(node.content)
          .closeNode();
      },
    },
  };
});

export const tightListItem = extendListItemSchemaForTask.extendSchema((prev) => (ctx) => {
  const base = prev(ctx);
  return {
    ...base,
    toMarkdown: {
      match: base.toMarkdown.match,
      runner: (state, node) => {
        if (node.attrs.checked != null) return base.toMarkdown.runner(state, node); // a task item: already a boolean
        state.openNode('listItem', undefined, { spread: isSpread(node.attrs.spread) });
        state.next(node.content);
        state.closeNode();
      },
    },
  };
});
