// The round-trip harness (release plan 0.12, R1): markdown in → the real editor
// → markdown out, under node --test.
//
// The editor is the one the app ships — editor-factory.js, mounted in jsdom by
// jsdom-editor.mjs — because the serialiser's behaviour is not remark's alone:
// what a list looks like on the way out is decided by attributes the ProseMirror
// schema put on it on the way in. A bare mdast round trip would pass where the
// app fails.
//
// Three kinds of assertion live here. `Idempotent` is the invariant: whatever
// the editor makes of a document, it makes the same of its own output, so a
// save is stable after the first one. `MeasuredRewritesAreReproduced` is the
// record: one assertion per row of the plan's measured table, stating what the
// editor does to that construct. When a convention is pinned (R2) the row's
// expectation is changed on purpose, with the reason beside it — never by
// regenerating. `ConventionsArePinned`, `IntrawordUnderscoreSurvives`,
// `EmphasisBesidePunctuationSurvives` and `AstralNeighboursSurvive` hold the
// forms src/conventions.js chooses, one case each, so a drift in any of them is
// a red case with a name.
import test, { before, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { callCommand } from '@milkdown/kit/utils';
import { toggleEmphasisCommand, toggleStrongCommand } from '@milkdown/kit/preset/commonmark';
import { mountEditor } from './jsdom-editor.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const read = (...p) => readFileSync(join(here, ...p), 'utf8');

// The corpus R1 names: the audit's fixture (the measured table was taken from
// it) plus the two documents the project itself is written in.
const CORPUS = {
  'roundtrip-audit.md': read('fixtures', 'roundtrip-audit.md'),
  'README.md': read('..', '..', 'README.md'),
  'HELP.md': read('..', '..', 'HELP.md'),
};

let ed;
before(async () => { ed = await mountEditor(); });

/** Whether the current document holds an emphasis mark anywhere. */
const hasEmphasis = () => {
  let found = false;
  ed.view().state.doc.descendants((n) => { if (n.marks.some((m) => m.type.name === 'emphasis')) found = true; });
  return found;
};

/**
 * Round-trip `md` and prove its output holds: it re-parses to the SAME
 * ProseMirror document, and a second pass changes nothing. Returns the output.
 */
const survives = (md) => {
  const s1 = ed.roundTrip(md);
  const d1 = ed.view().state.doc.toJSON();
  const s2 = ed.roundTrip(s1);
  const d2 = ed.view().state.doc.toJSON();
  assert.deepEqual(d2, d1, `${JSON.stringify(md)}: the editor's output ${JSON.stringify(s1)} re-parses to a different document`);
  assert.equal(s2, s1, `${JSON.stringify(md)}: a second pass changed the output`);
  return s1;
};

describe('Idempotent', () => {
  for (const [name, text] of Object.entries(CORPUS)) {
    test(name, () => {
      const s1 = ed.roundTrip(text);
      const s2 = ed.roundTrip(s1);
      assert.equal(s2, s1, `${name}: a second pass through the editor changed the output again`);
    });
  }
});

describe('MeasuredRewritesAreReproduced', () => {
  // Each case is a row of "The measured round-trip" table in
  // docs/plans/release-1.0.md, in the table's order. The expected string is the
  // editor's output for that construct, as it is today.
  let out;
  before(() => { out = ed.roundTrip(CORPUS['roundtrip-audit.md']); });

  test('setext heading becomes ATX', () => {
    // Pinned by R2 (setext: false).
    assert.match(out, /^# Title From Setext$/m);
    assert.doesNotMatch(out, /^=+$/m);
  });

  test('closing hashes on an ATX heading are stripped', () => {
    // Pinned by R2 (closeAtx: false).
    assert.match(out, /^## Heading with trailing hashes$/m);
  });

  test('+ bullets become -, and the adjacent * list becomes *', () => {
    // Was `*` then `-` (the serialiser's defaults). R2 pins bullet: '-' and
    // bulletOther: '*'; the second list still has to differ from the first or
    // the two would re-parse as one. The blank line between the two + items
    // went with the tight-list row below.
    assert.match(out, /^- plus bullet one\n- plus bullet two$/m);
    assert.match(out, /^\* star bullet$/m);
  });

  test('1) ordered becomes 1.', () => {
    // Pinned by R2 (bulletOrdered: '.', incrementListMarker: true).
    assert.match(out, /^1\. paren ordered\n2\. paren ordered two$/m);
  });

  test('a reference link is inlined and its definition deleted', () => {
    assert.match(out, /\[reference link\]\(https:\/\/example\.com\/ref "Ref Title"\)/);
    assert.doesNotMatch(out, /^\[ref\]:/m);
  });

  test('an indented code block becomes fenced', () => {
    // Pinned by R2 (fences: true).
    assert.match(out, /^```\nindented code block\nsecond line\n```$/m);
  });

  test('two trailing spaces become a backslash hard break', () => {
    // Pinned by R2 (HARD_BREAK) pending the product decision on the form.
    assert.match(out, /^Line with two trailing spaces\\\ncontinues here\.$/m);
  });

  test('intraword underscores survive', () => {
    // Was `snake\_case\_word` until R3: an underscore between two word
    // characters cannot open or close emphasis, so it is not escaped.
    assert.match(out, /a snake_case_word and/);
  });

  test('the table is reformatted to the widest cell', () => {
    assert.match(out, /^\| Left \| Right \|\n\| :--- \| ----: \|\n\| a {4}\| {5}b \|$/m);
  });

  test('a tight bullet list stays tight', () => {
    // Was loose (a blank line between the items) until R2's tightBulletList
    // fix, and `*` until bullet: '-'. The task list is the fixture's `-` list;
    // the table before it means it is not directly after another list.
    assert.match(out, /^- \[ \] task open\n- \[x\] task done$/m);
  });

  test('CRLF: LF outside code and HTML blocks, CRLF kept inside them', () => {
    // The fixture is LF-only, so this row has its own input: a paragraph, a
    // fenced block and an HTML block, all CRLF. The endings between blocks and
    // inside the paragraph are the parser's to fold; the line breaks INSIDE a
    // code or HTML block are the block's text and come back as written. The
    // ending after a block's last line is the serialiser's LF: it closes the
    // block, it is not part of it. (R4, on the host, makes the whole file one
    // ending again.)
    const crlf = 'para one\r\nline two\r\n\r\n```\r\ncode a\r\ncode b\r\n```\r\n\r\n<div>\r\nhtml\r\n</div>\r\n';
    assert.equal(ed.roundTrip(crlf), 'para one\nline two\n\n```\ncode a\r\ncode b\n```\n\n<div>\r\nhtml\r\n</div>\n');
  });
});

describe('ConventionsArePinned', () => {
  // R2: what the serialiser writes, one case per convention. The name in
  // parentheses is the option in src/conventions.js that decides it; flip that
  // one line and the case goes red.

  test('bullets are - (bullet)', () => {
    assert.equal(ed.roundTrip('* one\n* two'), '- one\n- two\n');
    assert.equal(ed.roundTrip('+ one\n+ two'), '- one\n- two\n');
  });

  test('one space after the bullet (listItemIndent)', () => {
    assert.equal(ed.roundTrip('-   one\n-   two'), '- one\n- two\n');
  });

  test('a list directly after another gets * (bulletOther)', () => {
    assert.equal(ed.roundTrip('- a\n\n+ b'), '- a\n\n* b\n');
  });

  test('adjacent lists alternate because one marker would merge them', () => {
    // Not a convention but the fact behind the one above: CommonMark reads two
    // `-` lists separated by a blank line as ONE loose list, so the serialiser
    // has to change marker, and bulletOther only chooses which one it uses.
    ed.roundTrip('- a\n\n- b');
    assert.equal(ed.view().state.doc.childCount, 1, 'same marker: one loose list');
    ed.roundTrip('- a\n\n* b');
    assert.equal(ed.view().state.doc.childCount, 2, 'different markers: two lists');
  });

  test('ordered lists are 1. (bulletOrdered)', () => {
    assert.equal(ed.roundTrip('1) a\n2) b'), '1. a\n2. b\n');
  });

  test('ordered markers count up (incrementListMarker)', () => {
    assert.equal(ed.roundTrip('1. a\n1. b\n1. c'), '1. a\n2. b\n3. c\n');
  });

  test('headings are ATX (setext)', () => {
    assert.equal(ed.roundTrip('Title\n=====\n\nSub\n---'), '# Title\n\n## Sub\n');
  });

  test('ATX headings carry no closing hashes (closeAtx)', () => {
    assert.equal(ed.roundTrip('## H ##'), '## H\n');
  });

  test('code blocks are fenced (fences)', () => {
    assert.equal(ed.roundTrip('    code\n    more'), '```\ncode\nmore\n```\n');
  });

  test('emphasis made in the editor is * (emphasis)', () => {
    // The option is the default marker of the emphasis mark, so it is only
    // visible on emphasis the editor creates — Ctrl+I over plain text here.
    ed.roundTrip('italic');
    ed.selectText(1, 7);
    ed.editor.action(callCommand(toggleEmphasisCommand.key));
    assert.equal(ed.markdown(), '*italic*\n');
  });

  test('strong made in the editor is ** (strong)', () => {
    ed.roundTrip('bold');
    ed.selectText(1, 5);
    ed.editor.action(callCommand(toggleStrongCommand.key));
    assert.equal(ed.markdown(), '**bold**\n');
  });

  test('emphasis markers written in the source are kept as written', () => {
    // Milkdown's remarkMarker records the marker each emphasis was written
    // with and the serialiser reuses it; the option above never overrides it.
    const md = '_a_ and __b__ and *c* and **d**';
    assert.equal(ed.roundTrip(md), md + '\n');
  });

  test('a hard break is a backslash (HARD_BREAK)', () => {
    assert.equal(ed.roundTrip('a  \nb'), 'a\\\nb\n');
  });

  test('a hard break where no newline can go is a space, as before', () => {
    // The default break handler writes a space inside an ATX heading or a
    // table cell, where a newline would end the construct. HARD_BREAK is
    // applied by wrapping that handler, and this proves the rule survived.
    // Level 3, because remark writes a level-1/2 heading that holds a break as
    // setext (the one form that can carry it), whatever `setext` says.
    ed.roundTrip('### ab');
    const view = ed.view();
    view.dispatch(view.state.tr.insert(2, view.state.schema.nodes.hardbreak.create()));
    assert.equal(ed.markdown(), '### a b\n');
  });

  test('a tight bullet list stays tight (tightBulletList)', () => {
    assert.equal(ed.roundTrip('- one\n- two'), '- one\n- two\n');
  });

  test('a tight nested bullet list stays tight (tightListItem)', () => {
    assert.equal(ed.roundTrip('- one\n  - nested\n- two'), '- one\n  - nested\n- two\n');
  });

  test('a loose bullet list stays loose', () => {
    assert.equal(ed.roundTrip('- one\n\n- two'), '- one\n\n- two\n');
  });

  test('a tight task list stays tight', () => {
    assert.equal(ed.roundTrip('- [ ] a\n- [x] b'), '- [ ] a\n- [x] b\n');
  });

  test('a tight ordered list stays tight, as before', () => {
    assert.equal(ed.roundTrip('1. a\n2. b'), '1. a\n2. b\n');
  });
});

describe('IntrawordUnderscoreSurvives', () => {
  // R3. An underscore run with a word character on BOTH sides can neither open
  // nor close emphasis (CommonMark's flanking rules for `_`), so escaping it
  // only defaces the text. Everywhere else the escape stays: that is the guard
  // half of this block, and it is what keeps a literal `_x_` from turning into
  // emphasis on the next open.
  const unchanged = (md) => assert.equal(ed.roundTrip(md), md + '\n');

  test('snake_case_word survives unescaped', () => {
    unchanged('snake_case_word');
    unchanged('call get_user_by_id then done');
  });

  test('a run of underscores inside a word survives', () => {
    unchanged('a__b and a___b');
  });

  test('word characters are any script, not only ASCII', () => {
    unchanged('ключ_значение and 変数_名');
  });

  test('inside a table cell too', () => {
    assert.equal(
      ed.roundTrip('| a | b |\n|---|---|\n| c_d | e |'),
      '| a   | b |\n| --- | - |\n| c_d | e |\n');
  });

  test('guard: _italic_ and __bold__ still round-trip', () => {
    unchanged('_italic_ and __bold__');
    assert.equal(hasEmphasis(), true);
  });

  test('guard: a literal underscore at a word boundary is still escaped', () => {
    // Each of these would read as emphasis, or could, if the escape went.
    unchanged('\\_x\\_ y');
    assert.equal(hasEmphasis(), false, 'the escaped pair must not come back as emphasis');
    unchanged('w\\_ v and \\_w v');
    unchanged('start\\_');
  });

  test('guard: punctuation and symbols are boundaries, whatever the script', () => {
    // A `_` after an emoji (a symbol) or before a currency sign is flanking
    // by the spec, so it stays escaped. The neighbour is read as a code point,
    // not a UTF-16 unit, or the emoji would pass for a word character.
    unchanged('😀\\_x and a\\_€ and a\\_.b');
  });

  test('guard: the boundary is read across nodes', () => {
    // The character before a text node's leading `_` is the previous node's
    // last output character — here the closing `*` of the emphasis.
    unchanged('*em*\\_x and x\\_*em*');
  });

  test('guard: a literal backslash before an underscore stays literal', () => {
    // `a\\_b` in the file is a backslash then an underscore; both survive.
    assert.equal(ed.roundTrip('a\\\\_b'), 'a\\\\\\_b\n');
  });
});

describe('EmphasisBesidePunctuationSurvives', () => {
  // Milkdown's own emphasis and strong handlers write marker, content, marker
  // and nothing else. mdast-util-to-markdown's also look at the character on
  // each side of the run: where the one outside and the one inside would keep
  // the run from opening or closing — a letter outside and punctuation inside,
  // for one — the letter is written as a character reference, so the run still
  // reads as emphasis on the next open. src/conventions.js puts that back
  // (encodedAttention). Each case, through `survives`: the editor's output
  // re-parses to the SAME document, and a second pass changes nothing.

  test('a*_b*: emphasis opening on an underscore, after a letter', () => {
    // micromark lets a `*` run open when the next character is another
    // attention marker, so this is `a` + emphasis(`_b`). Written back as
    // `a*\_b*` the run could not open — `\` is punctuation and `a` is neither
    // whitespace nor punctuation — and the emphasis was gone on the next open.
    // The letter outside the run is encoded instead.
    assert.equal(survives('a*_b*'), '&#x61;*\\_b*\n');
    assert.equal(hasEmphasis(), true, 'the emphasis must come back as emphasis');
  });

  test('guards: a literal underscore against an emphasis or strong run', () => {
    // Each of these survived before; they hold the encoding to the one case
    // that needs it.
    assert.equal(survives('*a*_b'), '*a*\\_b\n');
    assert.equal(survives('**a**_b'), '**a**\\_b\n');
    assert.equal(survives('a_*b*'), 'a\\_*b*\n');
    assert.equal(survives('_a_*b*'), '_a_*b*\n');
  });

  test('guard: the neighbour is told the marker, the handler is not run for it', () => {
    // containerPhrasing asks the NEXT sibling what it starts with, through its
    // handler's peek. Without a peek it runs the handler itself, and the
    // default handler leaves its encoding decision behind for the wrong node:
    // here the `*` closing `*x*` would be encoded, and the first emphasis lost.
    assert.equal(survives('*x*a*_b*'), '*x*&#x61;*\\_b*\n');
  });

  test('strong made in the editor over a_ then b', () => {
    // The `**a\_**b` shape: a run closing on an escaped underscore with a
    // letter after it. The letter outside the run is encoded, and the document
    // the editor held is the document the file gives back.
    ed.roundTrip('a_b');
    ed.selectText(1, 3);
    ed.editor.action(callCommand(toggleStrongCommand.key));
    const made = ed.view().state.doc.toJSON();
    const out = ed.markdown();
    assert.equal(out, '**a\\_**&#x62;\n');
    ed.roundTrip(out);
    assert.deepEqual(ed.view().state.doc.toJSON(), made);
    assert.equal(survives(out), out);
  });
});

describe('AstralNeighboursSurvive', () => {
  // #2 F-A. The default handlers, and containerPhrasing after them, read the
  // character beside a run as a UTF-16 code unit, so where F-1's encoding
  // landed on an astral character — an emoji, a mathematical letter — the file
  // got a character reference to HALF of it (`\uD83D&#xDE00;*\_b*`), which
  // micromark decodes as U+FFFD: the text was destroyed and the second save
  // differed from the first. src/conventions.js now writes such a run plain
  // (encodedAttention's fallback). Pinned here: the text survives every pass —
  // no reference to half a character, no lone surrogate, no U+FFFD, and the
  // character itself present in each output and each re-opened document — and
  // the save is stable from the second one on. The mark is the accepted loss:
  // to micromark a surrogate half is a letter, and a `*` after a letter cannot
  // open on punctuation, so the next open reads the plain run as text and the
  // second save escapes its markers. The cases where that happens say so.
  const EMOJI = '\u{1F600}';   // U+1F600, a surrogate pair
  const MATH_A = '\u{1D400}';  // U+1D400 MATHEMATICAL BOLD CAPITAL A: a letter, and a surrogate pair
  const HALF_REFERENCE = /&#x[dD][89a-fA-F][0-9a-fA-F]{2};/;  // a reference to D800–DFFF, either half
  const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/;
  const intact = (s, ch, where) => {
    assert.doesNotMatch(s, HALF_REFERENCE, `${where}: a reference to half a character`);
    assert.doesNotMatch(s, LONE_SURROGATE, `${where}: a lone surrogate`);
    assert.doesNotMatch(s, /�/, `${where}: U+FFFD`);
    assert.ok(s.includes(ch), `${where}: ${JSON.stringify(ch)} is missing from ${JSON.stringify(s)}`);
  };
  /** Three saves: `ch` intact in every output and every re-opened document; the third save equals the second. Returns the first two. */
  const textSurvives = (md, ch) => {
    const s1 = ed.roundTrip(md);
    intact(s1, ch, 'the first save');
    const s2 = ed.roundTrip(s1);
    intact(ed.view().state.doc.textContent, ch, 'the document re-opened from the first save');
    intact(s2, ch, 'the second save');
    const s3 = ed.roundTrip(s2);
    intact(ed.view().state.doc.textContent, ch, 'the document re-opened from the second save');
    assert.equal(s3, s2, 'the third save must equal the second');
    return [s1, s2];
  };

  test('an emoji before a run opening on an underscore', () => {
    // Was `\uD83D&#xDE00;*\_b*`. Plain, the run cannot open on the next open
    // (the emoji's second half is a letter to micromark): the mark is lost and
    // the second save writes the markers as the literal text they now are.
    const [s1, s2] = textSurvives(`${EMOJI}*_b*`, EMOJI);
    assert.equal(s1, `${EMOJI}*\\_b*\n`);
    ed.roundTrip(s1);
    assert.equal(hasEmphasis(), false, 'the accepted loss: the run is text on the next open');
    assert.equal(s2, `${EMOJI}\\*\\_b\\*\n`);
  });

  test('the same after a letter, and with a mathematical letter', () => {
    assert.equal(textSurvives(`x${EMOJI}*_b*`, EMOJI)[0], `x${EMOJI}*\\_b*\n`);
    assert.equal(textSurvives(`${MATH_A}*_b*`, MATH_A)[0], `${MATH_A}*\\_b*\n`);
  });

  test('an astral letter after a run closing on a letter', () => {
    // `*_b_*` reads as emphasis written `_` around `b` (the two nestings
    // collapse to one mark). For `_` the default encodes the letter on both
    // sides of the closing run, and the outer one was `&#xD835;\uDC00`.
    const [s1, s2] = textSurvives(`*_b_*${MATH_A}`, MATH_A);
    assert.equal(s1, `_b_${MATH_A}\n`);
    assert.equal(s2, `\\_b_${MATH_A}\n`);
  });

  test('emphasis made in the editor over _b after an emoji', () => {
    ed.roundTrip(`${EMOJI}_b`);
    ed.selectText(3, 5);  // `_b`; the emoji is two positions
    ed.editor.action(callCommand(toggleEmphasisCommand.key));
    const out = ed.markdown();
    assert.equal(out, `${EMOJI}*\\_b*\n`);
    textSurvives(out, EMOJI);
  });

  test('strong made in the editor over a_ before an astral letter keeps its mark', () => {
    // The `**a\_**𝐀` shape — F-1's `**a\_**b` with the letter astral: the
    // reference to half of it was the only thing wrong. Closing on `_`, an
    // attention marker, the run closes whatever follows it, so written plain
    // this one comes back whole and the second save equals the first.
    ed.roundTrip(`a_${MATH_A}`);
    ed.selectText(1, 3);
    ed.editor.action(callCommand(toggleStrongCommand.key));
    const made = ed.view().state.doc.toJSON();
    const out = ed.markdown();
    assert.equal(out, `**a\\_**${MATH_A}\n`);
    ed.roundTrip(out);
    assert.deepEqual(ed.view().state.doc.toJSON(), made);
    assert.equal(survives(out), out);
    textSurvives(out, MATH_A);
  });

  test('a letter typed beside _emoji_ read from a file: the encoding would land inside the run', () => {
    // `_😀_` is emphasis written `_`. A letter typed against it with the mark
    // off puts a letter outside and the emoji inside, and for `_` the default
    // encodes BOTH — the emoji's first half, or its last. The run is written
    // plain; on the next open it is text (a `_` between two letters can open
    // or close nothing), and the second save escapes the underscores.
    const typed = (md, pos) => {
      ed.roundTrip(md);
      const view = ed.view();
      view.dispatch(view.state.tr.insert(pos, view.state.schema.text('a')));  // a bare text node: no marks
      return ed.markdown();
    };
    const head = typed(`_${EMOJI}_`, 1);
    assert.equal(head, `a_${EMOJI}_\n`);
    assert.equal(textSurvives(head, EMOJI)[1], `a\\_${EMOJI}\\_\n`);
    const tail = typed(`_${EMOJI}_`, 3);
    assert.equal(tail, `_${EMOJI}_a\n`);
    assert.equal(textSurvives(tail, EMOJI)[1], `\\_${EMOJI}\\_a\n`);
  });
});
