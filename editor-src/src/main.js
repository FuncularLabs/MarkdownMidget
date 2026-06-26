// Markdown Midget — WYSIWYG editor surface.
// Built on Milkdown (ProseMirror). Markdown is the document model: the host
// pulls markdown with getMarkdown() and pushes it with setMarkdown(). The
// WordPad-style toolbar in the WPF shell drives formatting through cmd().

import { Editor, rootCtx, defaultValueCtx, editorViewOptionsCtx, commandsCtx, editorViewCtx } from '@milkdown/kit/core';
import { commonmark } from '@milkdown/kit/preset/commonmark';
import { gfm } from '@milkdown/kit/preset/gfm';
import { history } from '@milkdown/kit/plugin/history';
import { undo, redo } from '@milkdown/kit/prose/history';
import { listener, listenerCtx } from '@milkdown/kit/plugin/listener';
import { trailing } from '@milkdown/kit/plugin/trailing';
import { callCommand, replaceAll, getMarkdown, insert, $useKeymap, $command } from '@milkdown/kit/utils';
import { nord } from '@milkdown/theme-nord';
import { prism, prismConfig } from '@milkdown/plugin-prism';
import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey, TextSelection } from '@milkdown/kit/prose/state';
import { Decoration, DecorationSet } from '@milkdown/kit/prose/view';
import { formattingMarks } from './marks.js';
import { tableCellEditing, installTableContextMenu, insertTableAction } from './tables.js';

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
let editorView = null;
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

// Tell the host whether undo/redo are available so it can enable/disable buttons.
// Dry-running the same commands (no dispatch) reports applicability and avoids the
// undoDepth/redoDepth key mismatch between duplicate prosemirror-history copies.
function postHistory() {
  if (!editorView) return;
  postToHost({
    type: 'history',
    canUndo: undo(editorView.state),
    canRedo: redo(editorView.state),
  });
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
          // Defer a tick so view.state reflects the just-applied history step.
          setTimeout(postHistory, 0);
        });
        // Report the block type at the cursor so the host can reflect it in the
        // Style dropdown/menu.
        l.selectionUpdated((_ctx, selection) => {
          const node = selection?.$head?.parent;
          let style = 'paragraph';
          if (node) {
            if (node.type.name === 'heading') style = 'h' + (node.attrs.level || 1);
            else if (node.type.name === 'code_block') style = 'codeblock:' + (node.attrs.language || '');
            else style = node.type.name; // paragraph, blockquote, …
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
      .use(trailing)
      .use(formattingMarks)
      .use(tableCellEditing)
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

    editorView = editor.ctx.get(editorViewCtx);
    installTableContextMenu(editorView);
    postHistory();
    postToHost({ type: 'ready' });
    return true;
  },

  insertTable(rows, cols, header) {
    if (!editor) return;
    insertTableAction(editor, rows || 4, cols || 3, header !== false);
    this.focus();
  },

  getMarkdown() {
    if (!editor) return '';
    return editor.action(getMarkdown());
  },

  // flush=true rebuilds editor state, clearing undo history — used when loading a
  // document so undo can't reach back past the freshly opened/new content.
  setMarkdown(md, flush = true) {
    if (!editor) return;
    suppressChange = true;
    try {
      editor.action(replaceAll(md || '', flush));
    } finally {
      // markdownUpdated fires synchronously during the action above.
      suppressChange = false;
    }
    if (flush) editorView = editor.ctx.get(editorViewCtx); // state was recreated
    postHistory();
  },

  undo() { if (editorView) { undo(editorView.state, editorView.dispatch); this.focus(); } },
  redo() { if (editorView) { redo(editorView.state, editorView.dispatch); this.focus(); } },

  setSpellcheck(on) {
    if (editorView) editorView.dom.setAttribute('spellcheck', on ? 'true' : 'false');
  },

  // Read-only: ProseMirror stops accepting edits and the caret/handles disappear.
  setEditable(on) {
    if (editorView) editorView.setProps({ editable: () => !!on });
  },

  cmd(name, ...args) {
    if (!editor) return false;
    const factory = COMMANDS[name];
    if (!factory) return false;
    editor.action(factory(...args));
    this.focus();
    return true;
  },

  // Toggle Word-style formatting marks (¶ / ↵) on the editing surface.
  showMarks(on) {
    const root = document.querySelector('.mdm-prosemirror');
    if (root) root.classList.toggle('mdm-show-marks', !!on);
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
