// Render inline/block raw HTML (e.g. a centered logo: <p align="center"><img …>)
// instead of showing it as escaped text (Milkdown's default). The HTML is
// sanitized with DOMPurify (scripts, event handlers, iframes, etc. stripped)
// before being injected, and the model keeps the ORIGINAL unsanitized string in
// data-value so saving round-trips the markdown/HTML unchanged.

import { $nodeSchema } from '@milkdown/kit/utils';
import DOMPurify from 'dompurify';
import { SANITIZE_OPTS } from './sanitize.js';

export const htmlRender = $nodeSchema('html', () => ({
  atom: true,
  group: 'inline',
  inline: true,
  attrs: { value: { default: '' } },
  toDOM: (node) => {
    const dom = document.createElement('span');
    dom.className = 'mdm-html';
    dom.setAttribute('data-type', 'html');
    dom.setAttribute('data-value', node.attrs.value);
    dom.setAttribute('contenteditable', 'false');
    try {
      dom.innerHTML = DOMPurify.sanitize(String(node.attrs.value || ''), SANITIZE_OPTS);
    } catch (_) {
      dom.textContent = node.attrs.value;
    }
    return dom;
  },
  parseDOM: [{
    tag: 'span[data-type="html"]',
    getAttrs: (dom) => ({ value: dom.dataset.value ?? '' }),
  }],
  parseMarkdown: {
    match: ({ type }) => type === 'html',
    runner: (state, node, type) => state.addNode(type, { value: node.value }),
  },
  toMarkdown: {
    match: (node) => node.type.name === 'html',
    runner: (state, node) => state.addNode('html', undefined, node.attrs.value),
  },
}));
