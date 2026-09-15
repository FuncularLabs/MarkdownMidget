// Copy Link (link-at.js) reports the href a link's mark holds, as written; a.href would not do, the page's <base> rewrites it.
import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';
let m, linkAt; before(async () => { m = await mountEditor(); ({ linkAt } = await import('../src/link-at.js')); m.window.document.head.insertAdjacentHTML('beforeend', '<base href="https://mdm-doc.invalid/">'); });
const a = (text) => [...m.view().dom.querySelectorAll('a')].find((el) => el.textContent === text);
test('a right-click on a link reports its mark href: relative, absolute, #fragment, autolink, in a table, list or quote', () => {
  m.roundTrip('[rel](docs/HELP.md) [**abs**](https://example.com/a?b=1) [top](#known-limits) <https://auto.example/x>\n\n| t |\n|---|\n| [cell](mailto:a@b.example?subject=Hi%20there&body=See%20this) |\n\n- [item](../x.md)\n\n> [quoted](#q)');
  const want = { rel: 'docs/HELP.md', abs: 'https://example.com/a?b=1', top: '#known-limits', 'https://auto.example/x': 'https://auto.example/x', cell: 'mailto:a@b.example?subject=Hi%20there&body=See%20this', item: '../x.md', quoted: '#q' };
  for (const [text, href] of Object.entries(want)) assert.equal(linkAt(m.view(), a(text).querySelector('strong') ?? a(text)), href, text);
  assert.equal(a('rel').href, 'https://mdm-doc.invalid/docs/HELP.md');   // what the DOM would have said
});
test('a right-click off a link or on a raw-HTML <a> reports none, a linked picture reports its link, and with no target the caret decides', () => {
  m.roundTrip('plain **bold** [link](x.md) ![](i.png) [![](j.png)](y.md)<a href="z">raw</a>');
  const p = m.view().dom.querySelector('p'), [pic, linked] = p.querySelectorAll('img'), raw = p.querySelector('a[href="z"]'), at = (t) => linkAt(m.view(), t);
  assert.deepEqual([at(p), at(p.querySelector('strong')), at(pic), at(raw), at(linked)], [undefined, undefined, undefined, undefined, 'y.md']);
  m.selectText(3, 3); assert.equal(at(null), undefined);
  const inside = m.view().posAtDOM(a('link').firstChild, 2); m.selectText(inside, inside); assert.equal(at(null), 'x.md');
});
