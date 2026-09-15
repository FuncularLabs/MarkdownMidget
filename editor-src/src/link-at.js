// Copy Link on the host's context menu: the href of the link under a right-click, or at the caret when the Menu key or
// Shift+F10 opened the menu. Read from the link mark, as the markdown writes it, never from a.href: that is resolved
// against the page's <base href="https://mdm-doc.invalid/">, which turns a relative link into a web address.
export function linkAt(view, target) {
  try {
    const a = target?.closest?.('a');
    if (target && !a) return undefined;   // a click off any link says nothing, whatever the caret is on
    const pos = a ? view.posAtDOM(a, 0) : view.state.selection.head, $pos = view.state.doc.resolve(pos);
    if (a && !a.contains(view.nodeDOM(pos))) return undefined;   // an <a> written as raw HTML: no link mark drew it, and its neighbour's must not answer
    const link = view.state.schema.marks.link, marksOf = (node) => node?.marks ?? [];
    return (link.isInSet(marksOf($pos.nodeAfter)) ?? link.isInSet(marksOf($pos.nodeBefore)))?.attrs.href || undefined;
  } catch {
    return undefined;   // an <a> ProseMirror can't place (raw HTML) has no mark to read
  }
}
