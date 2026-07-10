// Optionally exempt code from spell check by decorating code_block nodes and
// inlineCode ranges with spellcheck="false". The browser's spell checker honors
// the attribute (and it inherits), so prose keeps getting checked while code is
// skipped. Toggled at runtime by the host; survives ProseMirror re-renders because
// it's a decoration recomputed from plugin state.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';

export const CODE_SPELLCHECK_KEY = new PluginKey('mdmCodeSpellcheck');

// Held at module scope so the setting survives EditorState rebuilds (setMarkdown
// flushes and re-creates the plugin state; without this the toggle would reset to
// its default every time a document is loaded).
let currentSkip = true;

function build(doc, skip) {
  if (!skip) return DecorationSet.empty;
  const decos = [];
  doc.descendants((node, pos) => {
    if (node.type.name === 'code_block') {
      decos.push(Decoration.node(pos, pos + node.nodeSize, { spellcheck: 'false' }));
      return false; // don't descend into the code block's text
    }
    if (node.isText && node.marks.some((m) => m.type.name === 'inlineCode')) {
      decos.push(Decoration.inline(pos, pos + node.nodeSize, { spellcheck: 'false' }));
    }
    return undefined;
  });
  return DecorationSet.create(doc, decos);
}

export const codeSpellcheck = $prose(() => new Plugin({
  key: CODE_SPELLCHECK_KEY,
  state: {
    init(_, state) { return { skip: currentSkip, deco: build(state.doc, currentSkip) }; },
    apply(tr, value, _old, newState) {
      const meta = tr.getMeta(CODE_SPELLCHECK_KEY);
      const skip = meta && typeof meta.skip === 'boolean' ? meta.skip : value.skip;
      if (tr.docChanged || (meta && typeof meta.skip === 'boolean')) {
        return { skip, deco: build(newState.doc, skip) };
      }
      return { skip, deco: value.deco.map(tr.mapping, tr.doc) };
    },
  },
  props: {
    decorations(state) { return CODE_SPELLCHECK_KEY.getState(state).deco; },
  },
}));

export function setCodeSpellcheck(view, skip) {
  currentSkip = !!skip;
  if (view) view.dispatch(view.state.tr.setMeta(CODE_SPELLCHECK_KEY, { skip: currentSkip }));
}
