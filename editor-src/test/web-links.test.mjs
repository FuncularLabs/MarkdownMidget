// A web link is handed to the host to confirm and open (web-links.js). The host here is a stub that records: nothing opens.
import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';
let m; const posted = [], web = (url) => [{ type: 'openLink', url }];
before(async () => { m = await mountEditor(); m.window.chrome = { webview: { postMessage: (msg) => posted.push(msg) } }; });
const click = (text, init = { ctrlKey: true }) => { posted.length = 0; const e = new m.window.MouseEvent('click', { bubbles: true, cancelable: true, ...init }); [...m.view().dom.querySelectorAll('a')].find((a) => a.textContent === text).dispatchEvent(e); return e; };
test("Ctrl+click posts openLink with the mark's href, and a plain click in the editable view posts nothing", () => {
  m.roundTrip('[web](https://example.com/a?b=1)');
  assert.equal(click('web', {}).defaultPrevented, false); assert.deepEqual(posted, []);
  assert.equal(click('web').defaultPrevented, true); assert.deepEqual(posted, web('https://example.com/a?b=1'));
});
test('a relative, file:, javascript: or mailto: link is refused, though <base> makes a relative a.href look like a web address', () => {
  m.window.document.head.insertAdjacentHTML('beforeend', '<base href="https://mdm-doc.invalid/">');
  m.roundTrip('[rel](other.md) [file](file:///C:/Windows/win.ini) [js](javascript:alert(1)) [mail](mailto:someone@example.com)');
  for (const t of ['rel', 'file', 'js', 'mail']) { assert.equal(click(t).defaultPrevented, true); assert.deepEqual(posted, [{ type: 'linkRefused' }]); }
});
test('a #fragment link posts nothing, and in a read-only view a plain click posts the web link', () => {
  m.roundTrip('## Here\n\n[frag](#here) [web](https://example.com/)');
  assert.equal(click('frag').defaultPrevented, true); assert.deepEqual(posted, []);
  m.view().setProps({ editable: () => false }); try { click('web', {}); assert.deepEqual(posted, web('https://example.com/')); } finally { m.view().setProps({ editable: () => true }); }
});
