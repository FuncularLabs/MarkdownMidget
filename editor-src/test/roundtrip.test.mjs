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
// regenerating. `ConventionsArePinned` and `IntrawordUnderscoreSurvives` hold
// the forms src/conventions.js chooses, one case each, so a drift in any of
// them is a red case with a name.
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
  const hasEmphasis = () => {
    let found = false;
    ed.view().state.doc.descendants((n) => { if (n.marks.some((m) => m.type.name === 'emphasis')) found = true; });
    return found;
  };

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
