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
import { readFileSync } from 'node:fs';
import { undo, undoDepth } from '@milkdown/kit/prose/history';
import { mountEditor } from './jsdom-editor.mjs';
import {
  findReset, findNext, findClear, findReplace, findReplaceAll, findCaptureScope,
  expandTemplate, dotnetGroupMap,
} from '../src/find.js';

// The one table both engines answer to (#5 F-1, F-4, F-13). FindEngineTests.cs reads
// the same file; a row that only one side satisfies is the divergence this pins.
const templateTable = JSON.parse(
  readFileSync(new URL('./fixtures/replace-templates.json', import.meta.url), 'utf8'));

// And the other one: which regex constructs Find accepts at all (#5 F-6).
const constructTable = JSON.parse(
  readFileSync(new URL('./fixtures/regex-constructs.json', import.meta.url), 'utf8'));

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

  test('with the caret moved away from the match, Replace is Find Next too', () => {
    // F3 three times, then a click at the top of the document. Replace must not
    // change the match Find last landed on — the source view degrades to Find Next
    // here (IsSourceFindSelection) and this view replaced regardless (#5 F-9).
    load('cat one\n\ncat two\n\ncat three');
    scan('cat');
    findNext(true); findNext(true); findNext(true);       // on the third
    const one = blockRange('cat one');
    ed.selectText(one.from, one.from);                    // the user clicks at the top
    const r = findReplace(ed.view(), 'dog', true, true);
    assert.deepEqual(r, { replaced: 0, skipped: 0, total: 3, current: 1 });
    assert.equal(md(), 'cat one\n\ncat two\n\ncat three');
  });

  test('Find Next leaves a real editor selection, not only a browser highlight', () => {
    // What makes the check above possible: the source view's Find selects in the
    // document, and so does this one now.
    load('cat one cat');
    scan('cat');
    findNext(true);
    const p = blockRange('cat one cat');
    const sel = ed.view().state.selection;
    assert.deepEqual({ from: sel.from, to: sel.to }, { from: p.from, to: p.from + 3 });
    assert.equal(undoDepth(ed.view().state), 0, 'moving the selection is not an undo step');
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
    assert.deepEqual(r, { replaced: 3, skipped: 0, moved: 0, total: 3, inSelection: false });
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
    assert.deepEqual(findReplaceAll(ed.view(), 'x', true), { replaced: 0, skipped: 0, moved: 0, total: 0, inSelection: false });
    assert.equal(undoDepth(ed.view().state), 0);
  });
});

describe('ReplaceAllAppliesItsPlanLastToFirst', () => {
  // Every position in the plan is measured against the document as it is now, so
  // the steps have to be added from the end backwards; adding them front to back
  // shifts every later position by the difference in length (#5 F-2).
  test('a longer replacement does not shift the matches after it', () => {
    load('cat one cat two cat');
    scan('cat');
    assert.equal(findReplaceAll(ed.view(), 'tiger', true).replaced, 3);
    // First to last would give 'tiger ontigerat tiger cat'.
    assert.equal(md(), 'tiger one tiger two tiger');
  });

  test('nor does a shorter one', () => {
    load('cat one cat two cat');
    scan('cat');
    assert.equal(findReplaceAll(ed.view(), 'x', true).replaced, 3);
    assert.equal(md(), 'x one x two x');
  });
});

describe('AnEditBehindTheIndexIsReScannedBeforeAnythingIsReplaced', () => {
  // The host re-issues findReset on every change message, but a change it has not
  // seen yet — or one made by something else entirely — leaves this module holding
  // offsets into a document that no longer exists. ensureFresh re-takes the index
  // first (#5 F-3).
  test('Replace All re-scans rather than replacing at the offsets it remembers', () => {
    load('one cat two');
    scan('cat');
    const v = ed.view();
    v.dispatch(v.state.tr.insertText('ZZZZZZ', 1, 1));     // no re-scan after it
    assert.equal(findReplaceAll(v, 'dog', true).replaced, 1);
    // Without ensureFresh the remembered offset 4 is read against the new text and
    // the document comes back 'ZZZZdogne cat two'.
    assert.equal(md(), 'ZZZZZZone dog two');
  });

  test('Replace on a document edited behind the index replaces nothing', () => {
    load('one cat two');
    scan('cat');
    findNext(true);
    const v = ed.view();
    v.dispatch(v.state.tr.insertText('ZZZZZZ', 1, 1));
    assert.equal(findReplace(v, 'dog', true, true).replaced, 0);
    assert.equal(md(), 'ZZZZZZone cat two');
  });
});

describe('ReplaceAllIsScopedToTheSelection', () => {
  test('only matches wholly inside the selected text are replaced', () => {
    load('cat one\n\ncat two\n\ncat three');
    const { from, to } = blockRange('cat two');
    ed.selectText(from, to);
    scan('cat');
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual(r, { replaced: 1, skipped: 0, moved: 0, total: 3, inSelection: true });
    assert.equal(md(), 'cat one\n\ndog two\n\ncat three');
  });

  test('a match that ends one character past the scope is outside it', () => {
    // The edge itself: '>' rather than '>=', or a stray +1 on the scope end, lets
    // a match that overruns the selection by exactly one character through (#5 F-8).
    load('catx');
    const p = blockRange('catx');
    ed.selectText(p.from, p.from + 2);           // 'ca' — the match runs one past it
    scan('cat');
    assert.equal(findReplaceAll(ed.view(), 'dog', true).replaced, 0);
    assert.equal(md(), 'catx');

    // One character more of selection and the same match is inside.
    findClear();
    load('catx');
    const q = blockRange('catx');
    ed.selectText(q.from, q.from + 3);
    scan('cat');
    assert.equal(findReplaceAll(ed.view(), 'dog', true).replaced, 1);
    assert.equal(md(), 'dogx');
  });

  test('the kept range follows a replacement far longer than the match', () => {
    // The existing pin shifts the document by two characters, which a scope that
    // was never mapped still covers. Seventeen does not (#5 F-7).
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    findCaptureScope(ed.view());
    scan('cat');
    findNext(true);
    findReplace(ed.view(), 'ELEPHANTINEQUADRUPED', true, true);   // 20 characters for 3
    const shifted = blockRange('cat two');
    assert.equal(shifted.from, two.from + 17);
    ed.selectText(shifted.from, shifted.from + 3);   // Find's selection of what is now match 1
    const r = findReplaceAll(ed.view(), 'dog', true);
    assert.deepEqual({ replaced: r.replaced, inSelection: r.inSelection }, { replaced: 1, inSelection: true });
    assert.equal(md(), 'ELEPHANTINEQUADRUPED one\n\ndog two\n\ncat three');
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
    assert.deepEqual(r, { replaced: 2, skipped: 0, moved: 0, total: 2, inSelection: false });
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
    assert.deepEqual(r, { replaced: 1, skipped: 0, moved: 0, total: 3, inSelection: true });
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
    assert.deepEqual(r, { replaced: 1, skipped: 0, moved: 0, total: 2, inSelection: true });
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
    assert.deepEqual(r, { replaced: 1, skipped: 0, moved: 0, total: 1, inSelection: false });

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

  test('a zero-width current match does not read as "deselected" and drop the kept range', () => {
    // Find's own selection of an empty match is a caret. Testing "empty" before
    // "is this Find's own?" threw the user's kept range away the moment Ctrl+F was
    // pressed on one, and scoped the next Replace All to the whole document (#5 F-5).
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    findCaptureScope(ed.view());
    scan('(?=cat)');
    findNext(true);
    const one = blockRange('cat one');
    ed.selectText(one.from, one.from);                // the caret Find leaves on match 1
    assert.deepEqual(findCaptureScope(ed.view()), { from: two.from, to: two.to });
    const r = findReplaceAll(ed.view(), 'X', true);
    assert.deepEqual({ replaced: r.replaced, inSelection: r.inSelection }, { replaced: 1, inSelection: true });
    assert.equal(md(), 'cat one\n\nXcat two\n\ncat three');
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
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 2, skipped: 0, moved: 0, total: 2, inSelection: false });
  });

  test('a selection that was Find’s does not survive as a selection of the replacement', () => {
    // Replace: the selection equal to the match becomes a caret after the
    // replacement, and then Find moves on and selects the NEXT match — never the
    // replacement text, which would pass for the user's own selection next time.
    // (The source view does the same: SelectSourceMatch, or a caret with nothing
    // left to find.)
    load('cat one cat');
    scan('cat');
    findNext(true);
    const p = blockRange('cat one cat');
    ed.selectText(p.from, p.from + 3);
    findReplace(ed.view(), 'tiger', true, true);
    let sel = ed.view().state.selection;
    assert.equal(md(), 'tiger one cat');
    assert.deepEqual({ from: sel.from, to: sel.to }, { from: p.from + 10, to: p.from + 13 });

    // With nothing left to find, a caret after the replacement and nothing selected.
    load('one cat');
    scan('cat');
    findNext(true);
    const last = blockRange('one cat');
    assert.deepEqual(findReplace(ed.view(), 'tiger', true, false), { replaced: 1, skipped: 0, total: 0, current: 0 });
    sel = ed.view().state.selection;
    assert.ok(sel.empty, 'a caret');
    assert.equal(sel.from, last.from + 9);

    // ...so a Replace All after the first one is not scoped to "tiger".
    load('cat one cat');
    scan('cat');
    findNext(true);
    ed.selectText(p.from, p.from + 3);
    findReplace(ed.view(), 'tiger', true, true);
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 1, skipped: 0, moved: 0, total: 1, inSelection: false });
    assert.equal(md(), 'tiger one dog');

    // Replace All with Find's selection and a kept range: the kept range is selected after.
    load('cat one\n\ncat two\n\ncat three');
    const two = blockRange('cat two');
    ed.selectText(two.from, two.to);
    findCaptureScope(ed.view());
    scan('cat');
    findNext(true);
    const one = blockRange('cat one');
    ed.selectText(one.from, one.from + 3);
    findReplaceAll(ed.view(), 'tiger', true);
    sel = ed.view().state.selection;
    // The kept range, grown by the replacement inside it ("tiger" is two longer).
    assert.deepEqual({ from: sel.from, to: sel.to }, { from: two.from, to: two.to + 2 });
    // A second Replace All in the same range, now as the user's own selection.
    scan('tiger');
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 1, skipped: 0, moved: 0, total: 1, inSelection: true });
    assert.equal(md(), 'cat one\n\ndog two\n\ncat three');

    // Replace All with Find's selection and nothing kept: a caret.
    load('cat cat');
    scan('cat');
    findNext(true);
    const q = blockRange('cat cat');
    ed.selectText(q.from, q.from + 3);
    findReplaceAll(ed.view(), 'dog', true);
    assert.ok(ed.view().state.selection.empty);
  });

  test('a caret is no selection: the whole document', () => {
    load('cat one\n\ncat two');
    const { from } = blockRange('cat two');
    ed.selectText(from, from);
    scan('cat');
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true), { replaced: 2, skipped: 0, moved: 0, total: 2, inSelection: false });
  });
});

describe('MatchesLeftAloneAreCountedApart', () => {
  test('a match whose text has moved is not counted as one that spans paragraphs', () => {
    // Two different reasons to leave a match alone, reported as one number and one
    // sentence — "they span paragraphs" — which for this one was simply untrue (#5 F-10).
    load('cat one');
    assert.equal(scan('cat').total, 1);
    // Take the text node out of the editor WITHOUT changing the document: the index
    // still holds it, and the range it describes is nowhere any more. (ensureFresh
    // re-scans on a document change; this is the case it cannot see.)
    ed.window.document.querySelector('.mdm-prosemirror').querySelector('p').firstChild.remove();
    assert.deepEqual(findReplaceAll(ed.view(), 'dog', true),
      { replaced: 0, skipped: 0, moved: 1, total: 1, inSelection: false });
  });
});

describe('AMatchAcrossBlocksIsLeftAlone', () => {
  test('a match that runs from one paragraph into the next is neither replaced nor joined', () => {
    // The index carries no separator between blocks, so "endstart" is found; a
    // replacement would have to join the paragraphs, and never does.
    load('end\n\nstart');
    assert.equal(scan('endstart').total, 1);
    assert.deepEqual(findReplaceAll(ed.view(), 'x', true), { replaced: 0, skipped: 1, moved: 0, total: 1, inSelection: false });
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

});

describe('TheReplacementTemplateSubsetIsTheSameInBothEngines', () => {
  // One row per line of editor-src/test/fixtures/replace-templates.json, which the
  // C# FindEngineTests reads too. Both sides build the regex case-sensitively and
  // multiline, take the first match, and expand the template against it.
  for (const row of templateTable.rows) {
    test(row.name, () => {
      const re = new RegExp(row.pattern, 'gmu');
      const m = re.exec(row.input);
      assert.ok(m, `pattern ${row.pattern} finds nothing in ${JSON.stringify(row.input)}`);
      assert.equal(expandTemplate(row.template, m, dotnetGroupMap(row.pattern)), row.expected);
    });
  }

  test('the .NET group map is used on the real Replace path, not only by the fixture', () => {
    // Dropping the map argument at the call site would read $1 as JavaScript numbers
    // it — 'a' rather than 'b' — and the fixture rows, which pass a map of their own,
    // would not notice.
    load('XXabYY');
    scan('(?<first>a)(b)');
    findReplaceAll(ed.view(), '$1-$2', false);
    assert.equal(md(), 'XXb-aYY');
  });
});

describe('ZeroWidthMatchesInsert', () => {
  // The source view replaces an empty match by inserting at it — that is how "^"
  // with "> " prefixes every line, pinned by FindEngineTests.ReplaceAllOfAnEmptyMatchInserts.
  // This view used to skip empty matches outright, so the same query did nothing at
  // all (#5 F-5).
  test('a lookahead is indexed at each position and Replace All inserts there', () => {
    load('cat one cat');
    assert.equal(scan('(?=cat)').total, 2);
    const r = findReplaceAll(ed.view(), 'X', true);
    assert.equal(r.replaced, 2);
    assert.equal(md(), 'Xcat one Xcat');
  });

  test('^ inserts at the start of the text, the way it does in the source view', () => {
    // The index this view searches is the document's text with no line breaks
    // between blocks, so ^ is the start of that text and matches once. HELP says so.
    load('abc');
    assert.equal(scan('^', 'gm').total, 1);
    assert.equal(findReplaceAll(ed.view(), 'X', true).replaced, 1);
    assert.equal(md(), 'Xabc');
  });

  test('the highlight on a zero-width match is a caret, not a run of text', () => {
    load('cat');
    scan('(?=cat)');
    assert.deepEqual(findNext(true), { total: 1, current: 1 });
    assert.equal(highlighted(), '');
  });

  test('Replace on a zero-width match moves past it instead of finding it again', () => {
    load('cat one cat');
    scan('(?=cat)');
    findNext(true);
    const r = findReplace(ed.view(), 'X', true, true);
    assert.equal(r.replaced, 1);
    assert.equal(md(), 'Xcat one cat');
    // Two matches again — before the X and before the second cat — and the cursor is
    // on the SECOND. Resuming at the replacement's end would land on the first again
    // and Replace would insert for ever, which is why the source view resumes
    // strictly after an empty match.
    assert.deepEqual({ total: r.total, current: r.current }, { total: 2, current: 2 });
  });
});

describe('WhatTheHostAcceptsThisViewCanRun', () => {
  // The host's FindEngine.Build is the gate: it refuses, before either view sees it,
  // any pattern the two engines would read differently. What it lets through has to
  // compile here — with the unicode flag find.js always adds — or the formatted view
  // silently finds nothing where the source view finds matches (#5 F-6).
  for (const row of constructTable.accepted) {
    test(`accepted: ${row.pattern}`, () => {
      load('cat one');
      assert.equal(scan(row.pattern).error, undefined, `${row.pattern} — ${row.why}`);
    });
  }

  for (const row of constructTable.literals) {
    test(`literal ${row.mode}: ${JSON.stringify(row.query)}`, () => {
      // The pattern the host's escaper writes for a literal query. .NET's own
      // Regex.Escape wrote "\ " for a space, which the unicode flag rejects outright.
      load('cat one');
      assert.equal(scan(row.pattern).error, undefined, `${row.pattern} — ${row.why || ''}`);
    });
  }

  // The refused half of the same table. It was written but never read, and reading
  // it showed the note's claim — "a refused row must be one JavaScript will not
  // compile" — was false of four rows: EITHER engine can be the one that refuses,
  // and two of them are refused by the host alone (#5 NF-6). Each row now says
  // which, and this is the half a browser engine can answer for.
  for (const row of constructTable.refused) {
    test(`refused: ${row.pattern}`, () => {
      load('cat one');
      const refusedHere = scan(row.pattern).error === 'Invalid pattern';
      if (row.refusedBy === 'javascript')
        assert.ok(refusedHere, `${row.pattern} should not compile here — ${row.why}`);
      else if (row.refusedBy === 'dotnet')
        assert.ok(!refusedHere,
          `${row.pattern} is JavaScript's own and compiles here; only the host refuses it — ${row.why}`);
      else
        // 'host': both engines may compile it, and FindEngineTests is the only place
        // the refusal can be proved. Nothing to measure here beyond the row being read.
        assert.equal(row.refusedBy, 'host', `${row.pattern} — unknown refusedBy`);
    });
  }

  test('every refused row says which engine refuses it', () => {
    // Without this a new row with no refusedBy would take the 'host' branch above and
    // assert nothing at all.
    assert.ok(constructTable.refused.length >= 20);
    for (const row of constructTable.refused)
      assert.ok(['javascript', 'dotnet', 'host'].includes(row.refusedBy),
        `${row.pattern}: refusedBy is ${JSON.stringify(row.refusedBy)}`);
  });

  test('the unicode flag is on whether or not the host asked for it', () => {
    load('café and cat');
    // \p{L} is a Unicode category only under the unicode flag; without it the escape
    // is read as a literal 'p' and the search silently means something else.
    assert.equal(scan('\\p{L}+', 'g').total > 0, true);
    findClear();
    // …and \A, which .NET has and JavaScript does not, is an error rather than a
    // literal 'A' match. (The host refuses it earlier; this is the second gate.)
    assert.deepEqual(scan('\\Acat', 'g'), { total: 0, current: 0, error: 'Invalid pattern' });
  });

  test('a pattern this engine will not compile is reported, not counted as no matches', () => {
    for (const p of ['(?i)cat', '(?>ab)c', '(?#note)cat', "(?'n'a)"]) {
      findClear();
      load('cat');
      assert.deepEqual(scan(p), { total: 0, current: 0, error: 'Invalid pattern' }, p);
    }
  });
});

describe('MalformedPatternReplacesNothing', () => {
  test('a pattern that does not compile indexes nothing, so Replace and Replace All apply nothing', () => {
    load('cat (unclosed');
    const r0 = scan('(unclosed');
    assert.equal(r0.error, 'Invalid pattern');
    assert.equal(r0.total, 0);
    assert.deepEqual(findReplaceAll(ed.view(), 'x', false), { replaced: 0, skipped: 0, moved: 0, total: 0, inSelection: false });
    assert.deepEqual(findReplace(ed.view(), 'x', false, true), { replaced: 0, skipped: 0, total: 0, current: 0 });
    assert.equal(md(), 'cat (unclosed');
  });
});
