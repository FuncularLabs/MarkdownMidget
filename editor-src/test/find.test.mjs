// Find & Replace in the formatted view (#5): find.js driven against the real
// editor in jsdom, the way the host drives it through MDM.find*.
//
// The host builds the regex (source + flags) and the per-mode replacement in
// FindEngine and hands both across; what is proved here is the editor side —
// that a replacement lands on exactly the found range and nothing else (F3),
// that Replace All is one transaction and so one undo step (F4), that it is
// scoped to a selection when there is one (F2), that the group forms the host
// documents expand (F5), and that Replace advances to the next match (F1).
import test, { before, beforeEach, describe } from 'node:test';
import assert from 'node:assert/strict';
import { undo, undoDepth } from '@milkdown/kit/prose/history';
import { mountEditor } from './jsdom-editor.mjs';
import {
  findReset, findNext, findClear, findReplace, findReplaceAll, findCaptureScope, expandTemplate,
} from '../src/find.js';

let ed;
before(async () => {
  ed = await mountEditor();
  // find.js walks the DOM with a TreeWalker; NodeFilter is the one global the
  // editor itself never reads, so the shared harness does not install it.
  globalThis.NodeFilter = ed.window.NodeFilter;
});
// The module holds the index and the captured scope between calls, as it does for
// the life of the dialog; each case starts as a freshly opened dialog would.
beforeEach(() => findClear());

/** Load a document and return the editor's own serialisation of it. */
const load = (text) => ed.roundTrip(text);
/** The document as markdown without the serialiser's trailing newline(s). */
const md = () => ed.markdown().replace(/\n+$/, '');
/** Index the current document for `source` (a JS regex source), as the host does. */
const scan = (source, flags = 'g') => findReset(source, flags, ed.view());
/** The DOM selection's text — where Find leaves the highlight. */
const highlighted = () => ed.window.getSelection().getRangeAt(0).toString();
/** ProseMirror positions of the textblock whose text is `text`. */
const blockRange = (text) => {
  let found = null;
  ed.view().state.doc.descendants((node, pos) => {
    if (found || !node.isTextblock || node.textContent !== text) return;
    found = { from: pos + 1, to: pos + 1 + node.content.size };
  });
  assert.ok(found, `no textblock reads ${JSON.stringify(text)}`);
  return found;
};

describe('ReplacesExactlyTheFoundRangeAcrossInlineMarks', () => {
  test('a match running from plain text into bold is replaced as plain text', () => {
    load('plain **bold** tail');
    assert.equal(scan('n bo').total, 1);
    findNext(true);
    const r = findReplace(ed.view(), 'X', true, true);
    assert.equal(r.replaced, 1);
    assert.equal(md(), 'plaiX**ld** tail');
  });

  test('a match starting inside bold carries the bold onto the replacement', () => {
    load('plain **bold** tail');
    scan('ld t');
    findNext(true);
    findReplace(ed.view(), 'Y', true, true);
    assert.equal(md(), 'plain **boY**ail');
  });

  test('a match starting on the first bold character is bold (the marks are the first character’s, not the caret’s)', () => {
    // ProseMirror's $pos.marks() at a boundary reports the text BEFORE it; a
    // replacement takes the marks of the character it replaces first.
    load('plain **bold** tail');
    scan('bold t');
    findNext(true);
    findReplace(ed.view(), 'Z', true, true);
    assert.equal(md(), 'plain **Z**ail');
  });

  test('a match inside inline code stays code', () => {
    load('- one `cat`\n- two cat');
    scan('cat');
    findNext(true);
    findReplace(ed.view(), 'dog', true, true);
    assert.equal(md(), '- one `dog`\n- two cat');
  });

  test('an empty replacement deletes the range', () => {
    load('a cat b');
    scan('cat ');
    findReplaceAll(ed.view(), '', true);
    assert.equal(md(), 'a b');
  });
});

describe('ReplaceMovesToTheNextMatch', () => {
  test('each Replace lands on the following match, and the last leaves nothing selected', () => {
    load('cat one cat two cat');
    assert.deepEqual(scan('cat'), { total: 3, current: 0 });
    assert.deepEqual(findNext(true), { total: 3, current: 1 });

    let r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 2, current: 1 });
    assert.equal(md(), 'dog one cat two cat');
    assert.equal(highlighted(), 'cat');

    r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 1, current: 1 });

    r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 0, current: 0 });
    assert.equal(md(), 'dog one dog two dog');

    // Nothing found: Replace is just Find Next, and there is nothing to find.
    r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 0, skipped: 0, total: 0, current: 0 });
  });

  test('a replacement that still matches is not revisited before the following match', () => {
    // "cat" -> "cats": the resume point is the end of the replacement, so the next
    // stop is the second cat, not the cats just written.
    load('cat cat');
    scan('cat');
    findNext(true);
    const r = findReplace(ed.view(), 'cats', true, true);
    assert.equal(md(), 'cats cat');
    assert.equal(r.current, 2);        // of the two matches now ("cats" and "cat"), the second
    assert.equal(highlighted(), 'cat');
  });

  test('with nothing found yet, Replace is Find Next', () => {
    load('cat cat');
    scan('cat');                        // indexed, but no current match
    const r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 0, skipped: 0, total: 2, current: 1 });
    assert.equal(md(), 'cat cat');
  });

  test('past the last match, wrap goes round and no-wrap stops', () => {
    load('cat one cat');
    scan('cat');
    findNext(true); findNext(true);     // on the second
    let r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 1, current: 1 });   // wrapped to the first

    load('cat one cat');
    scan('cat');
    findNext(true); findNext(true);
    r = findReplace(ed.view(), 'dog', true, false);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 1, current: 0 });   // one left, none selected
  });
});

describe('FindResetKeepsThePlaceOnAnUnchangedDocument', () => {
  test('re-indexing the same pattern on the same document does not move the cursor', () => {
    load('cat one cat two cat');
    scan('cat');
    findNext(true); findNext(true);
    assert.deepEqual(scan('cat'), { total: 3, current: 2 });
    assert.deepEqual(findNext(true), { total: 3, current: 3 });
  });

  test('after a Replace the host re-index that follows keeps the new place', () => {
    load('cat one cat two cat');
    scan('cat');
    findNext(true);
    findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(scan('cat'), { total: 2, current: 1 });
  });

  test('a different pattern, or an edited document, starts over', () => {
    load('cat one cat');
    scan('cat');
    findNext(true);
    assert.deepEqual(scan('one'), { total: 1, current: 0 });
    findNext(true);
    load('cat one cat');                // a new document (the host loads through the same path)
    assert.deepEqual(scan('one'), { total: 1, current: 0 });
  });

  test('without a view the index is always rebuilt (the pre-#5 contract)', () => {
    load('cat cat');
    findReset('cat', 'g');
    findNext(true);
    assert.deepEqual(findReset('cat', 'g'), { total: 2, current: 0 });
  });
});

describe('ReplaceAllIsOneUndoStep', () => {
  test('one undo restores the whole document', () => {
    // The list sits in the middle on purpose: the editor's trailing plugin adds
    // an empty paragraph after a document that ENDS in a list on any transaction
    // (a Replace All or the undo of one alike), which would make an exact
    // comparison read as a difference that is not this code's.
    const original = load('cat one\n\n- cat two\n\ncat three');
    const before = ed.view().state.doc.toJSON();
    scan('cat');
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 3, skipped: 0, total: 3, inSelection: false });
    assert.equal(md(), 'dog one\n\n- dog two\n\ndog three');
    assert.equal(undoDepth(ed.view().state), 1);
    undo(ed.view().state, ed.view().dispatch);
    assert.deepEqual(ed.view().state.doc.toJSON(), before);
    assert.equal(ed.markdown(), original);
    assert.equal(undoDepth(ed.view().state), 0);
  });

  test('Replace All with no matches changes nothing and adds no undo step', () => {
    load('cat');
    scan('dog');
    assert.deepEqual(findReplaceAll(ed.view(), 'x', true), { replaced: 0, skipped: 0, total: 0, inSelection: false });
    assert.equal(undoDepth(ed.view().state), 0);
  });
});

describe('ReplaceAllIsScopedToTheSelection', () => {
  test('only matches wholly inside the selected text are replaced', () => {
    load('cat one\n\ncat two\n\ncat three');
    const { from, to } = blockRange('cat two');
    ed.selectText(from, to);
    scan('cat');
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 3, inSelection: true });
    assert.equal(md(), 'cat one\n\ndog two\n\ncat three');
  });

  test('a match the selection cuts through is outside it', () => {
    load('cat one\n\ncat two');
    const { from, to } = blockRange('cat two');
    ed.selectText(from + 1, to);        // "at two"
    scan('cat');
    assert.equal(findReplaceAll(ed.view(), 'dog', true).replaced, 0);
    assert.equal(md(), 'cat one\n\ncat two');
  });

  test('a selection that is the current match is Find’s, not the user’s: the whole document', () => {
    load('cat cat');
    scan('cat');
    findNext(true);
    const { from } = blockRange('cat cat');
    ed.selectText(from, from + 3);      // exactly the first match, as F3 in the editor leaves it
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 2, skipped: 0, total: 2, inSelection: false });
  });

  test('the selection captured when Find opened scopes Replace All after Find has moved the selection', () => {
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    assert.deepEqual(findCaptureScope(ed.view()), { from: two.from, to: two.to });
    scan('cat');
    findNext(true);
    const one = blockRange('cat one');
    ed.selectText(one.from, one.from + 3);   // Find's own selection of match 1
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 3, inSelection: true });
    assert.equal(md(), 'cat one\n\ndog two\n\ncat three');
  });

  test('the captured selection follows a Replace and is dropped by findClear and by a foreign edit', () => {
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    findCaptureScope(ed.view());
    scan('cat');
    findNext(true);
    findReplace(ed.view(), 'tiger', true, true);    // "cat one" -> "tiger one": everything after shifts by 2
    const shifted = blockRange('cat two');
    assert.equal(shifted.from, two.from + 2);
    ed.selectText(shifted.from, shifted.from + 3);  // Find's selection of what is now match 1 ("cat" of "cat two")
    let r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 2, inSelection: true });
    assert.equal(md(), 'tiger one\n\ndog two\n\ncat three');

    // An edit the find layer did not make: the captured scope no longer describes
    // the document, so it is dropped rather than applied to the wrong text.
    const dogTwo = blockRange('dog two');
    ed.selectText(dogTwo.from, dogTwo.to);
    findCaptureScope(ed.view());
    const v = ed.view();
    v.dispatch(v.state.tr.insertText('Z', 1, 1));
    scan('cat');
    findNext(true);
    const three = blockRange('cat three');
    ed.selectText(three.from, three.from + 3);      // Find's selection of the one remaining match
    r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, total: 1, inSelection: false });

    // And findClear forgets it.
    load('cat one\n\ncat two');
    const b = blockRange('cat two');
    ed.selectText(b.from, b.to);
    findCaptureScope(ed.view());
    findClear();
    scan('cat');
    findNext(true);
    const a = blockRange('cat one');
    ed.selectText(a.from, a.from + 3);
    assert.equal(findReplaceAll(ed.view(), 'dog', true).inSelection, false);
  });

  test('capturing again keeps the range while the selection is Find’s, and drops it for a caret', () => {
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    findCaptureScope(ed.view());
    scan('cat');
    findNext(true);
    const one = blockRange('cat one');
    ed.selectText(one.from, one.from + 3);       // Find's selection of match 1
    // Ctrl+F again with Find's selection: the kept range is still the answer.
    assert.deepEqual(findCaptureScope(ed.view()), { from: two.from, to: two.to });
    assert.equal(findReplaceAll(ed.view(), 'dog', true).inSelection, true);
    assert.equal(md(), 'cat one\n\ndog two\n\ncat three');

    // Ctrl+F again after clicking in the document (a caret): nothing is kept.
    ed.selectText(one.from, one.from);
    assert.equal(findCaptureScope(ed.view()), null);
    scan('cat');
    findNext(true);
    ed.selectText(one.from, one.from + 3);
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 2, skipped: 0, total: 2, inSelection: false });
  });

  test('a caret is no selection: the whole document', () => {
    load('cat one\n\ncat two');
    const { from } = blockRange('cat two');
    ed.selectText(from, from);
    scan('cat');
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 2, skipped: 0, total: 2, inSelection: false });
  });
});

describe('AMatchAcrossBlocksIsLeftAlone', () => {
  test('a match that runs from one paragraph into the next is neither replaced nor joined', () => {
    // The index carries no separator between blocks, so "endstart" is found; a
    // replacement would have to join the paragraphs, and never does.
    load('end\n\nstart');
    assert.equal(scan('endstart').total, 1);
    assert.deepEqual(findReplaceAll(ed.view(), 'x', true), { replaced: 0, skipped: 1, total: 1, inSelection: false });
    assert.equal(md(), 'end\n\nstart');
    findNext(true);
    assert.deepEqual(findReplace(ed.view(), 'x', true, true), { replaced: 0, skipped: 1, total: 1, current: 1 });
    assert.equal(md(), 'end\n\nstart');
  });
});

describe('RegexGroupsSubstitute', () => {
  test('$1 and ${name} expand in Regex mode; literal mode inserts the template as typed', () => {
    load('me@host');
    scan('(\\w+)@(\\w+)');
    findReplaceAll(ed.view(), '$2 at $1', false);
    assert.equal(md(), 'host at me');

    load('me@host');
    scan('(?<user>\\w+)@');
    findReplaceAll(ed.view(), '${user}!', false);
    assert.equal(md(), 'me!host');

    load('cost');
    scan('cost');
    findReplaceAll(ed.view(), '$$5 $1', true);
    assert.equal(md(), '$$5 $1');
  });

  test('expandTemplate mirrors the .NET forms the host documents', () => {
    const m = ['ab', 'a', undefined];
    m.groups = { first: 'a', none: undefined };
    assert.equal(expandTemplate('[$$][$&][$0][$1][$2][${first}][$<first>][${none}][${1}][$9][$x][$]', m),
      '[$][ab][ab][a][][a][a][][a][$9][$x][$]');
  });
});

describe('MalformedPatternReplacesNothing', () => {
  test('a pattern that does not compile indexes nothing, so Replace and Replace All apply nothing', () => {
    load('cat (unclosed');
    const r0 = scan('(unclosed');
    assert.equal(r0.error, 'Invalid pattern');
    assert.equal(r0.total, 0);
    assert.deepEqual(findReplaceAll(ed.view(), 'x', false), { replaced: 0, skipped: 0, total: 0, inSelection: false });
    assert.deepEqual(findReplace(ed.view(), 'x', false, true), { replaced: 0, skipped: 0, total: 0, current: 0 });
    assert.equal(md(), 'cat (unclosed');
  });
});
