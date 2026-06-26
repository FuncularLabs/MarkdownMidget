// Image node that carries width/height. Markdown has no image sizing, so a sized
// image round-trips as inline HTML <img …>; an unsized one stays as ![alt](src).

import { $nodeSchema, $remark } from '@milkdown/kit/utils';

function attr(html, name) {
  const m = html.match(new RegExp(`${name}\\s*=\\s*"([^"]*)"`, 'i'));
  return m ? m[1] : '';
}

// Parse inline <img …> HTML (which remark leaves as `html` nodes) into image nodes
// carrying width/height in node.data, so the schema's parseMarkdown can read them.
export const remarkImageSize = $remark('remarkImageSize', () => () => (tree) => {
  const walk = (node) => {
    if (!Array.isArray(node.children)) return;
    for (const child of node.children) {
      if (child.type === 'html' && /^<img\b/i.test((child.value || '').trim())) {
        const src = attr(child.value, 'src');
        if (src) {
          child.type = 'image';
          child.url = src;
          child.alt = attr(child.value, 'alt');
          child.title = attr(child.value, 'title') || null;
          child.data = { width: attr(child.value, 'width'), height: attr(child.value, 'height') };
          delete child.value;
        }
      }
      walk(child);
    }
  };
  walk(tree);
});

export const resizableImage = $nodeSchema('image', () => ({
  inline: true,
  group: 'inline',
  selectable: true,
  draggable: true,
  marks: '',
  atom: true,
  defining: true,
  isolating: true,
  attrs: {
    src: { default: '' },
    alt: { default: '' },
    title: { default: '' },
    width: { default: '' },
    height: { default: '' },
  },
  parseDOM: [{
    tag: 'img[src]',
    getAttrs: (dom) => ({
      src: dom.getAttribute('src') || '',
      alt: dom.getAttribute('alt') || '',
      title: dom.getAttribute('title') || '',
      width: dom.getAttribute('width') || '',
      height: dom.getAttribute('height') || '',
    }),
  }],
  toDOM: (node) => {
    const a = { src: node.attrs.src, alt: node.attrs.alt };
    if (node.attrs.title) a.title = node.attrs.title;
    if (node.attrs.width) a.width = node.attrs.width;
    if (node.attrs.height) a.height = node.attrs.height;
    return ['img', a];
  },
  parseMarkdown: {
    match: ({ type }) => type === 'image',
    runner: (state, node, type) => state.addNode(type, {
      src: node.url,
      alt: node.alt || '',
      title: node.title || '',
      width: node.data?.width || '',
      height: node.data?.height || '',
    }),
  },
  toMarkdown: {
    match: (node) => node.type.name === 'image',
    runner: (state, node) => {
      const { src, alt, title, width, height } = node.attrs;
      if (width || height) {
        const parts = [`src="${src}"`];
        if (alt) parts.push(`alt="${alt}"`);
        if (width) parts.push(`width="${width}"`);
        if (height) parts.push(`height="${height}"`);
        state.addNode('html', undefined, `<img ${parts.join(' ')} />`);
      } else {
        state.addNode('image', undefined, undefined, { title, url: src, alt });
      }
    },
  },
}));
