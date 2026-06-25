// Markdown Midget — WYSIWYG editor surface.
// Built on Milkdown (ProseMirror). Markdown is the document model: the host
// pulls markdown with getMarkdown() and pushes it with setMarkdown(). The
// WordPad-style toolbar in the WPF shell drives formatting through cmd().

import { Editor, rootCtx, defaultValueCtx, editorViewOptionsCtx, commandsCtx } from '@milkdown/kit/core';
import { commonmark } from '@milkdown/kit/preset/commonmark';
import { gfm } from '@milkdown/kit/preset/gfm';
import { history } from '@milkdown/kit/plugin/history';
import { listener, listenerCtx } from '@milkdown/kit/plugin/listener';
import { callCommand, replaceAll, getMarkdown, insert, $useKeymap, $command } from '@milkdown/kit/utils';
import { nord } from '@milkdown/theme-nord';
import { prism, prismConfig } from '@milkdown/plugin-prism';
import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';

import {
  toggleStrongCommand,
  toggleEmphasisCommand,
  toggleInlineCodeCommand,
  wrapInHeadingCommand,
  wrapInBulletListCommand,
  wrapInOrderedListCommand,
  wrapInBlockquoteCommand,
  createCodeBlockCommand,
  turnIntoTextCommand,
  insertHrCommand,
} from '@milkdown/kit/preset/commonmark';
import { toggleStrikethroughCommand } from '@milkdown/kit/preset/gfm';
import { underline, toggleUnderlineCommand } from './underline.js';

// Syntax-highlighting languages for fenced code blocks.
import { refractor } from 'refractor';
import csharp from 'refractor/csharp';
import javascript from 'refractor/javascript';
import typescript from 'refractor/typescript';
import css from 'refractor/css';
import markup from 'refractor/markup'; // html / xml

import '@milkdown/kit/prose/view/style/prosemirror.css';
import '@milkdown/theme-nord/style.css';
import '../styles/editor.css';

[csharp, javascript, typescript, css, markup].forEach((l) => refractor.register(l));

// Show each link's URL as a native browser tooltip (title attribute), so links
// behave like they would in a browser. Pairs with the link styling in editor.css.
const linkTitle = $prose(() => new Plugin({
  key: new PluginKey('mdmLinkTitle'),
  props: {
    decorations(state) {
      const linkMark = state.schema.marks.link;
      if (!linkMark) return DecorationSet.empty;
      const decos = [];
      state.doc.descendants((node, pos) => {
        if (!node.isText) return;
        const mark = node.marks.find((m) => m.type === linkMark);
        if (mark) {
          const href = mark.attrs.href || '';
          decos.push(Decoration.inline(pos, pos + node.nodeSize, { title: href }));
        }
      });
      return DecorationSet.create(state.doc, decos);
    },
  },
}));

// Word-like: pressing Enter at the end of a heading starts a plain paragraph
// rather than continuing the heading. Returns false otherwise so the default
// Enter handling still applies elsewhere.
const splitHeadingCommand = $command('SplitHeadingToParagraph', () => () => (state, dispatch) => {
  const { $head, empty } = state.selection;
  if (!empty) return false;
  const heading = $head.parent;
  if (heading.type.name !== 'heading') return false;
  if ($head.parentOffset < heading.content.size) return false; // only at the end
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  if (dispatch) dispatch(state.tr.split($head.pos, 1, [{ type: paragraph }]).scrollIntoView());
  return true;
});

const headingEnterKeymap = $useKeymap('mdmHeadingEnterKeymap', {
  SplitHeadingToParagraph: {
    shortcuts: 'Enter',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(splitHeadingCommand.key); },
  },
});

// Headings + paragraph keymap: Ctrl+1..Ctrl+5 => H1..H5, Ctrl+0 => paragraph.
const headingKeymap = $useKeymap('mdmHeadingKeymap', {
  Paragraph: {
    shortcuts: 'Mod-0',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(turnIntoTextCommand.key); },
  },
  H1: { shortcuts: 'Mod-1', command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(wrapInHeadingCommand.key, 1); } },
  H2: { shortcuts: 'Mod-2', command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(wrapInHeadingCommand.key, 2); } },
  H3: { shortcuts: 'Mod-3', command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(wrapInHeadingCommand.key, 3); } },
  H4: { shortcuts: 'Mod-4', command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(wrapInHeadingCommand.key, 4); } },
  H5: { shortcuts: 'Mod-5', command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(wrapInHeadingCommand.key, 5); } },
});

// Map the host's logical command names to Milkdown command keys (+ optional payload).
const COMMANDS = {
  bold: () => callCommand(toggleStrongCommand.key),
  italic: () => callCommand(toggleEmphasisCommand.key),
  underline: () => callCommand(toggleUnderlineCommand.key),
  strike: () => callCommand(toggleStrikethroughCommand.key),
  code: () => callCommand(toggleInlineCodeCommand.key),
  paragraph: () => callCommand(turnIntoTextCommand.key),
  h1: () => callCommand(wrapInHeadingCommand.key, 1),
  h2: () => callCommand(wrapInHeadingCommand.key, 2),
  h3: () => callCommand(wrapInHeadingCommand.key, 3),
  h4: () => callCommand(wrapInHeadingCommand.key, 4),
  h5: () => callCommand(wrapInHeadingCommand.key, 5),
  h6: () => callCommand(wrapInHeadingCommand.key, 6),
  bullet: () => callCommand(wrapInBulletListCommand.key),
  ordered: () => callCommand(wrapInOrderedListCommand.key),
  quote: () => callCommand(wrapInBlockquoteCommand.key),
  hr: () => callCommand(insertHrCommand.key),
  codeblock: (lang) => callCommand(createCodeBlockCommand.key, lang || ''),
};

let editor = null;
let suppressChange = false;

function postToHost(message) {
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
    }
  } catch (_) {
    /* host bridge not present (e.g. running in a plain browser) */
  }
}

const MDM = {
  async create(initialMarkdown) {
    const root = document.getElementById('app');
    editor = await Editor.make()
      .config((ctx) => {
        ctx.set(rootCtx, root);
        ctx.set(defaultValueCtx, initialMarkdown || '');
        ctx.update(editorViewOptionsCtx, (prev) => ({
          ...prev,
          attributes: { class: 'mdm-prosemirror', spellcheck: 'true' },
        }));
        const l = ctx.get(listenerCtx);
        l.markdownUpdated(() => {
          if (suppressChange) return;
          postToHost({ type: 'change' });
        });
        // Report the block type at the cursor so the host can reflect it in the
        // Style dropdown/menu.
        l.selectionUpdated((_ctx, selection) => {
          const node = selection?.$head?.parent;
          let style = 'paragraph';
          if (node) {
            if (node.type.name === 'heading') style = 'h' + (node.attrs.level || 1);
            else style = node.type.name; // paragraph, blockquote, code_block, …
          }
          postToHost({ type: 'selection', style });
        });
      })
      .config(nord)
      .config((ctx) => {
        ctx.set(prismConfig.key, { configureRefractor: () => refractor });
      })
      .use(commonmark)
      .use(gfm)
      .use(history)
      .use(listener)
      .use(underline)
      .use(prism)
      .use(linkTitle)
      .use(splitHeadingCommand)
      .use(headingEnterKeymap)
      .use(headingKeymap)
      .create();

    postToHost({ type: 'ready' });
    return true;
  },

  getMarkdown() {
    if (!editor) return '';
    return editor.action(getMarkdown());
  },

  setMarkdown(md) {
    if (!editor) return;
    suppressChange = true;
    try {
      editor.action(replaceAll(md || ''));
    } finally {
      // markdownUpdated fires synchronously during the action above.
      suppressChange = false;
    }
  },

  cmd(name, ...args) {
    if (!editor) return false;
    const factory = COMMANDS[name];
    if (!factory) return false;
    editor.action(factory(...args));
    this.focus();
    return true;
  },

  // Insert a markdown fragment (e.g. a link or image) at the cursor.
  insertMarkdown(md) {
    if (!editor || !md) return false;
    editor.action(insert(md));
    this.focus();
    return true;
  },

  focus() {
    const view = document.querySelector('.mdm-prosemirror');
    if (view) view.focus();
  },
};

window.MDM = MDM;

// Tell the host the bridge is wired up; the host then calls MDM.create with the
// initial document. (Done from the host so file-open content arrives in one path.)
postToHost({ type: 'loaded' });
