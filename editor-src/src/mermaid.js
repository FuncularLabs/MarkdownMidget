// Mermaid diagram rendering for fenced ```mermaid code blocks.
//
// Strategy: a Prose plugin that adds two decorations per mermaid code_block —
//   1) A widget after the block containing the rendered SVG (cached by source).
//   2) A `node` decoration that adds the `mdm-mermaid-active` CSS class to the
//      code_block when the cursor is inside it.
// The screen CSS hides mermaid code blocks by default and reveals them while
// active, so the user sees only the diagram unless they're editing the source.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import mermaid from 'mermaid';

// Mermaid's own built-ins. A theme names one in `--mdm-mermaid-theme`; anything
// else falls back, because that value comes out of a user's stylesheet and mermaid
// throws on a name it doesn't recognise — which would replace every diagram in the
// document with an error box.
const THEMES = ['default', 'dark', 'neutral', 'forest', 'base'];
let currentTheme = 'default';

let mermaidReady = false;
function initOnce() {
  if (mermaidReady) return;
  mermaid.initialize({ startOnLoad: false, theme: currentTheme, securityLevel: 'strict' });
  mermaidReady = true;
}

// Cache rendered SVG by source text to keep keystrokes fast.
const svgCache = new Map(); // source -> {svg, error}
let renderTicket = 0;

// Bumped when the theme changes and mixed into the decoration key. Without it
// ProseMirror sees the same key for the same source, reuses the widget DOM it
// already has, and never calls the factory again — so every diagram already on
// screen keeps the old theme while anything typed afterwards gets the new one.
let themeEpoch = 0;

function escapeHtml(s) {
  return s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

async function renderInto(container, source, epoch) {
  if (!source.trim()) {
    container.innerHTML = '<div class="mdm-mermaid-empty">(empty mermaid block)</div>';
    container.classList.remove('mdm-mermaid-error');
    return;
  }
  const cached = svgCache.get(source);
  if (cached) {
    container.innerHTML = cached.svg;
    container.classList.toggle('mdm-mermaid-error', !!cached.error);
    return;
  }
  initOnce();
  try {
    const id = 'mdm-mermaid-' + (++renderTicket);
    const { svg } = await mermaid.render(id, source);
    // A theme switch during the await just cleared the cache and asked every
    // diagram to re-render under the NEW theme — including this exact source,
    // in a fresh call with the new epoch. If this older call is still the one
    // to resolve, writing its (stale-theme) result to the cache now would
    // silently overwrite what the new call already wrote — the "stale diagram"
    // bug this file's whole epoch/cache-clear mechanism exists to prevent,
    // reintroduced through a race in the cache instead of the decoration key.
    // The container itself is already detached — ProseMirror swapped it for a
    // new one under the new decoration key — so discarding here loses nothing
    // that was still visible.
    if (epoch !== themeEpoch) return;
    svgCache.set(source, { svg, error: false });
    container.innerHTML = svg;
    container.classList.remove('mdm-mermaid-error');
  } catch (e) {
    if (epoch !== themeEpoch) return;
    const msg = (e && e.message ? e.message : String(e)).split('\n')[0];
    const html = '<pre class="mdm-mermaid-error-msg">' + escapeHtml(msg) + '</pre>';
    svgCache.set(source, { svg: html, error: true });
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
    // apart from the new one it triggered for the same source.
    const epoch = themeEpoch;
    const key = 'mermaid:' + epoch + ':' + hash(source) + ':' + source.length;
    decos.push(Decoration.widget(end, () => {
      const container = document.createElement('div');
      container.className = 'mdm-mermaid';
      container.setAttribute('contenteditable', 'false');
      renderInto(container, source, epoch);
      return container;
    }, { side: 1, ignoreSelection: true, key }));
  });
  return DecorationSet.create(doc, decos);
}

/**
 * Point mermaid at a different built-in theme and redraw what is already on screen.
 *
 * Three things have to happen together, and leaving any one out looks like the
 * feature half-working rather than like a bug:
 *
 *   - `mermaidReady` goes back to false, or `initialize` never runs again and the
 *     new theme is simply never handed to mermaid;
 *   - the SVG cache is cleared, because it is keyed on source text alone and would
 *     otherwise re-serve the previous theme's rendering for every diagram already
 *     in the document;
 *   - the decoration key changes and the plugin is told to rebuild, because
 *     ProseMirror keeps DOM it believes is unchanged.
 *
 * Returns false when nothing changed, so the caller can skip the redraw.
 */
export function setMermaidTheme(view, name) {
  const next = THEMES.includes(name) ? name : 'default';
  if (next === currentTheme) return false;

  currentTheme = next;
  mermaidReady = false;
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
