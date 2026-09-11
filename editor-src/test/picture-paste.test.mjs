// The picture ceiling on a paste into the formatted view (picture-paste.js).
//
// Every other route a picture takes into a document — a dropped file, Insert ▸
// Picture — is the host's, and the host applies PictureLimit to it. A paste here
// is Chromium's own (with no upload plugin, a clipboard holding only a picture is
// pasted by the browser into ProseMirror's hidden capture element), so the host
// never sees it and the editor has to apply the ceiling itself. These tests pin
// the decision; picture-paste-editor.test.mjs pins that the real editor asks it
// before ProseMirror or the browser can paste anything.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { refusedPictureSize, ceilingFrom, refusalMessage } from '../src/picture-paste.js';

const CEILING = 1000;

/** A clipboardData stand-in, shaped like the browser's: getData answers from
 *  `formats` ('' for a format that is not there, as the browser answers), and
 *  `items` / `files` are what the paste event lists. */
function clipboard({ formats = {}, items, files } = {}) {
  const data = { getData: (type) => formats[type] ?? '' };
  if (items !== undefined) data.items = items;
  if (files !== undefined) data.files = files;
  return data;
}

/** One clipboard item holding a picture of `size` bytes, as Chromium lists a
 *  screenshot: kind 'file', type 'image/png', a File named image.png. */
function picture(size, type = 'image/png') {
  return { kind: 'file', type, getAsFile: () => ({ name: 'image.png', type, size }) };
}

test('a picture past the ceiling, pasted on its own, is refused and its size reported', () => {
  assert.equal(refusedPictureSize(clipboard({ items: [picture(CEILING + 1)] }), CEILING), CEILING + 1);
  assert.equal(refusedPictureSize(clipboard({ items: [picture(5 * CEILING, 'image/jpeg')] }), CEILING), 5 * CEILING);
});

test('exactly the ceiling goes in; one byte more does not', () => {
  assert.equal(refusedPictureSize(clipboard({ items: [picture(CEILING)] }), CEILING), null);
  assert.equal(refusedPictureSize(clipboard({ items: [picture(CEILING + 1)] }), CEILING), CEILING + 1);
});

test("the ceiling is the one the host passed in, never a number of the editor's own", () => {
  // The host's is 64 MB today. A guard with a 64 MB of its own would agree with
  // the host at 64 MB and nowhere else — so the same picture is judged against
  // ceilings on both sides of it.
  const MB = 1024 * 1024;
  const sixtyFive = clipboard({ items: [picture(65 * MB)] });
  assert.equal(refusedPictureSize(sixtyFive, 64 * MB), 65 * MB);
  assert.equal(refusedPictureSize(sixtyFive, 128 * MB), null);
  assert.equal(refusedPictureSize(clipboard({ items: [picture(11)] }), 10), 11);
});

test('with no usable ceiling from the host, nothing is refused', () => {
  // The host always passes one (PictureLimit.EditorOptionsJson). A page started
  // without it — the plain-browser self-test — pastes as it always did, rather
  // than refusing every picture over a number nobody gave it.
  const huge = clipboard({ items: [picture(10 ** 12)] });
  for (const ceiling of [undefined, null, NaN, -1, 1.5, '1000', Infinity]) {
    assert.equal(refusedPictureSize(huge, ceiling), null, `ceiling ${String(ceiling)}`);
  }
});

test('a paste with nothing that is a picture is left alone', () => {
  const pdf = { kind: 'file', type: 'application/pdf', getAsFile: () => ({ name: 'a.pdf', type: 'application/pdf', size: 10 ** 9 }) };
  const words = { kind: 'string', type: 'text/plain', getAsFile: () => null };
  assert.equal(refusedPictureSize(clipboard({ items: [pdf] }), CEILING), null);
  assert.equal(refusedPictureSize(clipboard({ items: [words] }), CEILING), null);
  assert.equal(refusedPictureSize(clipboard({ items: [] }), CEILING), null);
  // Only a picture item is asked for its file; there is nothing to learn from any
  // other, so none is asked.
  const untouchable = {
    kind: 'file',
    type: 'application/zip',
    getAsFile: () => { throw new Error('asked for the file of an item that is not a picture'); },
  };
  assert.equal(refusedPictureSize(clipboard({ items: [untouchable] }), CEILING), null);
});

test('a picture that comes with text is left alone: text wins', () => {
  // ProseMirror pastes the text (or the HTML) whenever the clipboard has any, and
  // the picture beside it is never used — so it is not this guard's to refuse. The
  // source view's paste has the same rule (ImagePaste.ShouldHandle).
  const huge = [picture(CEILING * 10)];
  for (const [format, value] of [
    ['text/plain', 'words'],
    ['text/html', '<img src="x.png">'],
    ['text/uri-list', 'https://example.com/x.png'],
    ['Text', 'words'],
  ]) {
    assert.equal(refusedPictureSize(clipboard({ formats: { [format]: value }, items: huge }), CEILING), null, format);
  }
  // A format that is there but EMPTY is not text: ProseMirror goes by the value,
  // not by the list of types, and so does this.
  assert.equal(
    refusedPictureSize(clipboard({ formats: { 'text/plain': '', 'text/html': '' }, items: huge }), CEILING),
    CEILING * 10);
});

test('no clipboardData is left alone', () => {
  assert.equal(refusedPictureSize(undefined, CEILING), null);
  assert.equal(refusedPictureSize(null, CEILING), null);
});

test('a picture whose size cannot be read is not refused', () => {
  // The host's "did not say" (-1): no ceiling applies, rather than a picture being
  // refused over a size nobody reported.
  const noFile = { kind: 'file', type: 'image/png', getAsFile: () => null };
  const noSize = { kind: 'file', type: 'image/png', getAsFile: () => ({ type: 'image/png' }) };
  assert.equal(refusedPictureSize(clipboard({ items: [noFile] }), CEILING), null);
  assert.equal(refusedPictureSize(clipboard({ items: [noSize] }), CEILING), null);
});

test('the files list is read when the clipboard has no items list', () => {
  const files = [
    { name: 'a.png', type: 'image/png', size: CEILING + 1 },
    { name: 'b.txt', type: 'text/plain', size: CEILING * 9 },
  ];
  assert.equal(refusedPictureSize(clipboard({ files }), CEILING), CEILING + 1);
  assert.equal(refusedPictureSize(clipboard({ files: [files[1]] }), CEILING), null);
  assert.equal(refusedPictureSize(clipboard({}), CEILING), null);   // neither list at all
});

test('several pictures: refused if any is past the ceiling, and the largest is the one reported', () => {
  assert.equal(refusedPictureSize(clipboard({ items: [picture(10), picture(5000), picture(2000)] }), CEILING), 5000);
  assert.equal(refusedPictureSize(clipboard({ items: [picture(10), picture(CEILING)] }), CEILING), null);
});

test('the ceiling is read from what the host hands MDM.create', () => {
  assert.equal(ceilingFrom({ maxPictureBytes: 64 * 1024 * 1024 }), 64 * 1024 * 1024);
  assert.equal(ceilingFrom({ maxPictureBytes: 0 }), 0);
  for (const options of [
    undefined, null, {},
    { maxPictureBytes: -1 }, { maxPictureBytes: '67108864' }, { maxPictureBytes: 1.5 }, { maxPictureBytes: NaN },
  ]) {
    assert.equal(ceilingFrom(options), null, JSON.stringify(options));
  }
});

test('the refusal goes to the host as one small message', () => {
  assert.deepEqual(refusalMessage(123456789), { type: 'pictureRefused', size: 123456789 });
});
