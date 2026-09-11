// Reading dropped files for the host, in two phases.
//
// The OS doesn't expose a dropped file's path to web content, so the host can't
// open the file itself — the editor has to read it and hand the bytes over. What
// it must NOT do is read every dropped file in full before the host has said it
// wants any of them: a 200 MB video dropped by accident became a >260 MB base64
// string, copied again into a web message, decoded again on the host — a gigabyte
// of peak across two processes, a frozen UI, and quite possibly a message the
// bridge silently drops.
//
// So phase one sends only what routing needs: each file's name, its size, and its
// first HEAD_BYTES bytes. The host routes on that (DropRouting.Plan — the rules
// live there and only there) and then asks, in phase two, for the full bytes of
// the few files it actually chose.
//
// Everything here is pure but for the FileReader it is handed: every function
// takes the reader factory, so the whole reader can be tested in node with no DOM.

/** How many bytes of each dropped file travel in the fileDrop message.
 *
 * Mirrors DropRouting.SniffLength on the host, which is the length of the longest
 * signature it sniffs for (a BMP's file header plus the DIB header size at offset
 * 14). Not read FROM the host: the host tolerates a head of any length, because it
 * only ever looks at the prefix — so the two can differ without breaking, and a
 * head that is too SHORT is the only failure mode. 32 is the value that covers
 * every signature today. */
export const HEAD_BYTES = 32;

const newFileReader = () => new FileReader();

/**
 * A blob's bytes as base64, or null if it could not be read.
 *
 * Resolves on every path a FileReader can take: load, error, abort, and a
 * readAsDataURL that throws outright. A promise that can hang here would hang the
 * host's insert behind it, which is why abort is wired as well as error. A reader
 * that fires two of them settles on the first — resolve() after a promise has
 * settled is a no-op in the language, so no flag guards it (a flag here was
 * untestable: no mutation of it could change an observable answer).
 */
export function readBase64(blob, makeReader = newFileReader) {
  return new Promise((resolve) => {
    let reader;
    try {
      reader = makeReader();
    } catch (_) {
      return resolve(null);
    }
    // A data URL is "data:<type>;base64,<payload>"; only the payload travels. An
    // empty file can come back as a bare "data:" with no comma: that is an empty
    // payload, not an unreadable file.
    reader.onload = () => {
      const s = String(reader.result);
      const comma = s.indexOf(',');
      resolve(comma < 0 ? '' : s.slice(comma + 1));
    };
    reader.onerror = () => resolve(null);
    reader.onabort = () => resolve(null);
    try {
      reader.readAsDataURL(blob);
    } catch (_) {
      resolve(null);
    }
  });
}

/** The first HEAD_BYTES of a file — or the whole file, if it is shorter, or if it
 *  has no slice() to take. */
function head(file) {
  return typeof file.slice === 'function' ? file.slice(0, HEAD_BYTES) : file;
}

/**
 * Phase one: what the host routes on, in drop order.
 *
 * Returns one entry per dropped file — `{name, size, headBase64}` — with
 * headBase64 null for a file that could not be read at all (a dropped folder),
 * which the host refuses by name. Drop order is the contract: the host answers in
 * indices into this array.
 */
export function readHeads(files, makeReader = newFileReader) {
  return Promise.all(Array.from(files, async (file) => ({
    name: file.name,
    size: typeof file.size === 'number' ? file.size : 0,
    headBase64: await readBase64(head(file), makeReader),
  })));
}

/**
 * Which drop a phase-two request is about, and what it actually asks for.
 *
 * The generation token. `dropSeq` is the editor's counter for the drop whose File
 * objects are currently held; `drop` is the counter the host's request names. They
 * differ whenever the user dropped again while the host was routing — and then
 * index 3 no longer means the file the host routed, it means whatever is third in
 * the NEW drop. Reading it would hand the host a file it never routed, embedded
 * under a name from the drop before. So a request about any other drop is `stale`
 * and reads nothing; the host still gets one null entry per index it named, so it
 * can tell "could not read" from "no answer at all".
 *
 * `indices` is sanitised, not trusted: whole numbers only (files[1.5] and
 * files[NaN] are not files), each one once (a duplicate would read the file and
 * base64 it twice), in the order asked. An index that names no file is left in —
 * readFull answers null for it, and the host needs that entry.
 *
 * Pure, and separate from main.js for exactly that reason: the rest of the
 * handshake needs a WebView, a host and a real drop, and this is the part of it
 * that decides whether a user's picture or someone else's goes into the document.
 */
export function planDroppedRead(dropSeq, drop, indices) {
  const seen = new Set();
  const asked = [];
  if (Array.isArray(indices)) {
    for (const index of indices) {
      if (!Number.isInteger(index) || seen.has(index)) continue;
      seen.add(index);
      asked.push(index);
    }
  }
  return { stale: drop !== dropSeq, indices: asked };
}

/**
 * Post one phase-two answer to the host, and say whether it got there.
 *
 * The host blocks on this exact message, so "posted nothing" is the one outcome
 * that must not happen quietly — and it can: an answer carrying a picture at the
 * host's 64 MB ceiling is about 85 MB of base64, which the bridge may simply
 * refuse. `post` is postToHost, which reports false rather than throwing when the
 * bridge rejects the message or isn't there at all.
 *
 * So a refused payload is followed by a SMALL one — same type, same drop, no files
 * — which the host reads as "none of these could be had" and refuses rather than
 * waits for. If that one is refused too the bridge itself is gone, nothing here can
 * reach the host, and the host's own timeout is what ends the wait; this returns
 * false to say so.
 */
export function postAnswer(post, drop, files) {
  const send = (message) => {
    try {
      return post(message) !== false;
    } catch (_) {
      // A post that throws instead of reporting is still a post that did not go —
      // and an exception escaping here would leave readDroppedFiles' promise
      // rejected with the host hearing nothing at all.
      return false;
    }
  };
  return send({ type: 'droppedFileBytes', drop, files })
    || send({ type: 'droppedFileBytes', drop, files: null, error: 'post-failed' });
}

/**
 * Phase two: the full bytes of the files the host chose, by their index in the
 * phase-one array.
 *
 * Returns `[{index, base64}]` in the order asked, with base64 null for a file that
 * could not be read — including an index that isn't in the drop at all, so a stale
 * or malformed request can't read the wrong file or throw.
 */
export function readFull(files, indices, makeReader = newFileReader) {
  return Promise.all(Array.from(indices, async (index) => ({
    index,
    base64: files[index] ? await readBase64(files[index], makeReader) : null,
  })));
}
