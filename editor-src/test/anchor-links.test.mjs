// A `#fragment` link jumps to its heading (anchor-links.js). Slugs are DocAnchorLinksTests' cases; the jump is the shipped
// editor, clicked the way Chromium delivers a click on a link: Ctrl+click where it is editable, a plain click where it is not.
import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { mountEditor } from './jsdom-editor.mjs';
let m, slug;
before(async () => { m = await mountEditor(); ({ slug } = await import('../src/anchor-links.js')); });
const click = (href, init = { ctrlKey: true }) => { const e = new m.window.MouseEvent('click', { bubbles: true, cancelable: true, ...init }); m.view().dom.querySelector(`a[href="${href}"]`).dispatchEvent(e); return e; };
const caret = () => { const { $head } = m.view().state.selection; return $head.parent.type.name === 'heading' && !$head.parentOffset ? `${$head.index(0)}:${$head.parent.textContent}` : $head.pos; };

test("a slug is DocAnchorLinksTests' slug", () => {
  for (const [h, s] of [['Known limits', 'known-limits'], ['Markdown Midget — Help', 'markdown-midget--help'], ['Secure Markdown (encrypted documents)', 'secure-markdown-encrypted-documents'],
    ['Distribution (single-file builds)', 'distribution-single-file-builds'], ['Formatting marks (¶)', 'formatting-marks-'], ['Host ↔ editor bridge (`window.MDM`)', 'host--editor-bridge-windowmdm'],
    ['Decided 2026-08-13 — how a portable sibling learns it is superseded', 'decided-2026-08-13--how-a-portable-sibling-learns-it-is-superseded']]) assert.equal(slug(h), s);
});

test("HELP's Known limits link: a plain click edits, Ctrl+click jumps and scrolls #app, and read-only a plain click jumps", () => {
  m.roundTrip(readFileSync(new URL('../../HELP.md', import.meta.url), 'utf8'));
  const app = m.window.document.getElementById('app'), start = caret();
  Object.defineProperty(app, 'scrollTop', { value: 100, writable: true });   // jsdom has no layout: the heading sits 900px down
  [...m.view().dom.querySelectorAll('h2')].find((h) => h.firstChild?.nodeValue === 'Known limits').getBoundingClientRect = () => ({ top: 900 });
  assert.equal(click('#known-limits', {}).defaultPrevented, false); assert.equal(caret(), start);
  assert.equal(click('#known-limits').defaultPrevented, true); assert.match(String(caret()), /^\d+:Known limits$/); assert.equal(app.scrollTop, 992);
  m.selectText(1, 1); m.view().setProps({ editable: () => false });
  try { click('#known-limits', {}); assert.match(String(caret()), /^\d+:Known limits$/); } finally { m.view().setProps({ editable: () => true }); }
});

test('a repeated heading takes its numbered anchor, and a percent-encoded fragment is decoded', () => {
  m.roundTrip('### Fixed\n\n### Fixed\n\n### Fixed\n\n## Café\n\n[a](#fixed-2) [b](#caf%C3%A9)');
  click('#fixed-2'); assert.equal(caret(), '2:Fixed');
  click('#caf%C3%A9'); assert.equal(caret(), '3:Café');
});

test('an unmatched or malformed fragment does nothing, and an external link is left alone', () => {
  m.roundTrip('## Here\n\n[a](#nowhere) [b](#100%) [c](https://example.com/#here)');
  m.selectText(2, 2);
  assert.equal(click('#nowhere').defaultPrevented, true); click('#100%'); assert.equal(caret(), 2);
  assert.equal(click('https://example.com/#here').defaultPrevented, false); assert.equal(caret(), 2);
});
