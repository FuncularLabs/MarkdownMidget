// The serialiser's conventions (release plan 0.12, R2).
//
// A document that passes through the formatted view is saved in these forms,
// whatever it was written in. Each one is pinned by a case in
// test/roundtrip.test.mjs (ConventionsArePinned); change a line here and that
// case goes red, which is the point of it.
//
// Three things live here: the remark-stringify options and handlers the editor
// serialises with (`conventions`, a Milkdown config — the hard-break form, the
// underscore rule of R3 and the emphasis/strong encoding are handlers), and the
// schema fix that keeps tight lists tight (`tightBulletList`, `tightListItem`).
// All are wired in by editor-factory.js.

import { remarkStringifyOptionsCtx } from '@milkdown/kit/core';
import { bulletListSchema } from '@milkdown/kit/preset/commonmark';
import { extendListItemSchemaForTask } from '@milkdown/kit/preset/gfm';
import { defaultHandlers } from 'mdast-util-to-markdown';
import { recordingRoot } from './line-map.js';

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

// ---- intraword underscores survive (R3) ------------------------------------
//
// mdast-util-to-markdown escapes every `_` in phrasing (its `unsafe` list, which
// options can add to but not trim), so `snake_case_word` was saved as
// `snake\_case\_word`. Under CommonMark's flanking rules a `_` run with a word
// character on BOTH sides can neither open nor close emphasis — it is left- and
// right-flanking at once, and `_` needs punctuation beside it to be a delimiter
// in that state — so that escape buys nothing. The handler below wraps
// Milkdown's own text handler and un-escapes exactly those runs; every other
// `_` keeps its backslash, which is what stops a literal `_x_` from coming back
// as emphasis. "Word character" is anything that is neither whitespace nor
// punctuation, punctuation being \p{P} and \p{S} — the classes micromark, the
// parser, tests. micromark tests them on UTF-16 code UNITS (its preprocessor
// reads charCodeAt) where this reads code points, so the two sides disagree on
// exactly one thing: an astral punctuation or symbol character — an emoji — is
// a lone surrogate, hence a word character, to the parser and a symbol here.
// That disagreement only ever runs the safe way: this side keeps the escape on
// a `_` the parser would have read as literal anyway, and an extra escape is
// always literal. Image alt text never reaches this handler — the image
// handler escapes it through state.safe itself — so its `_` stays `\_`.
const NOT_WORD = /[\s\p{P}\p{S}]/u;
const isWordChar = (ch) => ch !== '' && !NOT_WORD.test(ch);
// The neighbouring character as a code point, so an emoji (a surrogate pair)
// is read as itself and not as its second half.
const lastChar = (s) => [...s.slice(-2)].pop() ?? '';
const firstChar = (s) => [...s.slice(0, 2)][0] ?? '';

function keepIntrawordUnderscores(escaped, info) {
  return escaped.replace(/(?:\\_)+/g, (run, offset) => {
    // At the node's edges the neighbour is what the serialiser wrote before
    // this node / will write after it: `info.before` and `info.after`.
    const end = offset + run.length;
    const prev = offset > 0 ? lastChar(escaped.slice(0, offset)) : lastChar(info.before || '');
    const next = end < escaped.length ? firstChar(escaped.slice(end)) : firstChar(info.after || '');
    return isWordChar(prev) && isWordChar(next) ? run.replaceAll('\\', '') : run;
  });
}

/** Milkdown's `text` handler (which escapes through state.safe) with the underscore rule on top. */
function intrawordUnderscores(text) {
  return (node, parent, state, info) => keepIntrawordUnderscores(text(node, parent, state, info), info);
}

// ---- emphasis beside punctuation survives (#2 F-1) --------------------------
//
// Milkdown replaces mdast-util-to-markdown's emphasis and strong handlers with
// its own (@milkdown/core's remarkHandlers) so that a mark is written with the
// marker it was READ with — remarkMarker records it on the mdast node as
// `node.marker`. Theirs write marker, content, marker and nothing else. The
// defaults also look at the character on each side of the run and, where the
// pair would keep the run from opening or closing, write the outer letter as a
// character reference (encodeInfo): `a` + emphasis(`_b`) is `&#x61;*\_b*`,
// which reads back as emphasis, where Milkdown's `a*\_b*` does not — `\` is
// punctuation and `a` is not, so that `*` run can open nothing. The wrapper
// below is the default handler with the node's marker in force (and, where
// the default's encoding would land on half a character, the plain form
// instead: the section after this one). The default
// reads the marker from state.options, and state.options is the copy
// remark-stringify makes for each stringify call, so setting it for one node
// reaches no other document; it is restored after the call because a run can
// nest one of the other marker. A marker that is neither `*` nor `_` (there
// is none: remarkMarker reads the construct's first character) falls back to
// the option, where the default would throw. The peek is what the sibling
// BEFORE a run is told the run starts with: without one containerPhrasing
// runs the whole handler as the peek, and the default handler leaves its
// encoding decision (state.attentionEncodeSurroundingInfo) behind for the
// wrong node.
const ATTENTION_MARKERS = new Set(['*', '_']);
const markerOf = (node, option, state) =>
  ATTENTION_MARKERS.has(node.marker) ? node.marker : (state.options[option] || '*');

/** mdast-util-to-markdown's `emphasis` / `strong` handler (the `option` names both) writing the node's own marker. */
function encodedAttention(option) {
  const handler = (node, parent, state, info) => {
    const marker = markerOf(node, option, state);
    const fence = option === 'strong' ? marker + marker : marker;
    const saved = state.options[option];
    state.options[option] = marker;
    try {
      const out = defaultHandlers[option](node, parent, state, info);
      const asked = state.attentionEncodeSurroundingInfo || {};
      if (encodesHalfANeighbour(asked, info)) return plainAttention(node, option, state, info, fence);
      const content = out.slice(fence.length, -fence.length);
      if (!referencesHalfACharacter(content)) return out;
      // The reference at the run's own head or tail may be the run's own TEXT
      // rather than the default's encoding of it; only the plain content, which
      // carries no encoding, says which (#2 O-5). Writing the run plain is the
      // expensive half of the answer, so it is computed only here, and only for
      // the two shapes that can be a half.
      const plain = plainAttention(node, option, state, info, fence);
      if (encodesHalfAnEnd(content, plain.slice(fence.length, -fence.length))) return plain;
      state.attentionEncodeSurroundingInfo = asked;  // plainAttention cleared it; the default's decision stands
      return out;
    } finally {
      state.options[option] = saved;
    }
  };
  handler.peek = (node, parent, state) => markerOf(node, option, state);
  return handler;
}

// ---- an astral neighbour is left alone (#2 F-A) ----------------------------
//
// The default handlers, and containerPhrasing after them, read the characters
// around a run as UTF-16 code UNITS — `charCodeAt`, `slice(-1)`, `slice(0, 1)`
// — so when the letter they decide to encode is an astral character (an emoji,
// a mathematical letter) the reference they write is to ONE HALF of its
// surrogate pair: `😀*_b*` was saved as `\uD83D&#xDE00;*\_b*`, and micromark
// decodes a reference to a lone surrogate as U+FFFD. The text was destroyed
// (the half left behind is a lone surrogate, which a UTF-8 writer can only
// replace as well) and the second save differed from the first. Before F-1 the
// same input lost the MARK and kept the text, and that is the outcome kept
// here: where the default's encoding landed, or would land, on half a
// character, the run is written plain — fence, content, fence, Milkdown's own
// form — and nothing is asked of the neighbours. The text survives and a save
// is stable from the second one on; the mark may not survive the next open (to
// micromark a surrogate half is a letter, and a `*` after a letter cannot open
// on punctuation), which is the accepted cost of that rare case. Encoding the
// whole character instead (`&#x1F600;*\_b*` keeps mark and text) would mean
// rewriting the neighbouring text node, which this handler does not reach.
//
// That fallback costs a mark, so it fires only where a character really was cut
// in half. A reference in the output is not proof of one: `&#xD83D;` is eight
// characters a user can type, and the run's own text ends with them just as the
// default's encoding does (#2 O-5). Every branch below is anchored to a real
// surrogate half — in `info.before`/`info.after` for a neighbour, in the plain
// (unencoded) content for the run's own ends.
const ENDS_IN_LOW_SURROGATE = /[\uDC00-\uDFFF]$/;      // `info.before`, or the plain content: a character by its second half
const STARTS_WITH_HIGH_SURROGATE = /^[\uD800-\uDBFF]/; // `info.after`, or the plain content: a character by its first half
const HALF_REFERENCE = '&#x[dD][89a-fA-F][0-9a-fA-F]{2};';  // what encodeCharacterReference makes of either half: D800–DFFF
const HEAD_HALF_REFERENCE = new RegExp('^' + HALF_REFERENCE);
const TAIL_HALF_REFERENCE = new RegExp(HALF_REFERENCE + '$');

/**
 * Whether a neighbour the handler asked containerPhrasing to encode
 * (state.attentionEncodeSurroundingInfo) is half a character.
 */
function encodesHalfANeighbour(asked, info) {
  return (asked.before && ENDS_IN_LOW_SURROGATE.test(info.before))
    || (asked.after && STARTS_WITH_HIGH_SURROGATE.test(info.after));
}

/** Whether the run's own first or last characters LOOK like a reference to half a character. Cheap; `encodesHalfAnEnd` decides. */
function referencesHalfACharacter(content) {
  return HEAD_HALF_REFERENCE.test(content) || TAIL_HALF_REFERENCE.test(content);
}

/**
 * Whether that reference is the default's encoding of the run's own first or
 * last character, and not text the user wrote. The pattern alone cannot tell
 * them apart: a run whose text ends with the eight characters `&#xD83D;` is
 * written `\&#xD83D;` and matches, and writing THAT run plain loses a mark the
 * default would have kept (#2 O-5). The plain content — the content before any
 * encoding — settles it: only the default's work leaves a real surrogate half
 * at that end. A `(?<!\\)` lookbehind is not the discriminator; a run ending in
 * a literal backslash before an emoji is written `\\&#xDE00;`, a real half it
 * would miss.
 */
function encodesHalfAnEnd(content, plainContent) {
  return (HEAD_HALF_REFERENCE.test(content) && STARTS_WITH_HIGH_SURROGATE.test(plainContent))
    || (TAIL_HALF_REFERENCE.test(content) && ENDS_IN_LOW_SURROGATE.test(plainContent));
}

/** Milkdown's own form of the run — fence, content, fence — with no encoding asked of the neighbours. */
function plainAttention(node, option, state, info, fence) {
  state.attentionEncodeSurroundingInfo = undefined;
  const exit = state.enter(option);
  const tracker = state.createTracker(info);
  const open = tracker.move(fence);
  const content = tracker.move(state.containerPhrasing(node, { before: open, after: fence[0], ...tracker.current() }));
  exit();
  return open + content + tracker.move(fence);
}

/** Milkdown config: install the options and handlers above. */
export function conventions(ctx) {
  ctx.update(remarkStringifyOptionsCtx, (prev) => ({
    ...prev,
    ...SERIALIZER_OPTIONS,
    handlers: {
      ...prev.handlers,
      root: recordingRoot(prev.handlers.root || defaultHandlers.root),   // block lines for #10 (line-map.js)
      break: hardBreak,
      text: intrawordUnderscores(prev.handlers.text || defaultHandlers.text),
      emphasis: encodedAttention('emphasis'),
      strong: encodedAttention('strong'),
    },
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
