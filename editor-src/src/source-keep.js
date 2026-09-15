// Save fidelity: the formatted view saves each top-level block it did not change as the text it was read from, and writes only the
// rest through the serialiser (test/source-keep.test.mjs). The baseline is the text a load installed, then the markdown each save
// wrote (line-map.js forgetLoad), with where each of its top-level blocks starts and ends and the line records inside it.
//
// An untouched document (the baseline's own, or an equal one: an edit undone) is its baseline's text, with nothing serialised.
// Otherwise the whole document is serialised as ever, and each block found in the baseline, by identity (ProseMirror shares a node
// no edit touched) or else as an equal node close ahead in order, keeps its baseline text. So does the text between two such
// blocks that were neighbours in the baseline: link definitions, extra blank lines. Every other join is the serialiser's, and a
// join's definitions whose own place is gone go after the block they followed (else before the next one that survives), once.
//
// Blocks are read in context, so a change is checked by parsing it again: each run of changed blocks and joins with one block
// either side, plus the document's definitions. Every block there must read as the node it stands for (a changed one: or as its
// own text alone reads, which is all a full serialisation promises). A window below the first block is read below a paragraph,
// as it stands in the document (a `---` rule at the top of a text is front matter). A window that does not is written by the serialiser from
// the neighbours out, at most twice more, and then the whole document is. A definition inside a block (in a quote, say) serves
// the whole document from there, so a document with one is written whole once anything changes.
const SEP = /\n+(?:<!---->\n+)?/y;   // what the serialiser writes between top-level blocks (mdast-util-to-markdown containerFlow)
const eols = (s) => { let n = 0; for (let i = s.indexOf('\n'); i >= 0; i = s.indexOf('\n', i + 1)) n++; return n; };
const groupsOf = (recs) => { const g = []; for (const r of recs) if (r.top) g.push([r]); else g.at(-1)?.push(r); return g; };
const tidy = (tops, end) => tops.every((t, i) => t.from >= (i ? tops[i - 1].to : 0) && t.to >= t.from && t.to <= end);

/** A load's baseline: `text`, and the records its parse found (lineCapture: `top` blocks with their offsets), paired with `doc`; null where they do not pair. */
export function baseFrom(doc, text, recs, linkDefs, nested) {
  if (!recs || text.startsWith('﻿')) return null;
  const tops = groupsOf(recs).map((g) => ({ from: g[0].from, to: g[0].to, line: g[0].line, recs: g })), last = doc.lastChild;
  if (doc.childCount === tops.length + 1 && last.type.name === 'paragraph' && !last.content.size) tops.push({ from: text.length, to: text.length, line: 0, recs: [] });   // settle.js
  return tops.length === doc.childCount && tidy(tops, text.length) ? { doc, markdown: text, recs, tops, linkDefs, nested } : null;
}

/** Where each of `n` top-level outputs lies in `md`, or null when they cannot be found in order. */
function spans(md, values, n) {
  if (values?.length !== n) return null;
  const at = [];
  for (let i = 0, p = 0; i < n; i++) {
    if (i) { SEP.lastIndex = p; if (!SEP.test(md)) return null; p = SEP.lastIndex; }
    if (!md.startsWith(values[i], p)) return null;
    at.push([p, p += values[i].length]);
  }
  return at;
}

/**
 * What `doc` saves as: { doc, markdown, recs (line records, as pair takes them), tops, linkDefs, nested }, a baseline for the next.
 * `serialize()` is the serialiser's { markdown, recs, tops (each top-level block's markdown) }; `parse` reads markdown; no `base`: serialised whole.
 */
export function render(doc, serialize, base, parse) {
  if (base && (doc === base.doc || doc.eq(base.doc))) return { ...base, doc, index: doc === base.doc ? base.index : undefined };
  const ser = serialize(), n = doc.childCount, sp = spans(ser.markdown, ser.tops, n), groups = groupsOf(ser.recs);
  const whole = { doc, markdown: ser.markdown, recs: ser.recs, linkDefs: [], nested: false,
    tops: sp && groups.length === n ? sp.map(([from, to], i) => ({ from, to, line: groups[i][0].line, recs: groups[i] })) : null };
  if (!base || base.nested || !whole.tops || !parse) return whole;

  const bt = base.tops, m = bt.length, src = [], used = new Set(), bmd = base.markdown, smd = ser.markdown;
  if (!base.index) { base.index = new Map(); base.doc.forEach((c, _, i) => base.index.has(c) || base.index.set(c, i)); }
  for (let i = 0, cursor = 0; i < n; i++) {   // the baseline block each block is, if any
    const c = doc.child(i);
    let j = base.index.get(c) ?? -1;
    if (j < 0 || used.has(j)) { j = -1; for (let k = cursor; j < 0 && k < Math.min(cursor + 4, m); k++) if (!used.has(k) && base.doc.child(k).eq(c)) j = k; }
    src.push(j);
    if (j >= 0) { used.add(j); cursor = j + 1; }
  }
  const where = new Map(src.map((j, i) => [j, i])), keep = src.map((j) => j >= 0);
  const bsep = (k) => bmd.slice(k < 0 ? 0 : bt[k].to, k + 1 < m ? bt[k + 1].from : bmd.length);   // k: -1 the lead, m - 1 the tail
  const ssep = (b) => smd.slice(b < 0 ? 0 : sp[b][1], b + 1 < n ? sp[b + 1][0] : smd.length);

  for (let tries = 0; tries < 3; tries++) {
    const texts = src.map((j, i) => (keep[i] ? bmd.slice(bt[j].from, bt[j].to) : smd.slice(...sp[i])));
    const verbatim = (b) => (b < 0 ? keep[0] && src[0] === 0 : b === n - 1 ? keep[b] && src[b] === m - 1 : keep[b] && keep[b + 1] && src[b + 1] === src[b] + 1);
    const extra = new Map(), usedSeps = new Set();
    for (let b = -1; b < n; b++) if (verbatim(b)) usedSeps.add(b < 0 ? -1 : src[b]);
    for (let k = -1; k < m; k++) {
      const text = usedSeps.has(k) ? '' : bsep(k).split('\n').filter((l) => /\S/.test(l)).join('\n');
      if (!text) continue;
      let next = k + 1;
      while (next < m && !where.has(next)) next++;
      const b = k < 0 ? -1 : where.has(k) ? where.get(k) : next < m ? where.get(next) - 1 : n - 1;
      extra.set(b, extra.has(b) ? `${extra.get(b)}\n${text}` : text);
    }
    const sepText = (b) => {
      const s = verbatim(b) ? bsep(b < 0 ? -1 : src[b]) : ssep(b), x = extra.get(b);
      return !x ? s : b === n - 1 ? `${s.replace(/\n*$/, '\n\n')}${x}\n` : `${s}${x}\n\n`;
    };
    const fns = texts.filter((_, i) => doc.child(i).type.name === 'footnote_definition'), defs = [...base.linkDefs, ...fns].join('\n\n');
    const need = (i) => !keep[i] || extra.has(i - 1) || extra.has(i) || !verbatim(i - 1) || !verbatim(i);
    const windows = [];
    for (let i = 0; i < n; i++) {
      if (!need(i)) continue;
      const a = Math.max(0, i - 1), z = Math.min(n - 1, i + 1), w = windows.at(-1);
      if (w && a <= w[1]) w[1] = Math.max(w[1], z); else windows.push([a, z]);
    }
    const reads = ([a, z]) => {
      let md = a ? '' : sepText(-1);
      for (let i = a; i <= z; i++) md += texts[i] + (i < z || z === n - 1 ? sepText(i) : '');
      const count = z - a + 1 - (z === n - 1 && !texts[z] ? 1 : 0);   // the trailing empty paragraph writes nothing
      const below = (i) => (i ? 1 : 0);   // text from below the first block is read below a paragraph: at the top of a text, `---` opens front matter
      try {
        const got = parse(`${'x\n\n'.repeat(below(a))}${md}\n\n${defs}`);
        return got.childCount === below(a) + count + fns.length && [...Array(count).keys()].every((k) => got.child(below(a) + k).eq(doc.child(a + k))
          || (!keep[a + k] && !!parse(`${'x\n\n'.repeat(below(a + k))}${texts[a + k]}\n\n${defs}`).maybeChild(below(a + k))?.eq(got.child(below(a) + k))));
      } catch { return false; }
    };
    const failed = windows.filter((w) => !reads(w));
    if (!failed.length) {
      let md = sepText(-1), line = 1 + eols(md);
      const tops = [], recs = [];
      for (let i = 0; i < n; i++) {
        const g = keep[i] ? bt[src[i]] : whole.tops[i], s = sepText(i), from = md.length, mine = g.recs.map((r) => ({ ...r, line: r.line - g.line + line }));
        for (const r of mine) recs.push(r);
        md += texts[i];
        tops.push({ from, to: md.length, line, recs: mine });
        md += s;
        line += eols(texts[i]) + eols(s);
      }
      return { doc, markdown: md, recs, tops, linkDefs: base.linkDefs, nested: false };
    }
    let widened = false;
    for (const [a, z] of failed) for (let i = a; i <= z; i++) if (keep[i]) { keep[i] = false; widened = true; }
    if (!widened) break;
  }
  return whole;
}
