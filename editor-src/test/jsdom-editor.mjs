// The REAL editor, mounted in jsdom, for tests that need markdown to pass through
// the ProseMirror document and come back out — the serialiser's list `spread`
// behaviour, for one, comes from the schema and never shows up in a bare
// remark round trip.
//
// The editor is built by editor-factory.js, the same construction main.js uses,
// so the plugin set here IS the shipped one. main.js itself is not importable
// under Node: it imports the CSS bundle and wires up the host bridge at load.
//
// Globals are installed BEFORE the factory is imported (hence the dynamic
// import): prosemirror-view captures `navigator`/`document` at module load, and
// dompurify binds whichever `window` exists when it is first evaluated.
import { JSDOM } from 'jsdom';
import { replaceAll, getMarkdown } from '@milkdown/kit/utils';

// Window properties copied onto globalThis. Each is something the editor stack
// reads as a bare global at module load or at run time.
const GLOBALS = [
  'window', 'document', 'navigator',
  'Node', 'Element', 'HTMLElement', 'Text', 'DocumentFragment', 'Range',
  'MutationObserver', 'DOMParser', 'getComputedStyle', 'getSelection',
  'requestAnimationFrame', 'cancelAnimationFrame',
  'Event', 'CustomEvent', 'KeyboardEvent', 'MouseEvent', 'InputEvent',
];

// Window methods that are called as bare functions. @milkdown/ctx's Timer
// signals readiness with `dispatchEvent(new CustomEvent(...))` and waits with
// `addEventListener(name, fn)` — unqualified, so `this` must still be the
// window, hence the bind (jsdom throws "Illegal invocation" otherwise).
const WINDOW_METHODS = ['addEventListener', 'removeEventListener', 'dispatchEvent'];

function installGlobals(window) {
  for (const name of GLOBALS) {
    Object.defineProperty(globalThis, name, { value: window[name], configurable: true, writable: true });
  }
  for (const name of WINDOW_METHODS) {
    Object.defineProperty(globalThis, name, { value: window[name].bind(window), configurable: true, writable: true });
  }
}

// What jsdom does not implement and ProseMirror asks for. Every stub says which
// call needs it; none of them affect the document model or the serialiser.
function installStubs(window) {
  const rect = () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0, x: 0, y: 0 });
  const rects = () => [];
  // prosemirror-view coordsAtPos/scrollIntoView measure text through a Range:
  // textRange(node, from, to).getClientRects() / getBoundingClientRect().
  window.Range.prototype.getClientRects = rects;
  window.Range.prototype.getBoundingClientRect = rect;
  // prosemirror-view scrollRectIntoView walks up from view.dom measuring each
  // ancestor with getBoundingClientRect; jsdom returns all-zero rects already,
  // and getClientRects exists but returns an empty list. Nothing to add here.
  // prosemirror-view posAtCoords starts from document.elementFromPoint; jsdom
  // has no layout, so there is nothing under any point.
  window.document.elementFromPoint = () => null;
}

/**
 * Mount one editor. Call once per test file (module caching means a second
 * mount would reuse the factory bound to the first window); drive it with
 * roundTrip, which loads and reads the document exactly the way the host does
 * through MDM.setMarkdown / MDM.getMarkdown.
 */
export async function mountEditor(initialMarkdown = '') {
  const dom = new JSDOM('<!DOCTYPE html><html><body><div id="app"></div></body></html>', {
    pretendToBeVisual: true,   // gives requestAnimationFrame
  });
  const { window } = dom;
  installGlobals(window);
  installStubs(window);

  const { createEditor } = await import('../src/editor-factory.js');
  const root = window.document.getElementById('app');
  const editor = await createEditor({ root, initialMarkdown });

  return {
    editor,
    window,
    /** markdown in → the editor → markdown out. */
    roundTrip(markdown) {
      editor.action(replaceAll(markdown, true));
      return editor.action(getMarkdown());
    },
  };
}
