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
