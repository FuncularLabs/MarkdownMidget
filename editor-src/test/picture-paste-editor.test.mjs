// The real editor applies the picture ceiling to a paste — inside ProseMirror's
// own paste handling, not beside it.
//
// picture-paste.test.mjs pins the decision. This pins where it is asked: from a
// handleDOMEvents.paste handler in editor-factory.js, which ProseMirror runs
// before its own paste handler, and so before capturePaste — the step that hands
// a picture-only clipboard to Chromium's native paste. The editor is the shipped
// one (editor-factory.js, mounted in jsdom); the event is synthetic, since jsdom
// has no clipboard, and shaped like the one Chromium dispatches on Ctrl+V.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { mountEditor } from './jsdom-editor.mjs';

const CEILING = 1000;
const refused = [];
let mounted;

before(async () => {
  mounted = await mountEditor('', { maxPictureBytes: CEILING, onPictureRefused: (size) => refused.push(size) });
});

/** A clipboard with `text` (if any) and one picture item per size. */
function clipboard({ text = '', images = [] }) {
  return {
    getData: (type) => (type === 'text/plain' ? text : ''),
    items: images.map((size) => ({
      kind: 'file',
      type: 'image/png',
      getAsFile: () => ({ name: 'image.png', type: 'image/png', size }),
    })),
  };
}

/** Paste `data` into the editor the way Chromium delivers Ctrl+V; returns the event. */
function paste(data) {
  const event = new mounted.window.Event('paste', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'clipboardData', { value: data });
  mounted.view().dom.dispatchEvent(event);
  return event;
}

test("a picture past the host's ceiling, pasted on its own, is cancelled before anything can paste it", () => {
  refused.length = 0;
  mounted.roundTrip('Some text');
  const event = paste(clipboard({ images: [CEILING + 1] }));
  // Cancelled, so Chromium's native paste — the one that would have inserted the
  // picture — never runs; and reported, so the host can say why.
  assert.equal(event.defaultPrevented, true);
  assert.deepEqual(refused, [CEILING + 1]);
  assert.equal(mounted.markdown().trim(), 'Some text');
});

test('a picture under the ceiling is left to the paste that has always inserted it', async () => {
  refused.length = 0;
  mounted.roundTrip('Some text');
  const event = paste(clipboard({ images: [CEILING] }));
  // Neither cancelled nor reported: ProseMirror found no text, so it hands the
  // paste on to the browser (capturePaste), exactly as it did before the guard.
  assert.equal(event.defaultPrevented, false);
  assert.deepEqual(refused, []);
  // capturePaste finishes on a 50 ms timer; let it, before the next test.
  await new Promise((resolve) => setTimeout(resolve, 100));
});

test('a picture that comes with text is left to ProseMirror, which pastes the text', () => {
  refused.length = 0;
  mounted.roundTrip('');
  const event = paste(clipboard({ text: 'pasted words', images: [CEILING * 10] }));
  assert.deepEqual(refused, []);
  assert.equal(event.defaultPrevented, true);   // by ProseMirror, having pasted the text itself
  assert.match(mounted.markdown(), /pasted words/);
});

test('a read-only view takes no paste, and reports no refusal for one', () => {
  refused.length = 0;
  const view = mounted.view();
  view.setProps({ editable: () => false });
  try {
    paste(clipboard({ images: [CEILING + 1] }));
    assert.deepEqual(refused, []);
  } finally {
    view.setProps({ editable: () => true });
  }
});
