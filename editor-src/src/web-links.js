// A web or email link opens outside the app, once the host has asked (LinkOpening.cs, OpenLinkDialog). The gesture is
// anchor-links.js's: Ctrl+click where the view is editable, a plain click where it is read-only (the Help window); a `#fragment`
// link is anchor-links.js's. The href is the link mark's own, never a.href: that one is resolved against the document's
// <base href="https://mdm-doc.invalid/">, which dresses a relative path up as a web address. The host checks it again regardless.
import { $prose } from '@milkdown/kit/utils';
import { Plugin, PluginKey } from '@milkdown/kit/prose/state';

const post = (message) => { try { window.chrome?.webview?.postMessage(message); } catch { /* no host, or a dead WebView */ } };

/** The href of the link mark the clicked <a> renders, or null (a link written as raw HTML has no mark). */
function markHref(view, a) {
  try { return view.state.doc.nodeAt(view.posAtDOM(a, 0))?.marks.find((mk) => mk.type.name === 'link')?.attrs.href ?? null; }
  catch { return null; }
}

export const webLinks = $prose(() => new Plugin({
  key: new PluginKey('MDM_WEB_LINKS'),
  props: {
    handleDOMEvents: {
      click(view, event) {
        const a = event.target.closest?.('a[href]');
        if (!a || a.getAttribute('href').startsWith('#') || (view.editable && !event.ctrlKey)) return false;
        event.preventDefault();   // opened or refused, never a navigation: the host would cancel it and say nothing
        if (event.detail > 1) return true;   // the second click of a double-click: one prompt, not two
        const href = markHref(view, a);
        post(typeof href === 'string' && /^(https?:\/\/|mailto:)/i.test(href) ? { type: 'openLink', url: href } : { type: 'linkRefused' });
        return true;
      },
    },
  },
}));
