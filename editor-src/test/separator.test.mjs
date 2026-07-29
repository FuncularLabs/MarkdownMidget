// The repeated-word deletion refuses to act unless the separator between the two
// occurrences matches this class, so a silent change here becomes a menu item that
// does nothing. Written with escapes for exactly that reason — these tests pin the
// membership so a tool that mangled the class can't ship green.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SEPARATOR } from '../src/spell-separator.js';

const matches = (cp) => SEPARATOR.test(String.fromCodePoint(cp));

test('matches ordinary whitespace', () => {
  for (const cp of [0x20, 0x09, 0x0A, 0x0D, 0x00A0, 0x2028, 0x2029, 0x3000]) {
    assert.ok(matches(cp), `U+${cp.toString(16)} should be a separator`);
  }
});

test('matches the invisible characters that arrive with pasted text', () => {
  for (const cp of [0x00AD, 0x200B, 0x200C, 0x200D, 0xFEFF]) {
    assert.ok(matches(cp), `U+${cp.toString(16).toUpperCase()} should be a separator`);
  }
});

test('does NOT match hyphen — the degraded-regex failure mode', () => {
  // If the escapes were ever stripped, the class collapses to /[\s-]/, which is a
  // VALID regex that quietly starts treating '-' as a word separator.
  assert.ok(!SEPARATOR.test('-'));
});

test('does not match ordinary word characters', () => {
  for (const ch of ['a', 'Z', '0', "'", '_', '.']) assert.ok(!SEPARATOR.test(ch));
});
