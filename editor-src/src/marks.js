// Word-style formatting marks: a pilcrow (¶) at the end of every paragraph/heading
// and a return arrow (↵) at each manual line break. The widgets are always present
// but hidden by CSS unless the editor root carries the `mdm-show-marks` class, which
// the host toggles via MDM.showMarks(). Kept light gray to stay unobtrusive.

import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';

function mark(ch, kind) {
  return () => {
    const span = document.createElement('span');
    span.className = `mdm-mark mdm-mark-${kind}`;
    span.textContent = ch;
    span.setAttribute('contenteditable', 'false');
    return span;
  };
}

// Drawn by one function per kind and keyed, so ProseMirror keeps the spans already drawn instead of drawing every one again
// on each transaction (widgets of a kind are interchangeable, as a key requires); and built once per document.
const WIDGETS = { break: mark('↵', 'break'), tab: mark('→', 'tab'), para: mark('¶', 'para') };
const spec = (kind, side) => ({ side, ignoreSelection: true, key: `mdm-mark-${kind}` });
const built = new WeakMap();

export const formattingMarks = $prose(() => new Plugin({
  key: new PluginKey('mdmFormattingMarks'),
  props: {
    decorations(state) {
      if (built.has(state.doc)) return built.get(state.doc);   // a selection or an empty transaction keeps its document
      const decos = [];
      state.doc.descendants((node, pos) => {
        if (node.type.name === 'hardbreak') {
          decos.push(Decoration.widget(pos, WIDGETS.break, spec('break', -1)));
        } else if (node.isText && node.text && node.text.includes('\t')) {
          // A tab arrow (→) for each tab character.
          for (let i = node.text.indexOf('\t'); i !== -1; i = node.text.indexOf('\t', i + 1)) {
            decos.push(Decoration.widget(pos + i, WIDGETS.tab, spec('tab', -1)));
          }
        } else if (node.isTextblock &&
                   (node.type.name === 'paragraph' || node.type.name === 'heading')) {
          const end = pos + node.nodeSize - 1; // just inside the block's close
          decos.push(Decoration.widget(end, WIDGETS.para, spec('para', 1)));
        }
      });
      built.set(state.doc, DecorationSet.create(state.doc, decos));
      return built.get(state.doc);
    },
  },
}));
