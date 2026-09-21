// What a diagram is drawn with: one of mermaid's own themes, and the document's text size.
// The size has to reach mermaid itself: it measures each label and draws a box to fit, so
// text styled to any other size spills out of its box. Pure, so tested without a layout
// engine (test/mermaid-look.test.mjs); mermaid.js keeps the state, main.js reads the page.

// Mermaid's built-ins. A theme names one in `--mdm-mermaid-theme`; anything else falls
// back, because that value comes out of a user's stylesheet and mermaid throws on a name
// it doesn't recognise, replacing every diagram in the document with an error box.
export const MERMAID_THEMES = ['default', 'dark', 'neutral', 'forest', 'base'];
export const DEFAULT_FONT_PX = 16;   // mermaid's own, and the document's (structure.css)

export const themeName = (name) => (MERMAID_THEMES.includes(name) ? name : 'default');

/** A size in px, from a number or a computed `Npx`; anything else is the default. */
export function fontPx(value) {
  const px = typeof value === 'number' ? value : Number(/^\s*(\d*\.?\d+)px\s*$/.exec(String(value ?? ''))?.[1]);
  return Number.isFinite(px) && px > 0 ? px : DEFAULT_FONT_PX;
}

/** --mdm-font-size as the editor sees it; registered as a length, it computes to px. */
export function documentFontPx(doc, win) {
  const el = doc.querySelector('.mdm-prosemirror') || doc.documentElement;
  return fontPx(win.getComputedStyle(el).getPropertyValue('--mdm-font-size'));
}

export const mermaidLook = (theme, size) => ({ theme: themeName(theme), fontPx: fontPx(size) });
export const sameLook = (a, b) => a.theme === b.theme && a.fontPx === b.fontPx;

/** `themeVariables.fontSize` is what mermaid sizes label text by, in every built-in theme;
 *  top-level `fontSize` only sizes image-only labels. The pie's own three sizes are left
 *  alone: they sit on geometry that does not grow with them. */
export const mermaidConfig = (look) => ({
  startOnLoad: false,
  theme: look.theme,
  securityLevel: 'strict',
  themeVariables: { fontSize: `${look.fontPx}px` },
});

/** A drawing is only ever served to the look it was drawn in. */
export const cacheKey = (look, source) => `${look.theme}|${look.fontPx}|${source}`;
