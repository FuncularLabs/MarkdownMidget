// Markdown Midget — WYSIWYG editor surface.
// Built on Milkdown (ProseMirror). Markdown is the document model: the host
// pulls markdown with getMarkdown() and pushes it with setMarkdown(). The
// WordPad-style toolbar in the WPF shell drives formatting through cmd().

import { editorViewCtx, serializerCtx, parserCtx } from '@milkdown/kit/core';
import { undo, redo } from '@milkdown/kit/prose/history';
import { callCommand, replaceAll, getMarkdown, insert } from '@milkdown/kit/utils';
import { insertTableAction, runTableCommand, focusTableCell } from './tables.js';
import { setMermaidTheme } from './mermaid.js';
import { getScrollAnchor, restoreScrollAnchor } from './scroll-anchor.js';
import { setSpellRanges, beginSpellCheck, misspellingAt } from './spell-decorate.js';
import { extractSpellText } from './spell-extract.js';
import { SEPARATOR } from './spell-separator.js';
import {
  findReset as fReset, findNext as fNext, findPrev as fPrev, findClear as fClear,
  findReplace as fReplace, findReplaceAll as fReplaceAll, findCaptureScope as fCaptureScope,
  findSelectionText as fSelectionText,
} from './find.js';
import { settleDocument } from './settle.js';
import { linkAt } from './link-at.js';
import { readHeads, readFull, planDroppedRead, postAnswer, postWithFiles } from './file-drop.js';
import { ceilingFrom, refusalMessage } from './picture-paste.js';
import { NodeSelection, Selection } from '@milkdown/kit/prose/state';
import { createEditor } from './editor-factory.js';
import {
  beginLoad, endLoad, forgetLoad, markdownWithLines, settledMarkdown, changedSinceLoad, ensureLines, lineStatus, lineTarget, pinLine, showLineNumbers,
} from './line-map.js';

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
import { toggleUnderlineCommand } from './underline.js';

// One entry, because the cascade order is decided in there — see bundle.css.
// Importing any of these files separately as well would emit part of the vendor
// CSS unlayered, with no warning.
import '../styles/bundle.css';

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
const serialize = (doc) => editor.action(doc ? (ctx) => ctx.get(serializerCtx)(doc) : getMarkdown());   // the live document's, or `doc`'s
const parse = (markdown) => editor.action((ctx) => ctx.get(parserCtx)(markdown));   // what markdown opens as: a save's check on the blocks it kept (source-keep.js)

// Returns whether the message actually went. Almost every caller ignores that —
// a 'change' or 'contextmenu' the host missed is not worth a second thought — but
// the drop handshake does not: the host BLOCKS on the droppedFileBytes message,
// and an oversized payload (about 85 MB of base64 at the 64 MB picture ceiling) is
// exactly the kind the bridge refuses. Swallowing that failure left the host
// waiting with nothing on screen to say why. See postAnswer in file-drop.js.
function postToHost(message) {
  try {
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage(message);
      return true;
    }
  } catch (_) {
    /* the bridge refused the message — an oversized payload, or a dead WebView */
  }
  return false;   // also the plain-browser case: there is no host to hear it
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

// The misspelling under the pointer, or null. `before` carries the text leading
// up to it so the host can tell a genuine misspelling from a context-only error
// (a repeated word) without a second round-trip.
function spellAt(view, clientX, clientY) {
  try {
    const hit = view.posAtCoords({ left: clientX, top: clientY });
    if (!hit) return null;
    const r = misspellingAt(view.state, hit.pos);
    if (!r) return null;
    return {
      from: r.from,
      to: r.to,
      word: view.state.doc.textBetween(r.from, r.to),
      before: view.state.doc.textBetween(Math.max(0, r.from - 60), r.from, ' '),
    };
  } catch (_) {
    return null;   // no spell info is fine
  }
}

function requestContextMenu(view, clientX, clientY, img) {
  // Spelling info rides along with WHATEVER menu the click warrants — it must not
  // pick the menu. Letting a misspelling win outright cost the table its
  // insert/delete/select commands entirely: a click in a cell's padding resolves
  // to the end of the cell's text, which is inside the range of a trailing
  // misspelling, so cells full of flagged words (product codes, surnames) had no
  // reachable table commands at all.
  const spell = spellAt(view, clientX, clientY);
  const link = linkAt(view, img);   // Copy Link rides along the same way; `img` is the click's target, or null from the keyboard
  if (img && img.tagName === 'IMG') {
    try {
      const pos = view.posAtDOM(img, 0);
      view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)));
    } catch (_) { /* edge */ }
    postToHost({ type: 'contextmenu', menu: 'image', x: clientX, y: clientY, link, ...imageInfo(img) });
    return;
  }
  if (focusTableCell(view, clientX, clientY)) {
    postToHost({ type: 'contextmenu', menu: 'table', x: clientX, y: clientY, spell, link });
    return;
  }
  postToHost({ type: 'contextmenu', menu: 'text', x: clientX, y: clientY, spell, link });
}

// Word-protocol test for "is this mark on at the current selection": a
// collapsed caret answers from what TYPING would produce (storedMarks first,
// so a just-pressed Ctrl+B lights the button before any text exists); a range
// is on only when EVERY text node in it carries the mark - a half-bold
// selection shows Bold off, exactly as Word does. A range containing no text
// at all (an image alone) falls back to the marks at the caret side.
function markActive(state, type) {
  if (!type) return false;
  const { empty, from, to, $head } = state.selection;
  if (empty) return !!type.isInSet(state.storedMarks || $head.marks());
  let sawText = false, all = true;
  state.doc.nodesBetween(from, to, (n) => {
    if (!all) return false;
    if (!n.isText) return;
    sawText = true;
    if (!type.isInSet(n.marks)) all = false;
  });
  return sawText ? all : !!type.isInSet($head.marks());
}

// Formatting feedback for the host toolbar: block style for the Style combo,
// mark states for the B/I/U/S toggles. The editor's selectionState plugin
// (editor-factory.js) calls this only when the answer can have changed
// (selection moved, storedMarks flipped, or the doc changed under the same
// selection - toggling bold over a range is a doc change, not a selection
// change); cmd() below calls it unconditionally.
function postSelectionState(state) {
  const node = state.selection.$head.parent;
  let style = 'paragraph';
  if (node.type.name === 'heading') style = 'h' + (node.attrs.level || 1);
  else if (node.type.name === 'code_block') style = 'codeblock:' + (node.attrs.language || '');
  else style = node.type.name; // paragraph, blockquote, …
  const m = state.schema.marks;
  postToHost({
    type: 'selection',
    style,
    marks: {
      bold: markActive(state, m.strong),
      italic: markActive(state, m.emphasis),
      underline: markActive(state, m.underline),
      strike: markActive(state, m.strike_through || m.strikethrough),
      code: markActive(state, m.inlineCode || m.inline_code),
    },
    ...lineStatus(state),   // the status bar's Ln/Col (#10); absent when the document has no numbering
  });
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

// ===== file drop =====
//
// Web content never sees a dropped file's path, so the File objects are posted to the
// host alongside phase one (postWithFiles) and the host opens a document by its path.
// A picture's bytes are still the editor's to read. It does so in two phases, and
// the reason is memory: reading every dropped file in full before the host has
// said it wants any of them turned an accidentally-dropped 200 MB video into a
// >260 MB base64 string, copied into a web message, decoded again on the host.
//
// Phase one posts only what routing needs — each file's name, size and first
// HEAD_BYTES bytes. The host runs DropRouting.Plan on that (the rules live there,
// never here) and then calls readDroppedFiles for the one or two files it chose;
// phase two reads those in full and posts them back. The File objects of the last
// drop are kept here in the meantime — a File is a handle, not its contents, so
// holding them costs nothing.
//
// `dropSeq` numbers the drops. It travels out with phase one, back in on the
// request, and out again with the answer, so a second drop landing while the first
// is still being read can't be answered with the wrong files.
let droppedFiles = [];
let dropSeq = 0;

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
    const files = Array.from(e.dataTransfer.files);
    if (files.length === 0) return;
    // Held before the heads are posted: the host's request can only arrive after
    // the message, and it must find this drop's files waiting.
    droppedFiles = files;
    const drop = ++dropSeq;
    readHeads(files).then((read) => postWithFiles(window.chrome?.webview, { type: 'fileDrop', drop, files: read }, files));
  }, true);
}

// ===== theme read-back =====
//
// The host has to follow the theme in two places the WebView can't reach: the WPF
// source-view TextBox, and mermaid. Both need to know what the CSS *resolved to*,
// which is a question only the engine can answer — so it is asked rather than
// guessed. Parsing the stylesheet in C# to find the colours breaks on var()
// indirection, multiple :root blocks, `html {}` instead of `:root`, a declaration
// inside @media, and on every colour syntax WPF's ColorConverter can't read, which
// is most of them.

/**
 * Flatten a CSS colour to opaque 8-bit sRGB.
 *
 * getComputedStyle is only half an answer: modern Chromium preserves the author's
 * colour space, so a theme written in `oklch()` or `color-mix()` comes back as
 * `oklch(...)` and the host is parsing colour syntax again. A 1×1 canvas is the
 * engine's own converter — whatever CSS accepts, it composites to four bytes.
 *
 * Painted over an opaque base rather than returned with its alpha, because the
 * caller is a TextBox background: a translucent page colour has to become the
 * colour you can actually see through it, and #fff is where the window starts.
 */
function flattenColor(value, base) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) return null;
  for (const layer of [base, value]) {
    if (!layer) continue;
    // fillStyle silently keeps its previous value when handed something it can't
    // parse, so it is reset to a known colour first — otherwise an unreadable value
    // paints with whatever the last one was.
    ctx.fillStyle = '#000000';
    ctx.fillStyle = layer;
    ctx.fillRect(0, 0, 1, 1);
  }
  const [r, g, b] = ctx.getImageData(0, 0, 1, 1).data;
  return { r, g, b };
}

/**
 * Read what a theme resolved to, in a given document.
 *
 * Pure: no side effects beyond a probe element that is removed before returning.
 * Used against the live page (the document theme) and against the isolated frame
 * (a source-view theme that must NOT touch the page) — same code, so the two views
 * can never disagree about how a colour is read.
 */
function probeTheme(doc, win) {
  // Inside the editing surface when there is one. A theme is free to set its
  // variables on `.mdm-prosemirror` rather than on `:root`, and a probe parked on
  // <body> would not inherit those.
  const host = doc.querySelector('.mdm-prosemirror') || doc.body || doc.documentElement;

  const probe = doc.createElement('div');
  probe.setAttribute('aria-hidden', 'true');
  // The extra colour properties are carriers: the source view's markdown highlighting
  // is coloured from the theme's page-level palette (heading/link/quote), and reading
  // each through an unrelated colour property that the engine resolves to rgb is the
  // same trick as background/color above — it sidesteps oklch()/color-mix() that WPF
  // cannot parse. See SourcePalette on the host, which consumes the `source` block.
  probe.style.cssText =
    'position:absolute;left:-9999px;top:0;width:0;height:0;pointer-events:none;' +
    'background:var(--mdm-page-bg);color:var(--mdm-text);border-color:var(--mdm-app-bg);' +
    'outline-color:var(--mdm-heading);text-decoration-color:var(--mdm-link);' +
    'column-rule-color:var(--mdm-quote-bar);caret-color:var(--mdm-quote-text)';
  host.appendChild(probe);

  let result = { background: null, foreground: null, mermaid: '', source: null };
  try {
    const cs = win.getComputedStyle(probe);
    // Layered the way the window is: app background over white, page over that,
    // text over the page.
    const app = flattenColor(cs.borderTopColor, '#ffffff');
    const appCss = app ? `rgb(${app.r},${app.g},${app.b})` : '#ffffff';
    const bg = flattenColor(cs.backgroundColor, appCss);
    const bgCss = bg ? `rgb(${bg.r},${bg.g},${bg.b})` : appCss;
    const fg = flattenColor(cs.color, bgCss);

    // Source-view palette: each role flattened over the page background so a
    // translucent accent resolves to what it actually looks like on the page. All
    // four must resolve or `source` stays null — the host fail-closes on a partial
    // palette exactly as it does for background/foreground.
    const heading = flattenColor(cs.outlineColor, bgCss);
    const link = flattenColor(cs.textDecorationColor, bgCss);
    const accent = flattenColor(cs.columnRuleColor, bgCss);
    const quote = flattenColor(cs.caretColor, bgCss);
    const source = (heading && link && accent && quote)
      ? { heading, link, accent, quote }
      : null;

    result = {
      background: bg,
      foreground: fg,
      mermaid: (cs.getPropertyValue('--mdm-mermaid-theme') || '').trim().toLowerCase(),
      source,
    };
  } catch {
    // Something in the flattening step threw. Fall through with the empty result
    // declared above: the host's ThemeReadBack.Parse rejects a null colour pair and
    // reverts the source view with a status message, which is the honest answer.
    // (For the live page the caller still runs the mermaid side effect below, so a
    // throw here cannot leave mermaid on a theme two switches out of date.)
  } finally {
    probe.remove();
  }
  return result;
}

/** The live page's theme, plus the mermaid side effect that belongs with it. */
function readThemeBack() {
  const result = probeTheme(document, window);
  // Mermaid follows the DOCUMENT theme — a dark editor around a bright white diagram
  // reads as broken. It redraws itself if the name changed, and runs even when the
  // colour read-back failed: an empty name isn't one of mermaid's own themes, so
  // setMermaidTheme falls back to 'default' rather than leaving mermaid stale.
  setMermaidTheme(editorView, result.mermaid);
  return result;
}

// ===== resolving a theme WITHOUT applying it to the page =====
//
// The source view may run a different theme from the document (View ▸ Theme with
// "Same theme for both views" off). Its colours still have to be resolved by the
// engine — a theme is free to write oklch() or color-mix() — but installing that
// theme into the page would recolour the document. So it is resolved in a hidden,
// same-origin iframe that carries a copy of the bundle's stylesheets (the layer
// order and the default palette) but NOT the document's own theme element; the
// source theme is installed there, into the same top layer it would occupy on the
// page, and probed with the same code. The page never changes.
//
// about:blank inherits the page's origin and CSP; the CSP's frame-src 'none' does
// not block a srcless frame (verified in Chromium). Created once, on first use.
let isolatedFrame = null;

function ensureIsolatedFrame() {
  if (isolatedFrame && isolatedFrame.contentDocument) return isolatedFrame;
  const frame = document.createElement('iframe');
  frame.setAttribute('aria-hidden', 'true');
  frame.setAttribute('tabindex', '-1');
  frame.style.cssText = 'position:absolute;left:-9999px;top:0;width:0;height:0;border:0;';
  document.body.appendChild(frame);
  const doc = frame.contentDocument;

  // The bundle's own rules — every sheet except the theme element. Serialising via
  // cssRules keeps @layer statements and blocks intact, so the copied defaults sit
  // in mdm-base and a theme installed into mdm-theme outranks them exactly as on
  // the page.
  let base = '';
  for (const sheet of document.styleSheets) {
    if (sheet.ownerNode && sheet.ownerNode.id === 'mdm-theme') continue;
    try { for (const rule of sheet.cssRules) base += rule.cssText + '\n'; } catch { /* cross-origin: none of ours */ }
  }
  const baseStyle = doc.createElement('style');
  baseStyle.textContent = base;
  doc.head.appendChild(baseStyle);

  const host = doc.createElement('div');
  host.className = 'mdm-prosemirror';
  doc.body.appendChild(host);

  const theme = doc.createElement('style');
  theme.id = 'mdm-theme';
  doc.head.appendChild(theme);

  isolatedFrame = frame;
  return frame;
}

/** Resolve a theme's colours as they would apply, without touching the page. */
function resolveThemeIsolated(css) {
  const frame = ensureIsolatedFrame();
  const doc = frame.contentDocument;
  const el = doc.getElementById('mdm-theme');
  // textContent, never innerHTML — same reason as setTheme.
  el.textContent = css ? '@layer mdm-theme {\n' + css + '\n}\n' : '';
  return probeTheme(doc, frame.contentWindow);
}

// Opt-in timings (MDM.timing, MDM_TIMING=1 on the host): [phase, ms] notes, posted in one message two frames later, painted.
let timings = null, paintFrom = 0;
function note(phase, from) {
  if (!timings || timings.push([phase, Math.round(performance.now() - from)]) > 1) return;
  requestAnimationFrame(() => requestAnimationFrame(() => {
    if (paintFrom) timings.push(['painted', Math.round(performance.now() - paintFrom)]);   // from the setMarkdown's start
    paintFrom = 0; postToHost({ type: 'timing', lines: timings.splice(0) });
  }));
}

const MDM = {
  async create(initialMarkdown, options) {
    const root = document.getElementById('app');
    beginLoad();
    editor = await createEditor({
      root,
      initialMarkdown,
      onMarkdownUpdated: () => {
        if (suppressChange) return;
        postToHost({ type: 'change' });
        // Defer a tick so view.state reflects the just-applied history step.
        setTimeout(postHistory, 0);
      },
      onSelectionState: postSelectionState,
      // The host's picture ceiling (PictureLimit.EditorOptionsJson), applied here
      // to a paste — the one route a picture takes that the host never sees
      // (picture-paste.js). A refusal is reported so the host can say why nothing
      // went in, in the words its other routes use.
      maxPictureBytes: ceilingFrom(options),
      onPictureRefused: (size) => postToHost(refusalMessage(size)),
    });

    editorView = editor.ctx.get(editorViewCtx);
    // Same reason as setMarkdown: an initial document that does not end in a
    // paragraph gains the trailing plugin's empty one here rather than on the
    // reader's first click (settle.js). Suppressed, because installing a document
    // is not a change to report. The host opens with an empty document today, so
    // this is the other door rather than the one the bug came through.
    suppressChange = true;
    try { settleDocument(editorView); } finally { suppressChange = false; }
    endLoad(editorView.state.doc, initialMarkdown || '');
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

  getMarkdown(settledOnly) {
    if (!editor) return '';
    const t0 = performance.now();
    const { markdown, rebuilt } = (settledOnly ? settledMarkdown : markdownWithLines)(editorView.state.doc, serialize, parse);   // Save, Save As, backups, Ctrl+E
    if (rebuilt) postSelectionState(editorView.state);   // the status bar's line catches up (#10)
    note(rebuilt ? 'getMarkdown.rebuilt' : 'getMarkdown', t0);
    return markdown;
  },
  getSettledMarkdown() { return this.getMarkdown(true); },   // the document as setMarkdown installed it: the host's clean baseline as text, read only where text is compared (Ctrl+E, an external change)
  changedSinceLoad() { return !editorView || changedSinceLoad(editorView.state.doc); },   // false: node for node what setMarkdown installed, so clean against that load's baseline, with nothing serialised
  timing(on) { timings = on ? [] : null; },   // the host's MDM_TIMING=1 (TimingLog.cs)

  // Go to Line (#10): the host asks how many lines there are, then goes to one.
  lineCount() { return (editorView && ensureLines(editorView.state.doc, serialize, parse)?.lines) || 0; },
  goToLine(n) {
    if (!editorView || !ensureLines(editorView.state.doc, serialize, parse)) return false;
    const { state } = editorView;
    editorView.dispatch(state.tr.setSelection(Selection.near(state.doc.resolve(lineTarget(state.doc, n)))).scrollIntoView());
    pinLine(editorView.state, n); postSelectionState(editorView.state);   // a blank line, a fence or a rule reads as the line asked for
    return this.focus();
  },
  // A save made the file the saved markdown, so number by that from now on.
  lineBaseSaved() { forgetLoad(); this.getMarkdown(); },
  reportSelection() { if (editorView) postSelectionState(editorView.state); },
  setLineNumbers(on) { showLineNumbers(on); },   // View ▸ Line Numbers (#10)

  /**
   * Phase two of a file drop: the full bytes of the files the host's plan chose.
   *
   * `drop` is the counter that came out with the fileDrop message; `indices` are
   * positions in that message's file list. The answer goes back as the
   * droppedFileBytes message rather than as this function's return value, because
   * ExecuteScriptAsync does not await a promise — it would serialise this one as
   * {} and the host would be handed nothing.
   *
   * It always ANSWERS, whatever happens: a stale drop, an index that isn't in the
   * drop, a read that fails, or a throw on the way. What it cannot promise is that
   * the answer arrives — the bridge can refuse a message, and an answer carrying a
   * picture at the host's ceiling is about 85 MB of base64. postAnswer follows a
   * refused payload with a small message saying so; if that is refused too the
   * bridge is gone, and the host's own timeout is what ends the wait.
   */
  readDroppedFiles(drop, indices) {
    const answer = (files) => postAnswer(postToHost, drop, files);
    // Which drop this is about, and what it really asks for: planDroppedRead, which
    // is where that decision is tested (file-drop.js). A stale request — the user
    // dropped again while the host was routing — answers null for every index it
    // named rather than reading whatever is at that position in the NEW drop.
    const { stale, indices: wanted } = planDroppedRead(dropSeq, drop, indices);
    const allNull = () => wanted.map((index) => ({ index, base64: null }));
    if (stale) return Promise.resolve(answer(allNull()));
    return readFull(droppedFiles, wanted)
      .then(answer)
      .catch(() => answer(allNull()));
  },

  // flush=true rebuilds editor state, clearing undo history — used when loading a
  // document so undo can't reach back past the freshly opened/new content.
  setMarkdown(md, flush = true, load = 0) {   // load: the host's number for this install, echoed in its painted
    if (!editor) return;
    const t0 = performance.now(); if (timings) paintFrom = t0;
    suppressChange = true;
    try {
      beginLoad();   // number it by this text while it is untouched (#10)
      // A rebuilt view resets its element's scrollTop, a write that lays the whole new document out before it paints; #app is what scrolls.
      Object.defineProperty(editorView.dom, 'scrollTop', { get: () => 0, set() {}, configurable: true });
      try { editor.action(replaceAll(md || '', flush)); } finally { delete editorView.dom.scrollTop; }
      note('setMarkdown.parse', t0);
      if (flush) editorView = editor.ctx.get(editorViewCtx); // state was recreated
      // Inside the suppressed window on purpose: settling is part of installing the
      // document, not a change to report. See settle.js — without it the trailing
      // plugin's empty paragraph arrives on whatever the reader does first, and the
      // host, whose clean baseline was taken a moment earlier, calls the document
      // modified (#5 NF-5).
      settleDocument(editorView);
      endLoad(editorView.state.doc, md || '');
    } finally {
      // markdownUpdated fires synchronously during the action above.
      suppressChange = false;
    }
    postHistory();
    if (editorView) postSelectionState(editorView.state);   // the new document's Ln/Col (#10)
    note('setMarkdown', t0);
    // the host's install waits for this, and only this install's; a hidden page does not paint: say so now
    if (document.hidden) postToHost({ type: 'painted', load }); else this.paintedAfterFrame(load);
  },

  // 'painted' for load once the next frame is on screen. A hidden page draws no frame, so this one waits until it is shown:
  // the host's switch from the source view asks for it just after it shows this view.
  paintedAfterFrame(load) { requestAnimationFrame(() => setTimeout(() => postToHost({ type: 'painted', load }))); },

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


  // ---- Spell check (host-driven; see spell-extract.js / spell-decorate.js) ----

  // Call before extracting text for a check: starts recording edits so a slow
  // host response can be rebased instead of landing on stale positions.
  spellBegin() { beginSpellCheck(); },

  // The checkable text + segment map (JSON string), or null when not ready.
  // Starts the staleness recording itself, so no edit can slip between "begin"
  // and the snapshot the host actually checks.
  getSpellText(includeCode) {
    if (!editorView) return null;
    beginSpellCheck();
    return JSON.stringify(extractSpellText(editorView.state.doc, !!includeCode));
  },

  // Fresh check results as [{from,to}] ProseMirror ranges.
  setSpellRanges(ranges) {
    if (editorView) setSpellRanges(editorView, ranges || []);
  },

  // Delete a repeated word together with the separator in front of it. The gap
  // must be measured in DOCUMENT POSITIONS here, not in characters: an inline
  // leaf (image, inline HTML) occupies a position but contributes no text, so a
  // character count taken from textBetween drifts from the real positions and the
  // deletion silently refuses to apply.
  deleteRepeated(from, to, word) {
    if (!editorView) return false;
    const { state } = editorView;
    if (!(from >= 0 && to > from && to <= state.doc.content.size)) return false;
    if (state.doc.textBetween(from, to) !== word) return false;   // moved/edited since
    let start = from;
    while (start > 0) {
      const ch = state.doc.textBetween(start - 1, start);
      // A leaf yields '' for its position — stop rather than swallow it.
      if (ch.length !== 1 || !SEPARATOR.test(ch)) break;
      start--;
    }
    if (start === from) return false;   // nothing separating the two occurrences
    editorView.dispatch(state.tr.delete(start, to).scrollIntoView());
    editorView.focus();
    return true;
  },

  // Replace one range (a misspelled word) with new text — used by the host's
  // suggestion menu. The document may have changed between right-click and the
  // click on a suggestion (typing, auto-reload, a new file), so the replacement
  // only proceeds if the range still holds the exact word the menu was built
  // for; otherwise it's a silent no-op and the next re-check refreshes things.
  replaceRange(from, to, text, expected) {
    if (!editorView) return false;
    const size = editorView.state.doc.content.size;
    if (!(from >= 0 && to > from && to <= size)) return false;
    if (typeof expected === 'string' &&
        editorView.state.doc.textBetween(from, to) !== expected) return false;
    editorView.dispatch(editorView.state.tr.insertText(text, from, to).scrollIntoView());
    editorView.focus();
    return true;
  },

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

  // Install a theme, and tell the host what it actually resolved to.
  //
  // The CSS goes in verbatim, wrapped in `@layer mdm-theme` — the last layer in the
  // order declared by bundle.css, so a theme outranks everything for a normal
  // declaration and loses to the app's own `!important` rules for the handful of
  // things (squiggles, formatting marks, the resize handle) it must not be able to
  // remove. Empty means "the palette already in the bundle", which is the Default
  // entry and is what an unreadable or refused file falls back to.
  //
  // textContent, never innerHTML: a <style> element's text is handed to the CSS
  // parser, so a `</style>` in the middle of it is a parse error and not a way out.
  // Resolve a theme's colours for the SOURCE view without applying it to the page.
  // Same shape as setTheme's return, minus the mermaid side effect (mermaid follows
  // the document theme, not the source view's).
  resolveTheme(css) {
    return resolveThemeIsolated(css);
  },

  setTheme(css) {
    let el = document.getElementById('mdm-theme');
    if (!el) {
      el = document.createElement('style');
      el.id = 'mdm-theme';
      document.head.appendChild(el);
    }
    el.textContent = css ? '@layer mdm-theme {\n' + css + '\n}\n' : '';
    return readThemeBack();
  },

  // Document width: portrait (~A4), landscape (11/8 wider), or full window width.
  setPageWidth(mode) {
    const map = { portrait: '850px', landscape: '1169px', full: 'none' };
    document.documentElement.style.setProperty('--mdm-page-width', map[mode] || '850px');
  },

  // Find (WYSIWYG view). The host hands us a regex (source + flags) it built from
  // its FindEngine, so the four search modes stay consistent between views.
  findReset(source, flags) { return fReset(source, flags, editorView); },
  findNext(wrap) { return fNext(!!wrap); },
  findPrev(wrap) { return fPrev(!!wrap); },
  findClear() { fClear(); },
  // Replace (#5): the host hands over the replacement prepared for the search
  // mode and says whether it is literal text or a Regex-mode template.
  findReplace(replacement, literal, wrap) { return fReplace(editorView, replacement || '', !!literal, !!wrap); },
  findReplaceAll(replacement, literal) { return fReplaceAll(editorView, replacement || '', !!literal); },
  findCaptureScope() { return fCaptureScope(editorView); },
  findSelectionText(limit) { return fSelectionText(editorView, limit); },   // Find what from the selection

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
    // Repost formatting state even when the command was a no-op (marks are
    // disallowed in a code block, say): a refused command produces no
    // transaction, the selectionState plugin stays silent, and the host's
    // speculative ToggleButton flip would otherwise stick until the caret
    // moves. The unconditional answer confirms or reverts it either way.
    if (editorView) postSelectionState(editorView.state);
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

  // Focus through the EditorView when we have it: that restores/places the
  // ProseMirror selection as well as focusing the DOM, so a blank document gets a
  // real caret rather than just keyboard focus on the element.
  focus() {
    if (editorView) {
      editorView.focus();
      return true;
    }
    const dom = document.querySelector('.mdm-prosemirror');
    if (dom) { dom.focus(); return true; }
    return false;
  },
};

window.MDM = MDM;

// Tell the host the bridge is wired up; the host then calls MDM.create with the
// initial document. (Done from the host so file-open content arrives in one path.)
postToHost({ type: 'loaded' });
