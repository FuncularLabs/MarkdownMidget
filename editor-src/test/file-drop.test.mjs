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
import { readBase64, readHeads, readFull, planDroppedRead, postAnswer, HEAD_BYTES } from '../src/file-drop.js';

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

// ===== the generation token (review finding NF-5): which drop a request is about
//
// This decision used to live inline in main.js's readDroppedFiles, where nothing
// could reach it: the whole handshake needed a WebView, a host and a real drop. It
// is the decision that stops a request about drop 1 being answered with drop 2's
// files, so it is the one that most needs a test.

test('planDroppedRead reads the files of the drop the request names', async () => {
  assert.deepEqual(planDroppedRead(3, 3, [1, 0]), { stale: false, indices: [1, 0] });
});

test('planDroppedRead calls a request about an older drop stale, and reads nothing', async () => {
  // The host asked about drop 1; the user has since dropped again, so droppedFiles
  // now holds drop 2's files and index 0 is a DIFFERENT file. Reading it would hand
  // the host bytes it never asked for, under drop 1's name.
  assert.deepEqual(planDroppedRead(2, 1, [0, 1]), { stale: true, indices: [0, 1] });
  // The indices survive the staleness: the answer still needs one entry per index
  // asked for, each of them null, or the host is left waiting for entries that
  // never come.
});

test('planDroppedRead calls a request about a drop that has not happened stale', async () => {
  // A drop number ahead of the counter is not this editor's drop either — a
  // malformed or replayed request, and reading for it is as wrong as reading for a
  // superseded one.
  assert.deepEqual(planDroppedRead(1, 2, [0]), { stale: true, indices: [0] });
  // And drop 0 is what the host parses when a message did not say which drop it is;
  // the counter starts at 1, so it matches nothing.
  assert.deepEqual(planDroppedRead(1, 0, [0]), { stale: true, indices: [0] });
});

test('planDroppedRead calls a request under drop 0 stale, even before the first drop', async () => {
  // AR-8. dropSeq is pre-incremented, so 0 is never a drop the editor issued — it
  // is what the host parses when a message did not say which drop it is, and the
  // host refuses such a request up front (DropHandshake.CanBeAnswered). Before the
  // first drop, dropSeq is still 0, so `drop !== dropSeq` matched by arithmetic
  // accident and a request naming drop 0 was treated as current. droppedFiles is
  // empty at that moment, so nothing is read today; the decision was wrong all the
  // same, and it is the only thing standing between a request and the files.
  assert.deepEqual(planDroppedRead(0, 0, [0]), { stale: true, indices: [0] });
  assert.deepEqual(planDroppedRead(0, 0, []), { stale: true, indices: [] });
  // And a drop number below 0 is no editor's either, whatever the counter is on.
  assert.deepEqual(planDroppedRead(0, -1, [0]), { stale: true, indices: [0] });
  assert.deepEqual(planDroppedRead(3, -1, [0]), { stale: true, indices: [0] });
});

test('planDroppedRead asks for a duplicated index once', async () => {
  // Twice would read the file twice and answer twice — two entries with the same
  // key, which is one base64 copy of a picture more than the host needs in memory.
  assert.deepEqual(planDroppedRead(1, 1, [0, 1, 0, 1, 0]), { stale: false, indices: [0, 1] });
});

test('planDroppedRead drops an index that is not a whole number', async () => {
  // files[1.5], files["0"], files[null] are not the file the host meant, and
  // files[NaN] is undefined. None of them is answered with a guess.
  assert.deepEqual(planDroppedRead(1, 1, [0, 1.5, '1', null, undefined, NaN, 2]),
    { stale: false, indices: [0, 2] });
});

test('planDroppedRead treats a missing or non-array request as asking for nothing', async () => {
  // ExecuteScriptAsync can only hand this whatever the host serialised; a shape
  // that is not a list is answered with an empty list rather than a throw the host
  // never hears about.
  for (const bad of [undefined, null, 'nope', 7, {}]) {
    assert.deepEqual(planDroppedRead(1, 1, bad), { stale: false, indices: [] });
  }
});

test('planDroppedRead leaves an index that is not in the drop to readFull', async () => {
  // Not filtered here: readFull already answers null for an index with no file, and
  // the answer must carry an entry for every index the host asked about — including
  // the ones that name nothing — so it can tell "could not read" from "no answer".
  assert.deepEqual(planDroppedRead(1, 1, [0, 9, -1]), { stale: false, indices: [0, 9, -1] });
});

// ===== posting the answer (review finding NF-3)
//
// readDroppedFiles' comment claimed it "ALWAYS posts exactly once". postToHost
// could not keep that promise: it wrapped chrome.webview.postMessage in a bare
// catch, so a payload the bridge refuses — about 85 MB of base64 at the 64 MB
// ceiling — meant no post at all, and the host waited forever. It now says whether
// the post went, and a refused payload is followed by a small message that can.

test('postAnswer posts the bytes once when the bridge takes them', async () => {
  const sent = [];
  assert.equal(postAnswer((m) => { sent.push(m); return true; }, 4, [{ index: 0, base64: 'AAA' }]), true);
  assert.deepEqual(sent, [{ type: 'droppedFileBytes', drop: 4, files: [{ index: 0, base64: 'AAA' }] }]);
});

test('postAnswer follows a refused payload with a small failure message', async () => {
  // The message the host has to hear: same type, same drop — so it matches the read
  // being waited on — with no files, which the host reads as "none of these could be
  // had" and refuses rather than waits for.
  const sent = [];
  // A bridge that refuses the payload carrying bytes and takes the small one.
  const post = (m) => { sent.push(m); return m.files === null; };

  assert.equal(postAnswer(post, 4, [{ index: 0, base64: 'x'.repeat(8) }]), true);

  assert.equal(sent.length, 2);
  assert.deepEqual(sent[1], { type: 'droppedFileBytes', drop: 4, files: null, error: 'post-failed' });
});

test('postAnswer reports that nothing got through when even the small message fails', async () => {
  // The bridge itself is gone. Nothing here can help and nothing pretends to: the
  // host's timeout is what ends that wait.
  assert.equal(postAnswer(() => false, 4, [{ index: 0, base64: 'AAA' }]), false);
});

test('postAnswer treats a post that threw as one that did not go, and still tries the small message', async () => {
  // postToHost catches its own throw and reports false; a post function that throws
  // outright must not escape into readDroppedFiles' promise, where the host would
  // hear nothing at all. A throw is a post that did not go, so it is followed by the
  // small failure message exactly as a post that returned false is — the host has to
  // hear SOMETHING, and the small one is the message that can still fit.
  //
  // (AR-5. This test used to be called "does not retry a post that threw" and
  // asserted only the return value, so the call count — two, not one — could
  // contradict its own name with nothing to catch it.)
  const tried = [];
  const post = (m) => { tried.push(m); throw new Error('bridge gone'); };

  assert.equal(postAnswer(post, 4, []), false);

  assert.equal(tried.length, 2);
  assert.deepEqual(tried[0], { type: 'droppedFileBytes', drop: 4, files: [] });
  assert.deepEqual(tried[1], { type: 'droppedFileBytes', drop: 4, files: null, error: 'post-failed' });
});
