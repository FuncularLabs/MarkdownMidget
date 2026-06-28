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

let mermaidReady = false;
function initOnce() {
  if (mermaidReady) return;
  mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'strict' });
  mermaidReady = true;
}

// Cache rendered SVG by source text to keep keystrokes fast.
const svgCache = new Map(); // source -> {svg, error}
let renderTicket = 0;

function escapeHtml(s) {
  return s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
}

async function renderInto(container, source) {
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
    svgCache.set(source, { svg, error: false });
    container.innerHTML = svg;
    container.classList.remove('mdm-mermaid-error');
  } catch (e) {
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

    // Render the diagram after the block.
    const key = 'mermaid:' + hash(source) + ':' + source.length;
    decos.push(Decoration.widget(end, () => {
      const container = document.createElement('div');
      container.className = 'mdm-mermaid';
      container.setAttribute('contenteditable', 'false');
      renderInto(container, source);
      return container;
    }, { side: 1, ignoreSelection: true, key }));
  });
  return DecorationSet.create(doc, decos);
}

export const mermaidBlock = $prose(() => new Plugin({
  key: PLUGIN_KEY,
  state: {
    init(_, state) { return buildDecorations(state.doc, state.selection); },
    apply(tr, set, oldState, newState) {
      if (!tr.docChanged && oldState.selection.eq(newState.selection)) return set;
      return buildDecorations(newState.doc, newState.selection);
    },
  },
  props: {
    decorations(state) { return PLUGIN_KEY.getState(state); },
  },
}));
