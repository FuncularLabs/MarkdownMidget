// Extracts the checkable text of the document as one plain string plus a segment
// map back to ProseMirror positions. The HOST does the actual spell checking
// (Windows Spell Checking API) — this is the editor's half of the round-trip,
// proven in the de-risk spike: positions round-trip exactly across headings,
// lists, tables, links, blockquotes, and mark-split words.
//
// Code is skipped structurally (code_block nodes and inlineCode marks), which is
// what makes "Skip Spell Check in Code" exact rather than heuristic. A "\n" gap
// is inserted between blocks and around skipped runs so the checker never sees
// two unrelated fragments glued into one fake word.

/**
 * @param {Node} doc ProseMirror document
 * @param {boolean} includeCode when true, code text is checked too
 * @returns {{plain: string, segs: {plainStart:number, pmPos:number, len:number}[]}}
 */
export function extractSpellText(doc, includeCode) {
  let plain = '';
  const segs = [];
  let needGap = false;

  const pushText = (text, pos) => {
    if (needGap) { plain += '\n'; needGap = false; }
    segs.push({ plainStart: plain.length, pmPos: pos, len: text.length });
    plain += text;
  };

  doc.descendants((node, pos) => {
    if (node.type.name === 'code_block') {
      if (!includeCode) { needGap = true; return false; }
      // Code block text is one text child; positions inside it are pos+1 based.
      node.forEach((child, offset) => {
        if (child.isText) { needGap = true; pushText(child.text, pos + 1 + offset); }
      });
      needGap = true;
      return false;
    }
    if (node.isBlock) {
      if (plain.length) needGap = true;
      return undefined;
    }
    if (node.isText) {
      const isCode = node.marks.some((m) => m.type.name === 'inlineCode');
      if (isCode && !includeCode) { needGap = true; return undefined; }
      pushText(node.text, pos);
    }
    return undefined;
  });

  return { plain, segs };
}
