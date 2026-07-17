// Reading-position anchoring for the WYSIWYG view, used when a watched file is
// rewritten underneath the reader (see ScrollAnchor.cs for the source-view twin).
//
// The anchor is a topic, not a scroll offset: a regenerated document shifts every
// pixel and line, but its headings usually survive. So we remember the heading we
// were under, refine with the exact line when it's still there, and fall back to a
// proportional position only when nothing recognizable remains.

const FINGERPRINT_MAX = 80;
const BLOCK_SEL = 'p, li, td, th, blockquote, pre, h1, h2, h3, h4, h5, h6';
const HEADING_SEL = 'h1, h2, h3, h4, h5, h6';

const norm = (s) => (s || '').trim().replace(/\s+/g, ' ').toLowerCase().slice(0, FINGERPRINT_MAX);

function root() {
  return document.querySelector('.mdm-prosemirror') || document.body;
}

// The editor's scroll container isn't fixed by the markup (page-width modes wrap it
// differently), so find whichever ancestor actually scrolls.
function scroller() {
  let el = root();
  while (el && el !== document.body) {
    const oy = getComputedStyle(el).overflowY;
    if ((oy === 'auto' || oy === 'scroll') && el.scrollHeight > el.clientHeight + 2) return el;
    el = el.parentElement;
  }
  return document.scrollingElement || document.documentElement;
}

const topOf = (el, sc) => el.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop;

export function getScrollAnchor() {
  const sc = scroller();
  const r = root();
  const top = sc.scrollTop;
  const max = Math.max(1, sc.scrollHeight - sc.clientHeight);

  // Topic: last heading at or above the viewport top.
  const headings = [...r.querySelectorAll(HEADING_SEL)];
  let heading = null;
  let headingOrdinal = 0;
  for (const h of headings) {
    if (topOf(h, sc) <= top + 4) heading = h; else break;
  }
  if (heading) {
    const text = norm(heading.textContent);
    for (const h of headings) {
      if (h === heading) break;
      if (norm(h.textContent) === text) headingOrdinal++;
    }
    heading = text;
  }

  // Exact spot: first block whose bottom is still below the viewport top.
  let fingerprint = null;
  for (const b of r.querySelectorAll(BLOCK_SEL)) {
    if (topOf(b, sc) + b.offsetHeight > top + 2) {
      const t = norm(b.textContent);
      if (t) fingerprint = t;
      break;
    }
  }

  return { heading, headingOrdinal, fingerprint, ratio: Math.min(1, Math.max(0, top / max)) };
}

export function restoreScrollAnchor(anchor) {
  if (!anchor) return false;
  const sc = scroller();
  const r = root();
  const scrollTo = (el) => { sc.scrollTop = Math.max(0, topOf(el, sc) - 8); return true; };

  // 1) Find the topic; prefer the exact line within it if that line survived.
  if (anchor.heading) {
    const matches = [...r.querySelectorAll(HEADING_SEL)].filter((h) => norm(h.textContent) === anchor.heading);
    const h = matches[anchor.headingOrdinal] || matches[0];
    if (h) {
      if (anchor.fingerprint) {
        // Walk forward from the heading until the next heading — staying inside the topic
        // keeps an identical sentence elsewhere in the document from stealing the anchor.
        const blocks = [...r.querySelectorAll(BLOCK_SEL)];
        const start = blocks.indexOf(h);
        for (let i = start + 1; i < blocks.length; i++) {
          if (blocks[i].matches(HEADING_SEL)) break;
          if (norm(blocks[i].textContent) === anchor.fingerprint) return scrollTo(blocks[i]);
        }
      }
      return scrollTo(h);
    }
  }

  // 2) Topic gone — the exact line anywhere is still better than guessing.
  if (anchor.fingerprint) {
    for (const b of r.querySelectorAll(BLOCK_SEL)) {
      if (norm(b.textContent) === anchor.fingerprint) return scrollTo(b);
    }
  }

  // 3) Nothing recognizable survived; approximate.
  if (typeof anchor.ratio === 'number' && anchor.ratio > 0) {
    sc.scrollTop = anchor.ratio * Math.max(1, sc.scrollHeight - sc.clientHeight);
    return true;
  }
  return false;
}
