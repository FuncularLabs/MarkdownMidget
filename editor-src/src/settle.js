// Settling a freshly installed document (#5 NF-5).
//
// Milkdown's `trailing` plugin appends an empty paragraph to a document that does
// not end in a paragraph or a heading — a list, a table, a code block. It does so
// from appendTransaction, which runs only when something dispatches, so straight
// after replaceAll the paragraph is not there yet: the FIRST transaction of any
// kind brings it into being. That transaction is then whatever the reader does
// next — a click, or Find landing on a match — and getMarkdown() suddenly returns
// a trailing blank line it did not return a moment ago.
//
// The host compares markdown Ordinal against the baseline it took right after
// loading, so that blank line reads as an edit: a * in the title bar, a crash
// backup, and a save prompt on close, for a document nobody touched.
//
// One no-op transaction here settles it while the document is being installed, so
// the baseline the host takes IS what the editor will hand back from then on. The
// transaction carries no steps of its own and is kept out of the history, so undo
// cannot reach back to "the document before its own trailing paragraph" and the
// Undo button stays grey on a freshly opened file.
export function settleDocument(view) {
  if (!view) return;
  view.dispatch(view.state.tr.setMeta('addToHistory', false));
}
