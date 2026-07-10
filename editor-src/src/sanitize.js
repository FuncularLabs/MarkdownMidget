// DOMPurify configuration for rendering embedded raw HTML (see html-render.js).
// Kept in its own module — with no Milkdown/ProseMirror imports — so the exact
// filtering policy can be unit-tested in a bare Node + jsdom context.

export const SANITIZE_OPTS = {
  // Keep a few presentational attributes DOMPurify drops by default.
  ADD_ATTR: ['align', 'target', 'width', 'height', 'valign', 'colspan', 'rowspan'],
  FORBID_TAGS: ['script', 'style', 'iframe', 'form', 'object', 'embed', 'link', 'meta', 'base'],
  FORBID_ATTR: ['srcset'],
};
