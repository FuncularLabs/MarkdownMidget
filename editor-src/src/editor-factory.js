// The editor itself: the plugin set and the Editor.make() chain, with nothing of
// the host in it. main.js builds the shipped editor through createEditor and
// wires the WebView2 bridge around it; test/jsdom-editor.mjs builds the same
// editor in jsdom so markdown can be round-tripped through the real schema under
// node --test. The two must not drift: a plugin added here reaches both.
//
// No CSS is imported here — the stylesheet entry is main.js's (see bundle.css),
// and Node cannot load it.

import { Editor, rootCtx, defaultValueCtx, editorViewOptionsCtx, commandsCtx } from '@milkdown/kit/core';
import { commonmark } from '@milkdown/kit/preset/commonmark';
import { gfm } from '@milkdown/kit/preset/gfm';
import { history } from '@milkdown/kit/plugin/history';
import { listener, listenerCtx } from '@milkdown/kit/plugin/listener';
import { trailing } from '@milkdown/kit/plugin/trailing';
import { $useKeymap, $command } from '@milkdown/kit/utils';
import { nord } from '@milkdown/theme-nord';
import { prism, prismConfig } from '@milkdown/plugin-prism';
import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey, TextSelection } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { formattingMarks } from './marks.js';
import { tableCellEditing } from './tables.js';
import { mermaidBlock } from './mermaid.js';
import { spellDecorate } from './spell-decorate.js';
import { htmlRender } from './html-render.js';
import { resizableImage, remarkImageSize } from './resizable-image.js';

import {
  wrapInHeadingCommand,
  turnIntoTextCommand,
} from '@milkdown/kit/preset/commonmark';
import { underline } from './underline.js';

// Syntax-highlighting languages for fenced code blocks.
import { refractor } from 'refractor';
import csharp from 'refractor/csharp';
import javascript from 'refractor/javascript';
import typescript from 'refractor/typescript';
import css from 'refractor/css';
import markup from 'refractor/markup'; // html / xml

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

// Ctrl+Enter inserts a plain paragraph after the current block and moves into it —
// the escape hatch out of a code block (which otherwise swallows Enter), including
// when the code block is the last thing in the document.
const exitBlockCommand = $command('ExitBlockToParagraph', () => () => (state, dispatch) => {
  const { $from } = state.selection;
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  const after = $from.after($from.depth);
  if (dispatch) {
    const tr = state.tr.insert(after, paragraph.createAndFill());
    tr.setSelection(TextSelection.create(tr.doc, after + 1)).scrollIntoView();
    dispatch(tr);
  }
  return true;
});

const exitBlockKeymap = $useKeymap('mdmExitBlockKeymap', {
  ExitBlockToParagraph: {
    shortcuts: 'Mod-Enter',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(exitBlockCommand.key); },
  },
});

// ArrowUp at the top line of a code block that is the FIRST block inserts a
// paragraph above it — the only keyboard way out when there's nothing to click.
const escapeUpCommand = $command('EscapeCodeBlockUp', () => () => (state, dispatch) => {
  const { $head, empty } = state.selection;
  if (!empty) return false;
  const node = $head.parent;
  if (node.type.name !== 'code_block') return false;
  if ($head.before($head.depth) !== 0) return false;        // not the first block
  if (node.textBetween(0, $head.parentOffset).includes('\n')) return false; // not first line
  const paragraph = state.schema.nodes.paragraph;
  if (!paragraph) return false;
  if (dispatch) {
    const tr = state.tr.insert(0, paragraph.createAndFill());
    tr.setSelection(TextSelection.create(tr.doc, 1)).scrollIntoView();
    dispatch(tr);
  }
  return true;
});

const escapeUpKeymap = $useKeymap('mdmEscapeUpKeymap', {
  EscapeCodeBlockUp: {
    shortcuts: 'ArrowUp',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(escapeUpCommand.key); },
  },
});

// Tab inserts a tab character (Shift+Tab removes a preceding one), except inside
// list items where Tab keeps its indent/outdent behavior.
const inList = ($pos) => {
  for (let d = $pos.depth; d > 0; d--) if ($pos.node(d).type.name === 'list_item') return true;
  return false;
};
const insertTabCommand = $command('InsertTab', () => () => (state, dispatch) => {
  if (inList(state.selection.$head)) return false;
  if (dispatch) dispatch(state.tr.insertText('\t').scrollIntoView());
  return true;
});
const removeTabCommand = $command('RemoveTab', () => () => (state, dispatch) => {
  const { $head, empty } = state.selection;
  if (!empty || inList($head)) return false;
  const pos = $head.pos;
  if (pos < 1 || state.doc.textBetween(pos - 1, pos) !== '\t') return false;
  if (dispatch) dispatch(state.tr.delete(pos - 1, pos).scrollIntoView());
  return true;
});

const tabKeymap = $useKeymap('mdmTabKeymap', {
  InsertTab: {
    shortcuts: 'Tab',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(insertTabCommand.key); },
  },
  RemoveTab: {
    shortcuts: 'Shift-Tab',
    command: (ctx) => { const c = ctx.get(commandsCtx); return () => c.call(removeTabCommand.key); },
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

// Reports the selection to the host (main.js's postSelectionState) whenever the
// answer can have changed: selection moved, storedMarks flipped, or the doc
// changed under the same selection - toggling bold over a range is a doc
// change, not a selection change. The report itself is the host's business,
// so it arrives as a callback.
const selectionState = (report) => $prose(() => new Plugin({
  key: new PluginKey('MDM_SELECTION_STATE'),
  view: () => ({
    update(view, prev) {
      const s = view.state;
      if (s.selection.eq(prev.selection) && s.storedMarks === prev.storedMarks && s.doc === prev.doc) return;
      report(s);
    },
  }),
}));

/**
 * Build the editor.
 *
 * @param {object} o
 * @param {Element} o.root               where the editor mounts
 * @param {string}  [o.initialMarkdown]  the first document
 * @param {() => void} [o.onMarkdownUpdated]  the listener's markdownUpdated hook
 * @param {(state) => void} [o.onSelectionState]  block style + marks at the cursor
 * @returns {Promise<Editor>}
 */
export function createEditor({ root, initialMarkdown = '', onMarkdownUpdated = () => {}, onSelectionState = () => {} }) {
  return Editor.make()
    .config((ctx) => {
      ctx.set(rootCtx, root);
      ctx.set(defaultValueCtx, initialMarkdown || '');
      ctx.update(editorViewOptionsCtx, (prev) => ({
        ...prev,
        attributes: { class: 'mdm-prosemirror', spellcheck: 'false' }, // native off — the app runs its own engine
      }));
      const l = ctx.get(listenerCtx);
      l.markdownUpdated(onMarkdownUpdated);
      // Block style + active marks at the cursor are reported by the
      // selectionState plugin below, NOT here: selectionUpdated never fires
      // for a storedMarks-only transaction (Ctrl+B at a collapsed caret), so
      // a listener-based report goes stale at exactly the moment Word lights
      // the Bold button.
    })
    .config(nord)
    .config((ctx) => {
      ctx.set(prismConfig.key, { configureRefractor: () => refractor });
    })
    .use(commonmark)
    .use(gfm)
    .use(remarkImageSize)
    .use(resizableImage)
    .use(htmlRender)
    .use(history)
    .use(listener)
    .use(underline)
    .use(prism)
    .use(linkTitle)
    .use(trailing)
    .use(formattingMarks)
    .use(tableCellEditing)
    .use(mermaidBlock)
    .use(spellDecorate)
    .use(selectionState(onSelectionState))
    .use(splitHeadingCommand)
    .use(headingEnterKeymap)
    .use(exitBlockCommand)
    .use(exitBlockKeymap)
    .use(escapeUpCommand)
    .use(escapeUpKeymap)
    .use(insertTabCommand)
    .use(removeTabCommand)
    .use(tabKeymap)
    .use(headingKeymap)
    .create();
}
