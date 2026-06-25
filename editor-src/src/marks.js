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

export const formattingMarks = $prose(() => new Plugin({
  key: new PluginKey('mdmFormattingMarks'),
  props: {
    decorations(state) {
      const decos = [];
      state.doc.descendants((node, pos) => {
        if (node.type.name === 'hardbreak') {
          decos.push(Decoration.widget(pos, mark('↵', 'break'),
            { side: -1, ignoreSelection: true }));
        } else if (node.isText && node.text && node.text.includes('\t')) {
          // A tab arrow (→) for each tab character.
          for (let i = node.text.indexOf('\t'); i !== -1; i = node.text.indexOf('\t', i + 1)) {
            decos.push(Decoration.widget(pos + i, mark('→', 'tab'),
              { side: -1, ignoreSelection: true }));
          }
        } else if (node.isTextblock &&
                   (node.type.name === 'paragraph' || node.type.name === 'heading')) {
          const end = pos + node.nodeSize - 1; // just inside the block's close
          decos.push(Decoration.widget(end, mark('¶', 'para'),
            { side: 1, ignoreSelection: true }));
        }
      });
      return DecorationSet.create(state.doc, decos);
    },
  },
}));
