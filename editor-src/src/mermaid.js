// Mermaid diagram rendering for fenced ```mermaid code blocks.
//
// Strategy: a Prose plugin that adds two decorations per mermaid code_block —
//   1) A widget after the block containing the rendered SVG (cached by look and source).
//   2) A `node` decoration that adds the `mdm-mermaid-active` CSS class to the
//      code_block when the cursor is inside it.
// The screen CSS hides mermaid code blocks by default and reveals them while
// active, so the user sees only the diagram unless they're editing the source.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import mermaid from 'mermaid';
import { mermaidLook, sameLook, mermaidConfig, cacheKey, DEFAULT_FONT_PX } from './mermaid-look.js';

// Mermaid's theme and the document's text size (mermaid-look.js).
let currentLook = mermaidLook('default', DEFAULT_FONT_PX);

// Mermaid's config is global, and a render reads it when it STARTS. So diagrams are drawn
// one at a time, each under its own config, handed over just before it starts and left
// alone until it has finished: a diagram waiting its turn is drawn under the look it was
// asked for, not whatever a later switch set, and a journey can be drawn at mermaid's own
// size between two flowcharts at the document's (KEPT_SIZE_TYPES).
let queue = Promise.resolve();
let handed = null;   // the config mermaid holds, as JSON
function hand(config) {
  const json = JSON.stringify(config);
  if (json !== handed) { mermaid.initialize(config); handed = json; }
}
function draw(id, source, look) {
  const run = queue.then(() => {
    if (handed === null) hand(mermaidConfig(look));   // mermaid learns its diagram types here
    let type = null;
    try { type = mermaid.detectType(source); } catch { /* a type it doesn't know: render says so */ }
    hand(mermaidConfig(look, type));
    return mermaid.render(id, source);
  });
  queue = run.catch(() => {});
  return run;
}

// Cache rendered SVG by look and source text to keep keystrokes fast.
const svgCache = new Map(); // cacheKey(look, source) -> {svg, error}
let renderTicket = 0;

// Bumped when the theme or size changes and mixed into the decoration key. Without it
// ProseMirror sees the same key for the same source, reuses the widget DOM it
// already has, and never calls the factory again — so every diagram already on
// screen keeps the old theme while anything typed afterwards gets the new one.
let themeEpoch = 0;

function escapeHtml(s) {
  return s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

async function renderInto(container, source, epoch, look) {
  if (!source.trim()) {
    container.innerHTML = '<div class="mdm-mermaid-empty">(empty mermaid block)</div>';
    container.classList.remove('mdm-mermaid-error');
    return;
  }
  const key = cacheKey(look, source);
  const cached = svgCache.get(key);
  if (cached) {
    container.innerHTML = cached.svg;
    container.classList.toggle('mdm-mermaid-error', !!cached.error);
    return;
  }
  try {
    const id = 'mdm-mermaid-' + (++renderTicket);
    const { svg } = await draw(id, source, look);
    // A theme or size switch during the await asked every diagram to render again under
    // the new look, in a fresh call with a new epoch, and swapped this container out of the
    // page. So this older result shows nowhere and is not kept. A drawing kept by mistake
    // would still be of the look its key names (draw() sees to that), but a failure would
    // not be the diagram's: the look can come back, and the error box with it (the catch
    // below, test/mermaid-render.test.mjs).
    if (epoch !== themeEpoch) return;
    svgCache.set(key, { svg, error: false });
    container.innerHTML = svg;
    container.classList.remove('mdm-mermaid-error');
  } catch (e) {
    if (epoch !== themeEpoch) return;
    const msg = (e && e.message ? e.message : String(e)).split('\n')[0];
    const html = '<pre class="mdm-mermaid-error-msg">' + escapeHtml(msg) + '</pre>';
    svgCache.set(key, { svg: html, error: true });
    container.innerHTML = html;
    container.classList.add('mdm-mermaid-error');
  }
}

// Lightweight hash for keying decorations so identical source reuses the same DOM.
function hash(s) {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = ((h * 31 + s.charCodeAt(i)) | 0);
  return h;
}

const PLUGIN_KEY = new PluginKey('mdmMermaid');

function buildDecorations(doc, selection) {
  const decos = [];
  const cursor = selection?.head;
  doc.descendants((node, pos) => {
    if (node.type.name !== 'code_block') return;
    if ((node.attrs.language || '').toLowerCase() !== 'mermaid') return;

    const start = pos;
    const end = pos + node.nodeSize;
    const source = node.textContent;

    // Mark the code_block "active" when the cursor lives inside it.
    if (typeof cursor === 'number' && cursor >= start && cursor <= end) {
      decos.push(Decoration.node(start, end, { class: 'mdm-mermaid-active' }));
    }

    // Render the diagram after the block. The epoch is captured HERE, at widget
    // creation, not read fresh inside renderInto — it has to be the epoch this
    // particular render was asked for under, so a later switch can tell this call
    // apart from the new one it triggered for the same source. The look too.
    const epoch = themeEpoch;
    const look = currentLook;
    const key = 'mermaid:' + epoch + ':' + hash(source) + ':' + source.length;
    decos.push(Decoration.widget(end, () => {
      const container = document.createElement('div');
      container.className = 'mdm-mermaid';
      container.setAttribute('contenteditable', 'false');
      renderInto(container, source, epoch, look);
      return container;
    }, { side: 1, ignoreSelection: true, key }));
  });
  return DecorationSet.create(doc, decos);
}

/**
 * Point mermaid at a different built-in theme or document text size (px), and redraw
 * what is already on screen. What each step does:
 *
 *   - the new look becomes the one every diagram asks for from here on; draw() hands
 *     it to mermaid as each one's turn comes, so nothing is initialised here;
 *   - the epoch is bumped and the plugin told to rebuild, because ProseMirror keeps
 *     DOM it believes is unchanged: without it the diagrams on screen keep the old look;
 *   - the SVG cache is emptied. Housekeeping only: its keys carry the look, so nothing
 *     in it could be served under the new one; it would only take up room.
 *
 * Returns false when nothing changed, so the caller can skip the redraw.
 */
export function setMermaidTheme(view, name, fontSize) {
  const next = mermaidLook(name, fontSize);
  if (sameLook(next, currentLook)) return false;

  currentLook = next;
  svgCache.clear();
  themeEpoch++;

  if (view) view.dispatch(view.state.tr.setMeta(PLUGIN_KEY, 'retheme'));
  return true;
}

export const mermaidBlock = $prose(() => new Plugin({
  key: PLUGIN_KEY,
  state: {
    init(_, state) { return buildDecorations(state.doc, state.selection); },
    apply(tr, set, oldState, newState) {
      // A theme change alters neither the document nor the selection, so it needs
      // to say so explicitly or the early return below swallows it.
      if (tr.getMeta(PLUGIN_KEY) === 'retheme') return buildDecorations(newState.doc, newState.selection);
      if (!tr.docChanged && oldState.selection.eq(newState.selection)) return set;
      return buildDecorations(newState.doc, newState.selection);
    },
  },
  props: {
    decorations(state) { return PLUGIN_KEY.getState(state); },
  },
}));
