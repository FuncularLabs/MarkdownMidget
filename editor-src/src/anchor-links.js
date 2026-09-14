// A `#fragment` link jumps to its heading in this document. Left to the browser it resolves against the document's <base>
// (https://mdm-doc.invalid/) and the host cancels it as an off-site navigation. The gesture is the one a link already
// answers to: where the view is read-only (the Help window) links are live, so a plain click; where it is editable,
// Chromium never follows a link and a click only places the caret, so Ctrl+click. The headings are read at the click.
import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey, TextSelection } from '@milkdown/kit/prose/state';

/** GitHub's anchor for a heading's text, as DocAnchorLinksTests.Slug makes it: lowercased, spaces to hyphens, and
 *  every other character that is not a letter, digit, `-` or `_` dropped. */
export const slug = (text) => text.trim().toLowerCase().replace(/[^\p{L}\p{Nd}_\- ]/gu, '').replace(/ /g, '-');

/** The position of the heading `fragment` names, or null. A repeated slug is `-1`, `-2` after the first (AnchorsOf). */
export function headingFor(doc, fragment) {
  let want = fragment, found = null;
  const seen = new Map(); try { want = decodeURIComponent(fragment); } catch { /* a stray `%`: match it as written */ }
  doc.descendants((node, pos) => {
    if (found !== null || node.type.name !== 'heading') return found === null && !node.isTextblock;
    const s = slug(node.textContent), n = seen.get(s) ?? 0;
    if (s) { seen.set(s, n + 1); if ((n ? `${s}-${n}` : s) === want) found = pos; }
    return false;
  });
  return found;
}

export const anchorLinks = $prose(() => new Plugin({
  key: new PluginKey('MDM_ANCHOR_LINKS'),
  props: {
    handleDOMEvents: {
      click(view, event) {
        const link = event.target.closest?.('a[href^="#"]');
        if (!link || (view.editable && !event.ctrlKey)) return false;
        event.preventDefault();   // matched or not: an unmatched fragment does nothing, not a navigation the host refuses
        const pos = headingFor(view.state.doc, link.getAttribute('href').slice(1));
        if (pos === null) return true;
        view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, pos + 1)));   // Ln/Col follows
        const app = view.dom.closest('#app'), heading = view.nodeDOM(pos);   // #app is what scrolls (structure.css)
        if (app && heading) app.scrollTop += heading.getBoundingClientRect().top - app.getBoundingClientRect().top - 8;
        return true;
      },
    },
  },
}));
