// The round-trip harness (release plan 0.12, R1): markdown in → the real editor
// → markdown out, under node --test.
//
// The editor is the one the app ships — editor-factory.js, mounted in jsdom by
// jsdom-editor.mjs — because the serialiser's behaviour is not remark's alone:
// what a list looks like on the way out is decided by attributes the ProseMirror
// schema put on it on the way in. A bare mdast round trip would pass where the
// app fails.
//
// Two kinds of assertion live here. `Idempotent` is the invariant: whatever the
// editor makes of a document, it makes the same of its own output, so a save is
// stable after the first one. `MeasuredRewritesAreReproduced` is the record:
// one assertion per row of the plan's measured table, stating what the editor
// does to that construct today. When a convention is pinned (R2) the row's
// expectation is changed on purpose, with the reason beside it — never by
// regenerating.
import test, { before, describe } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
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
    assert.match(out, /^# Title From Setext$/m);
    assert.doesNotMatch(out, /^=+$/m);
  });

  test('closing hashes on an ATX heading are stripped', () => {
    assert.match(out, /^## Heading with trailing hashes$/m);
  });

  test('+ bullets become *, and the adjacent * list becomes -', () => {
    // (The blank line between the two + items is the tight-list row below,
    // which affects every bullet list, not only the one the table names.)
    assert.match(out, /^\* plus bullet one\n\n\* plus bullet two$/m);
    assert.match(out, /^- star bullet$/m);
  });

  test('1) ordered becomes 1.', () => {
    assert.match(out, /^1\. paren ordered\n2\. paren ordered two$/m);
  });

  test('a reference link is inlined and its definition deleted', () => {
    assert.match(out, /\[reference link\]\(https:\/\/example\.com\/ref "Ref Title"\)/);
    assert.doesNotMatch(out, /^\[ref\]:/m);
  });

  test('an indented code block becomes fenced', () => {
    assert.match(out, /^```\nindented code block\nsecond line\n```$/m);
  });

  test('two trailing spaces become a backslash hard break', () => {
    assert.match(out, /^Line with two trailing spaces\\\ncontinues here\.$/m);
  });

  test('intraword underscores are escaped', () => {
    assert.match(out, /snake\\_case\\_word/);
  });

  test('the table is reformatted to the widest cell', () => {
    assert.match(out, /^\| Left \| Right \|\n\| :--- \| ----: \|\n\| a {4}\| {5}b \|$/m);
  });

  test('a tight bullet list comes back loose', () => {
    // The task list is the fixture's `-` list. Its marker is `*` here because
    // the serialiser's default bullet is `*`; the table between it and the
    // previous list means it is not "adjacent", so no alternation.
    assert.match(out, /^\* \[ \] task open\n\n\* \[x\] task done$/m);
  });
});
