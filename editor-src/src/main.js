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
import { tableCellEditing, insertTableAction, runTableCommand, focusTableCell } from './tables.js';
import { mermaidBlock } from './mermaid.js';
import { codeSpellcheck, setCodeSpellcheck as setCodeSpell } from './code-spellcheck.js';
import { getScrollAnchor, restoreScrollAnchor } from './scroll-anchor.js';
import { spellDecorate, setSpellRanges, beginSpellCheck } from './spell-decorate.js';
import { htmlRender } from './html-render.js';
import { findReset as fReset, findNext as fNext, findPrev as fPrev, findClear as fClear } from './find.js';
import { resizableImage, remarkImageSize } from './resizable-image.js';
import { NodeSelection } from '@milkdown/kit/prose/state';

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

// Apply / remove print-only body classes precisely during print rendering.
// Fires for both ShowPrintUI and PrintToPdfAsync.
window.addEventListener('beforeprint', () => {
  const p = window.__mdmPrintPrefs || {};
  document.body.classList.toggle('mdm-print-source', !!p.sourceMode);
  document.body.classList.toggle('mdm-print-mono-code', p.colorCode === false);
});
window.addEventListener('afterprint', () => {
  document.body.classList.remove('mdm-print-source');
  document.body.classList.remove('mdm-print-mono-code');
});

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

// Editor context menus are native (WPF) in the host. Here we detect what was
// right-clicked (or where the Menu/Shift+F10 key fired) and ask the host to show
// the appropriate menu at that point. The host calls back via MDM.tableCmd /
// MDM.setImageSize / the Edit clipboard commands.

function imageInfo(img) {
  const natW = img.naturalWidth || img.width || 0;
  const natH = img.naturalHeight || img.height || 0;
  const curW = parseInt(img.getAttribute('width'), 10) || img.width || natW;
  const curH = parseInt(img.getAttribute('height'), 10) || img.height || natH;
  return { curW, curH, natW, natH };
}

function requestContextMenu(view, clientX, clientY, img) {
  if (img && img.tagName === 'IMG') {
    try {
      const pos = view.posAtDOM(img, 0);
      view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)));
    } catch (_) { /* edge */ }
    postToHost({ type: 'contextmenu', menu: 'image', x: clientX, y: clientY, ...imageInfo(img) });
    return;
  }
  if (focusTableCell(view, clientX, clientY)) {
    postToHost({ type: 'contextmenu', menu: 'table', x: clientX, y: clientY });
    return;
  }
  postToHost({ type: 'contextmenu', menu: 'text', x: clientX, y: clientY });
}

function installContextMenus(view) {
  view.dom.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    requestContextMenu(view, e.clientX, e.clientY, e.target);
  });
  // Context-menu / Shift+F10 key: anchor at the caret and pick the menu by selection.
  view.dom.addEventListener('keydown', (e) => {
    if (e.key !== 'ContextMenu' && !(e.shiftKey && e.key === 'F10')) return;
    e.preventDefault();
    const sel = view.state.selection;
    let x = 80, y = 80;
    try { const c = view.coordsAtPos(sel.head); x = c.left; y = c.bottom; } catch (_) { /* fallback */ }
    let img = null;
    if (sel.node && sel.node.type.name === 'image') {
      const dom = view.nodeDOM(sel.from);
      if (dom && dom.tagName === 'IMG') img = dom;
    }
    requestContextMenu(view, x, y, img);
  });
}

// Make the editor area a file drop target. The OS doesn't expose dropped-file
// paths to web content, so we read the text and hand the host the name + content.
function installFileDrop() {
  const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
  window.addEventListener('dragover', (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  }, true);
  window.addEventListener('drop', (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    const file = e.dataTransfer.files[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => postToHost({ type: 'fileDrop', name: file.name, content: String(reader.result) });
    reader.readAsText(file);
  }, true);
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
      .use(codeSpellcheck)
      .use(spellDecorate)
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
    installContextMenus(editorView);
    installFileDrop();
    postHistory();
    postToHost({ type: 'ready' });
    return true;
  },

  insertTable(rows, cols, header) {
    if (!editor) return;
    insertTableAction(editor, rows || 4, cols || 3, header !== false);
    this.focus();
  },

  tableCmd(name) {
    if (editorView) runTableCommand(editorView, name);
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

  // Resolve relative image/link paths against the open document's folder (mapped
  // by the host to a virtual host). This only changes how the browser resolves
  // relative URLs — the markdown model keeps the original relative paths, so
  // saving is unaffected. Pass null to clear (untitled / no folder context).
  setDocBase(url) {
    let base = document.querySelector('head > base');
    if (url) {
      if (!base) { base = document.createElement('base'); document.head.appendChild(base); }
      base.setAttribute('href', url);
    } else if (base) {
      base.remove();
    }
  },

  // Reading position as a topic, for surviving an external rewrite of the file.
  getScrollAnchor() { return getScrollAnchor(); },
  restoreScrollAnchor(a) { return restoreScrollAnchor(a); },

  // skip=true → code_block + inlineCode get spellcheck="false" (prose still checked).
  setCodeSpellcheck(skip) {
    if (editorView) setCodeSpell(editorView, skip);
  },

  // TEMP (spell-check spike): expose the live EditorView so the position-mapping
  // experiment can run against a real document. Revert before shipping.
  __view() { return editorView; },

  // TEMP (spike): host-supplied misspelling ranges (ProseMirror positions).
  __setSpellRanges(ranges) { if (editorView) setSpellRanges(editorView, ranges); },
  __beginSpellCheck() { beginSpellCheck(); },

  // Read-only: ProseMirror stops accepting edits and the caret/handles disappear.
  setEditable(on) {
    if (editorView) editorView.setProps({ editable: () => !!on });
  },

  // Stores the print prefs; they're applied to <body> on the standard browser
  // `beforeprint` event (and stripped on `afterprint`), so screen view stays
  // untouched and the timing is correct for both ShowPrintUI and PrintToPdf.
  setPrintMode(opts) {
    window.__mdmPrintPrefs = Object.assign(
      { sourceMode: false, colorCode: true, sourceText: '' }, opts || {});
    let pre = document.getElementById('mdm-print-source-pre');
    if (window.__mdmPrintPrefs.sourceMode) {
      if (!pre) {
        pre = document.createElement('pre');
        pre.id = 'mdm-print-source-pre';
        pre.className = 'mdm-print-source-pre';
        document.body.appendChild(pre);
      }
      pre.textContent = String(window.__mdmPrintPrefs.sourceText || '');
    } else if (pre) {
      pre.remove();
    }
  },

  // Document width: portrait (~A4), landscape (11/8 wider), or full window width.
  setPageWidth(mode) {
    const map = { portrait: '850px', landscape: '1169px', full: 'none' };
    document.documentElement.style.setProperty('--mdm-page-width', map[mode] || '850px');
  },

  // Find (WYSIWYG view). The host hands us a regex (source + flags) it built from
  // its FindEngine, so the four search modes stay consistent between views.
  findReset(source, flags) { return fReset(source, flags); },
  findNext(wrap) { return fNext(!!wrap); },
  findPrev(wrap) { return fPrev(!!wrap); },
  findClear() { fClear(); },

  // Apply width/height (px) to the currently selected image node.
  setImageSize(width, height) {
    if (!editorView) return;
    const { selection } = editorView.state;
    const node = selection.node;
    if (!node || node.type.name !== 'image') return;
    const attrs = { ...node.attrs, width: String(width), height: String(height) };
    editorView.dispatch(editorView.state.tr.setNodeMarkup(selection.from, undefined, attrs));
    this.focus();
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
