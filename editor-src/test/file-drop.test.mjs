// The drop reader is the only part of the editor that can lose a user's file
// silently: a promise that never settles leaves the host waiting forever with no
// message and no insert, and a payload sliced one character wrong corrupts the
// picture that goes into the document. None of it needed a DOM to test — the
// FileReader arrives as a factory — so none of it was tested. Now it is.
//
// `node --test` has no default per-test timeout, so the failure these tests exist
// to catch — a promise that never settles — used to HANG the suite rather than
// fail it (delete onabort and see). The npm `test` script passes
// --test-timeout=10000 so a hang is reported as a failing test with a message and
// a non-zero exit; run these with `npm test`, not a bare `node --test`.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readBase64, readHeads, readFull, HEAD_BYTES } from '../src/file-drop.js';

// A FileReader stand-in that takes the outcome it should produce. Real readers are
// asynchronous, so these are too: a reader that called back synchronously would
// hide an ordering bug.
function fakeReaderFactory(outcomeFor) {
  const reads = [];
  const make = () => ({
    result: null,
    readAsDataURL(blob) {
      reads.push(blob);
      const outcome = outcomeFor(blob);
      queueMicrotask(() => {
        if (outcome.event === 'load') { this.result = outcome.result; this.onload(); }
        else if (outcome.event === 'error') this.onerror();
        else if (outcome.event === 'abort') this.onabort();
        // 'silent' fires nothing at all.
      });
    },
  });
  make.reads = reads;
  return make;
}

const loads = (result) => fakeReaderFactory(() => ({ event: 'load', result }));

/** A File stand-in: a name, a size, and a slice() that records its arguments. */
function fakeFile(name, size = 100) {
  const file = {
    name,
    size,
    slices: [],
    slice(start, end) {
      file.slices.push([start, end]);
      return { of: name, start, end };
    },
  };
  return file;
}

test('a zero-length file comes back as a bare "data:" and reads as an empty payload', async () => {
  // Not an unreadable file — an empty one. Returning null here would refuse an
  // empty .md that should open as an empty document.
  assert.equal(await readBase64({}, loads('data:')), '');
});

test('the payload after the comma is what travels, and nothing else', async () => {
  assert.equal(await readBase64({}, loads('data:image/png;base64,AAA')), 'AAA');
  // Base64 has no commas, so the FIRST comma is the delimiter and the rest of the
  // string is payload whatever it contains.
  assert.equal(await readBase64({}, loads('data:text/plain;base64,AA,BB')), 'AA,BB');
  assert.equal(await readBase64({}, loads('data:;base64,')), '');
});

test('a read error resolves null rather than rejecting', async () => {
  assert.equal(await readBase64({}, fakeReaderFactory(() => ({ event: 'error' }))), null);
});

test('an aborted read resolves null — onabort is wired, so the promise cannot hang', async () => {
  // The failure this guards: only onerror wired, the read aborted (the file went
  // away mid-drop), the promise never settles, and the host's insert waits forever
  // with nothing on screen to say so.
  assert.equal(await readBase64({}, fakeReaderFactory(() => ({ event: 'abort' }))), null);
});

test('a readAsDataURL that throws resolves null instead of escaping the promise', async () => {
  const throwing = () => ({ readAsDataURL() { throw new Error('nope'); } });
  assert.equal(await readBase64({}, throwing), null);
});

test('readHeads slices exactly the first HEAD_BYTES of every file', async () => {
  // The whole point of phase one: a 200 MB video contributes 32 bytes to the
  // message, not 200 MB. If this ever read the whole file again, the drop would be
  // back to base64-ing gigabytes before the host could refuse them.
  assert.equal(HEAD_BYTES, 32);
  const files = [fakeFile('a.png', 4_000), fakeFile('big.mp4', 200_000_000)];
  const make = loads('data:application/octet-stream;base64,AAA');

  await readHeads(files, make);

  assert.deepEqual(files[0].slices, [[0, HEAD_BYTES]]);
  assert.deepEqual(files[1].slices, [[0, HEAD_BYTES]]);
  assert.equal(make.reads.length, 2);
  assert.deepEqual(make.reads.map((b) => b.end), [HEAD_BYTES, HEAD_BYTES]);
});

test('readHeads reports name, size and head for each file, in drop order', async () => {
  const files = [fakeFile('one.md', 10), fakeFile('two.png', 20), fakeFile('three.txt', 30)];
  // Each file's payload differs, so a result that arrived out of order is visible.
  const make = fakeReaderFactory((blob) => ({ event: 'load', result: `data:;base64,${blob.of}` }));

  assert.deepEqual(await readHeads(files, make), [
    { name: 'one.md', size: 10, headBase64: 'one.md' },
    { name: 'two.png', size: 20, headBase64: 'two.png' },
    { name: 'three.txt', size: 30, headBase64: 'three.txt' },
  ]);
});

test('readHeads reports a null head for a file that cannot be read', async () => {
  // A dropped folder. The host refuses a null head by name, whatever it is called.
  const make = fakeReaderFactory((blob) => (blob.of === 'a folder'
    ? { event: 'error' }
    : { event: 'load', result: 'data:;base64,QUJD' }));

  assert.deepEqual(await readHeads([fakeFile('a folder', 0), fakeFile('notes.md', 3)], make), [
    { name: 'a folder', size: 0, headBase64: null },
    { name: 'notes.md', size: 3, headBase64: 'QUJD' },
  ]);
});

test('readHeads on an empty drop is an empty list, not a hang', async () => {
  assert.deepEqual(await readHeads([], loads('data:')), []);
});

test('readFull returns the asked-for indices, in the order asked', async () => {
  const files = [fakeFile('a'), fakeFile('b'), fakeFile('c'), fakeFile('d')];
  const make = fakeReaderFactory((file) => ({ event: 'load', result: `data:;base64,${file.name}` }));

  assert.deepEqual(await readFull(files, [2, 0, 3], make), [
    { index: 2, base64: 'c' },
    { index: 0, base64: 'a' },
    { index: 3, base64: 'd' },
  ]);
  // And the whole file, not a slice: nothing was sliced on this path.
  assert.deepEqual(files[2].slices, []);
});

test('readFull reads only the indices asked for', async () => {
  const files = [fakeFile('a'), fakeFile('b'), fakeFile('c')];
  const make = loads('data:;base64,AAA');

  await readFull(files, [1], make);

  assert.equal(make.reads.length, 1);
  assert.equal(make.reads[0].name, 'b');
});

test('readFull answers null for an index that is not in the drop', async () => {
  // A stale request (the drop it refers to has been replaced) or a malformed one
  // must not throw, and must not read a different file by accident.
  const make = loads('data:;base64,AAA');
  assert.deepEqual(await readFull([fakeFile('a')], [0, 5, -1], make), [
    { index: 0, base64: 'AAA' },
    { index: 5, base64: null },
    { index: -1, base64: null },
  ]);
  assert.equal(make.reads.length, 1);
});

test('readFull answers null for a file whose read fails, without losing the others', async () => {
  // The host treats a null as "this picture could not be read" and inserts NONE of
  // them, so this null has to arrive rather than the promise hanging.
  const files = [fakeFile('a'), fakeFile('b')];
  const make = fakeReaderFactory((file) => (file.name === 'a' ? { event: 'abort' } : { event: 'load', result: 'data:;base64,Qg==' }));

  assert.deepEqual(await readFull(files, [0, 1], make), [
    { index: 0, base64: null },
    { index: 1, base64: 'Qg==' },
  ]);
});
