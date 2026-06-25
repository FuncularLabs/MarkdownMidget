// Underline support. Markdown has no underline, so we round-trip it as inline
// HTML: the WYSIWYG mark serializes to <u>…</u>, and on parse we collapse the
// <u> … </u> inline-HTML node pair that remark produces back into the mark.

import { $markSchema, $command, $remark, $useKeymap } from '@milkdown/kit/utils';
import { commandsCtx } from '@milkdown/kit/core';
import { toggleMark } from '@milkdown/kit/prose/commands';

const U_OPEN = /^<u(\s[^>]*)?>$/i;
const U_CLOSE = /^<\/u>$/i;

// remark plugin: provides both the parse-side transform and the stringify handler.
export const remarkUnderline = $remark('remarkUnderline', () =>
  function underlinePlugin() {
    const data = this.data();
    const toMd = data.toMarkdownExtensions || (data.toMarkdownExtensions = []);
    toMd.push({
      handlers: {
        underline(node, _parent, state, info) {
          const exit = state.enter('underline');
          const value = state.containerPhrasing(node, { ...info, before: '>', after: '<' });
          exit();
          return `<u>${value}</u>`;
        },
      },
    });

    // mdast transform: wrap inline-HTML <u> … </u> pairs into one `underline` node.
    return (tree) => {
      const walk = (parent) => {
        const children = parent.children;
        if (!Array.isArray(children)) return;
        for (let i = 0; i < children.length; i++) {
          const node = children[i];
          if (node.type === 'html' && U_OPEN.test(node.value.trim())) {
            for (let j = i + 1; j < children.length; j++) {
              const end = children[j];
              if (end.type === 'html' && U_CLOSE.test(end.value.trim())) {
                const inner = children.slice(i + 1, j);
                children.splice(i, j - i + 1, { type: 'underline', children: inner });
                break;
              }
            }
          }
          walk(node);
        }
      };
      walk(tree);
    };
  });

export const underlineSchema = $markSchema('underline', () => ({
  parseDOM: [{ tag: 'u' }],
  toDOM: () => ['u', 0],
  parseMarkdown: {
    match: (node) => node.type === 'underline',
    runner: (state, node, markType) => {
      state.openMark(markType);
      state.next(node.children);
      state.closeMark(markType);
    },
  },
  toMarkdown: {
    match: (mark) => mark.type.name === 'underline',
    runner: (state, mark) => {
      state.withMark(mark, 'underline');
    },
  },
}));

export const toggleUnderlineCommand = $command(
  'ToggleUnderline',
  (ctx) => () => toggleMark(underlineSchema.type(ctx)));

export const underlineKeymap = $useKeymap('underlineKeymap', {
  ToggleUnderline: {
    shortcuts: 'Mod-u',
    command: (ctx) => {
      const commands = ctx.get(commandsCtx);
      return () => commands.call(toggleUnderlineCommand.key);
    },
  },
});

// Plugins to register, in dependency order.
export const underline = [
  remarkUnderline,
  underlineSchema,
  toggleUnderlineCommand,
  underlineKeymap,
].flat();
