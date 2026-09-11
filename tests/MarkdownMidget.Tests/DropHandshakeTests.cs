using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The two-phase drop handshake's generation token, on the host side (issue #6).
///
/// The numbering of review findings restarted with each round, so they are
/// described here rather than cited: this file was created for the round that
/// found the out-of-order phase-one message and the drop that wanted no bytes, and
/// grew with the rounds after it — the bounded wait and its clamp, the
/// re-validation of a plan against the document, the view and the drop generation.
///
/// The editor numbers each drop; the number travels out with the fileDrop message,
/// back in on the host's request for bytes, and out again with the answer. Every
/// decision that number drives used to live inline in MainWindow, where nothing
/// could reach it without a window, a WebView2 and a real mouse — so nothing did,
/// and two ways of getting it wrong shipped: an out-of-order phase-one message
/// inverted the token (a 20-file drop's message can arrive AFTER a 1-file drop's,
/// because reading 20 heads takes longer), and a drop that wanted no bytes left the
/// previous drop's read outstanding to be answered all-null and reported as a
/// failure. Both decisions are pure functions here now; MainWindow keeps the wiring.
/// </summary>
public class DropHandshakeTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0];

    /// <summary>A droppedFileBytes answer, built from real JSON through the real
    /// parser — so an entry these tests describe is one the editor could actually
    /// send, not a dictionary hand-made to suit the assertion.</summary>
    private static DropBytesMessage Answer(long drop, params (int Index, byte[]? Bytes)[] files)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "droppedFileBytes",
            drop,
            files = System.Array.ConvertAll(files, f => new
            {
                index = f.Index,
                base64 = f.Bytes is null ? null : System.Convert.ToBase64String(f.Bytes),
            }),
        });
        using var doc = JsonDocument.Parse(json);
        return DropRouting.ParseBytesMessage(doc.RootElement);
    }

    // ===== which fileDrop message wins =====

    [Fact]
    public void AFileDropMessageOlderThanOneAlreadyHandledIsSuperseded()
    {
        // The finding. readHeads is asynchronous: 20 files take longer to read 32
        // bytes of each than one file does, so drop 1's fileDrop can arrive AFTER
        // drop 2's. Handling it would cancel drop 2's read (the newest drop, the one
        // the user is looking at) and then wait on an editor whose counter has moved
        // past drop 1 — which answers all-null. Newest drop silently abandoned,
        // superseded one raising the error.
        Assert.True(DropHandshake.IsSuperseded(newestDrop: 2, arrivingDrop: 1));
        Assert.True(DropHandshake.IsSuperseded(newestDrop: 9, arrivingDrop: 8));
    }

    [Fact]
    public void ANewerFileDropMessageIsHandledAndSoIsTheFirstOne()
    {
        Assert.False(DropHandshake.IsSuperseded(newestDrop: 1, arrivingDrop: 2));
        // Nothing handled yet: the mark starts at 0 and the editor's counter at 1.
        Assert.False(DropHandshake.IsSuperseded(newestDrop: 0, arrivingDrop: 1));
        // AR-6, and the reason MainWindow returns the mark to 0 on "loaded":
        // dropSeq is a module variable in the page, so a page reload restarts it and
        // the next drop is numbered 1 again. A mark left where the old page finished
        // makes that drop — and every drop after it, for the life of the window —
        // look older than one already handled.
        Assert.True(DropHandshake.IsSuperseded(newestDrop: 7, arrivingDrop: 1));
        // A repeat of the drop being handled is not OLDER, so it is not swallowed
        // here — it is the reply path that refuses a second answer.
        Assert.False(DropHandshake.IsSuperseded(newestDrop: 4, arrivingDrop: 4));
    }

    // ===== which droppedFileBytes answer is acted on =====

    [Fact]
    public void AnAnswerAboutTheReadBeingWaitedOnIsApplied()
    {
        var decision = DropHandshake.Decide(
            outstandingDrop: 5, replyDrop: 5, requested: [2, 0], reply: Answer(5, (2, Png), (0, Jpeg)));

        Assert.Equal(DropReply.Apply, decision.Outcome);
        Assert.Empty(decision.Missing);
        Assert.Equal(Png, decision.Bytes[2]);
        Assert.Equal(Jpeg, decision.Bytes[0]);
    }

    [Fact]
    public void AnAnswerAboutAnEarlierDropIsDiscarded()
    {
        // The straggler: drop 4's read was abandoned, drop 5's is outstanding, and
        // drop 4's answer turns up anyway. Acting on it would insert drop 4's files
        // into the document drop 5 is about to change.
        var decision = DropHandshake.Decide(
            outstandingDrop: 5, replyDrop: 4, requested: [0], reply: Answer(4, (0, Png)));

        Assert.Equal(DropReply.Discard, decision.Outcome);
        Assert.Empty(decision.Bytes);
    }

    [Fact]
    public void ASecondAnswerToAReadAlreadyTakenIsDiscarded()
    {
        // The first answer clears the outstanding drop to 0, and 0 is a drop number
        // the editor never issues (its counter starts at 1) — so a duplicate matches
        // nothing and inserts nothing a second time.
        var decision = DropHandshake.Decide(
            outstandingDrop: 0, replyDrop: 5, requested: [0], reply: Answer(5, (0, Png)));

        Assert.Equal(DropReply.Discard, decision.Outcome);
    }

    [Fact]
    public void AnAnswerAboutNoDropAtAllIsDiscarded()
    {
        // What the parser reports when a message did not say which drop it is: 0,
        // which matches no outstanding read even when one is waiting.
        Assert.Equal(DropReply.Discard, DropHandshake.Decide(5, 0, [0], Answer(0, (0, Png))).Outcome);
    }

    // ===== all or nothing =====

    [Fact]
    public void ANullForOneOfSeveralPicturesRefusesAllOfThem()
    {
        // A failed path read has always inserted NONE of a drop's pictures rather
        // than some of them, and the editor route matches it: half a drop in the
        // document is worse than a status line naming what failed.
        var decision = DropHandshake.Decide(
            outstandingDrop: 3, replyDrop: 3, requested: [0, 1, 2], reply: Answer(3, (0, Png), (1, null), (2, Jpeg)));

        Assert.Equal(DropReply.Refuse, decision.Outcome);
        Assert.Equal([1], decision.Missing);
        Assert.Empty(decision.Bytes);
    }

    [Fact]
    public void AnIndexTheAnswerNeverMentionsIsMissingRatherThanEmpty()
    {
        // Not the same as base64:null, and treated the same way: a file the host
        // asked about and got no word on is not a file it may embed.
        var decision = DropHandshake.Decide(
            outstandingDrop: 3, replyDrop: 3, requested: [0, 7], reply: Answer(3, (0, Png)));

        Assert.Equal(DropReply.Refuse, decision.Outcome);
        Assert.Equal([7], decision.Missing);
    }

    [Fact]
    public void AnAnswerWithNoFilesAtAllRefusesEveryIndexAsked()
    {
        // What the editor posts when its own post of the bytes failed — an oversized
        // payload, a bridge fault — as {files: null}. The host has to hear "no", not
        // wait.
        using var doc = JsonDocument.Parse("{\"type\":\"droppedFileBytes\",\"drop\":3,\"files\":null,\"error\":\"post-failed\"}");
        var decision = DropHandshake.Decide(3, 3, [0, 1], DropRouting.ParseBytesMessage(doc.RootElement));

        Assert.Equal(DropReply.Refuse, decision.Outcome);
        Assert.Equal([0, 1], decision.Missing);
    }

    [Fact]
    public void AnEmptyFileIsReadNotMissing()
    {
        // Zero bytes is an answer. It will not sniff as a picture and is refused
        // later for that reason, but the handshake is not where it goes wrong.
        var decision = DropHandshake.Decide(3, 3, [0], Answer(3, (0, [])));

        Assert.Equal(DropReply.Apply, decision.Outcome);
        Assert.Empty(decision.Bytes[0]);
    }

    // ===== the request itself =====

    [Fact]
    public void BytesTheHostNeverAskedForAreIgnored()
    {
        // The answer is trusted for the indices asked about and no further: an extra
        // entry is not an extra insertion.
        var decision = DropHandshake.Decide(
            outstandingDrop: 3, replyDrop: 3, requested: [0], reply: Answer(3, (0, Png), (1, Jpeg), (99, Jpeg)));

        Assert.Equal(DropReply.Apply, decision.Outcome);
        Assert.Equal(0, Assert.Single(decision.Bytes).Key);
    }

    [Fact]
    public void ADuplicatedRequestedIndexIsDecidedOnce()
    {
        var decision = DropHandshake.Decide(3, 3, [1, 1, 1], Answer(3, (1, Png)));
        Assert.Equal(DropReply.Apply, decision.Outcome);
        Assert.Equal(1, Assert.Single(decision.Bytes).Key);

        var refused = DropHandshake.Decide(3, 3, [1, 1], Answer(3, (1, null)));
        Assert.Equal(DropReply.Refuse, refused.Outcome);
        Assert.Equal([1], refused.Missing);
    }

    // ===== a drop that wants nothing still ends the previous read =====

    [Fact]
    public void ADropThatWantsNoBytesNeedsNoRoundTrip()
    {
        // Drop a photo, then quickly a .zip. The .zip's plan chooses nothing, so
        // there is no request to make — but the photo's read must still be over by
        // then, which is the caller's job (AbandonDroppedRead), not a reason to skip
        // it. What this pins is the decision handed back for an empty request: apply
        // nothing, refuse nothing, so the drop's own status line is what the user
        // sees rather than "couldn't read".
        Assert.Equal(DropReply.Apply, DropHandshake.NothingToRead.Outcome);
        Assert.Empty(DropHandshake.NothingToRead.Bytes);
        Assert.Empty(DropHandshake.NothingToRead.Missing);
    }

    [Fact]
    public void AnAbandonedReadEndsInTheDecisionThatDoesNothing()
    {
        // What the window hands the waiter when the user drops again: a decision it
        // returns on, not a cancellation it has to catch. The catch was how the
        // newest drop came to be abandoned in silence.
        Assert.Equal(DropReply.Discard, DropHandshake.Discarded.Outcome);
        Assert.Empty(DropHandshake.Discarded.Bytes);
        Assert.Empty(DropHandshake.Discarded.Missing);
    }

    [Fact]
    public void ARequestForADropTheEditorNeverNumberedIsNotWorthWaitingFor()
    {
        // Found reviewing this round's own fix layer. Decide now treats 0 as "no read
        // outstanding", which is what makes a duplicate answer harmless — but it also
        // means a request made UNDER drop 0 can never be matched: the answer comes
        // back carrying the same 0 and is discarded, and the drop sits there until the
        // timeout. 0 is what the parser reports for a fileDrop that did not say which
        // drop it is, so the request is refused up front instead.
        Assert.True(DropHandshake.CanBeAnswered(1));
        Assert.False(DropHandshake.CanBeAnswered(0));
        Assert.False(DropHandshake.CanBeAnswered(-3));
        // The reason, in the same test: an answer to such a request is discarded.
        Assert.Equal(DropReply.Discard, DropHandshake.Decide(0, 0, [0], Answer(0, (0, Png))).Outcome);
    }

    [Fact]
    public void AReadThatCannotEvenBeAskedForIsRefusedByName()
    {
        // No WebView, so no answer will ever come: say so at once rather than wait
        // for a message nothing will send.
        var decision = DropHandshake.Unreadable([0, 2]);
        Assert.Equal(DropReply.Refuse, decision.Outcome);
        Assert.Equal([0, 2], decision.Missing);
    }

    // ===== NF-3: the wait is bounded =====

    [Fact]
    public void TheWaitIsTenSecondsPlusASecondForEveryEightMegabytesAskedFor()
    {
        // The editor claims to answer always, and cannot quite promise it: the answer
        // crosses the bridge as ONE message — about 85 MB of base64 at the 64 MB
        // ceiling — and a post that fails there is a post the host never hears about.
        // So the wait is bounded, and the bound scales with what was asked for: long
        // enough that a big picture off a slow disk is not cut off, short enough to be
        // a pause rather than a hang.
        Assert.Equal(TimeSpan.FromSeconds(10), DropHandshake.ReadTimeout(0));
        Assert.Equal(TimeSpan.FromSeconds(11), DropHandshake.ReadTimeout(8 * 1024 * 1024));
        Assert.Equal(TimeSpan.FromSeconds(18), DropHandshake.ReadTimeout(64 * 1024 * 1024));
        // At the ceiling the wait is 18 s, and that is the longest it can be for one
        // picture — the ceiling is what stops it growing without limit.
        Assert.Equal(TimeSpan.FromSeconds(18), DropHandshake.ReadTimeout(DropRouting.MaxPictureBytes));
        // A nonsense total does not shorten it below the floor.
        Assert.Equal(TimeSpan.FromSeconds(10), DropHandshake.ReadTimeout(-500));
    }

    [Fact]
    public void TheWaitIsClampedSoNoStatedSizeCanOverflowIt()
    {
        // AR-3. Only PICTURES have a ceiling — a dropped document is opened, and
        // File ▸ Open has never capped what it opens — so BytesRequested can return
        // any long a fileDrop message cares to state. TimeSpan.FromSeconds of
        // long.MaxValue / 8 MB is past TimeSpan's own range, and the OverflowException
        // is thrown inside `async void HandleDroppedFiles`, where nothing catches it:
        // a malformed message took the process down.
        Assert.Equal(TimeSpan.FromSeconds(600), DropHandshake.ReadTimeout(long.MaxValue));

        // Ten pictures at the 64 MB ceiling is 640 MB, which is 90 s — nowhere near
        // ten minutes.
        Assert.Equal(TimeSpan.FromSeconds(90), DropHandshake.ReadTimeout(10 * DropRouting.MaxPictureBytes));

        // But 640 MB is NOT "the largest total the routing can actually produce"
        // (NF-5): the ceiling is per picture, and nothing caps how many are dropped
        // at once. The clamp is reachable honestly, and this is where — 73 pictures
        // at the ceiling stays inside it, 74 reaches it. Said with the real number
        // rather than "no picture could reach it", which was simply untrue.
        Assert.True(DropHandshake.ReadTimeout(73 * DropRouting.MaxPictureBytes) < TimeSpan.FromSeconds(600));
        Assert.Equal(TimeSpan.FromSeconds(600), DropHandshake.ReadTimeout(74 * DropRouting.MaxPictureBytes));

        // Which is the point of the clamp, not an argument against it: a drop of 74
        // pictures is a mis-drag, and the wait it buys is bounded either way.
        Assert.Equal(TimeSpan.FromSeconds(594), DropHandshake.ReadTimeout(73 * DropRouting.MaxPictureBytes));
    }

    [Fact]
    public void TheWaitIsSizedFromTheFilesActuallyAskedFor()
    {
        var files = new List<DroppedFile>
        {
            new("small.png", Png, 8 * 1024 * 1024),
            new("skip.png", Png, 64 * 1024 * 1024),
            new("also.png", Png, 8 * 1024 * 1024),
        };
        // Only the chosen indices count, and a duplicate counts once.
        Assert.Equal(16L * 1024 * 1024, DropHandshake.BytesRequested(files, [0, 2, 2]));
        // An index that is not in the drop contributes nothing rather than throwing.
        Assert.Equal(8L * 1024 * 1024, DropHandshake.BytesRequested(files, [0, 9, -1]));
        Assert.Equal(0, DropHandshake.BytesRequested(files, []));
    }

    [Fact]
    public void AFileWhoseSizeTheDropNeverStatedIsAssumedToBeTheLargestAllowed()
    {
        // -1 is "the message did not say". The editor always sends File.size, so this
        // is the malformed case — and a missing field must not SHORTEN the wait for a
        // file that could be anything up to the ceiling.
        var files = new List<DroppedFile> { new("mystery.png", Png) };
        Assert.Equal(DropRouting.MaxPictureBytes, DropHandshake.BytesRequested(files, [0]));
    }

    [Fact]
    public void AReadThatNeverAnswersEndsInAVisibleOutcome()
    {
        // The finding: `await pending.Task` was unbounded, and main.js's claim that
        // readDroppedFiles "ALWAYS posts exactly once" was not one postToHost could
        // keep — it swallowed every failure in a bare catch. A drop that ends this way
        // says so and gives the user the way round it.
        var decision = DropHandshake.TimedOut([0, 1, 1]);
        Assert.Equal(DropReply.TimedOut, decision.Outcome);
        Assert.Equal([0, 1], decision.Missing);
        Assert.Empty(decision.Bytes);
        Assert.Equal("Couldn't read the dropped file(s) — try Insert ▸ Picture.", DropHandshake.TimedOutNotice);
    }

    // ===== AR-2: the plan is re-validated against the document after the wait =====
    //
    // The plan is made from DropTargetNow() BEFORE the request for bytes goes out
    // and applied AFTER it comes back — a gap of up to ReadTimeout (10–90 s), with
    // no busy overlay over it. Everything the user can reach in that gap changes
    // what the plan was decided against: File ▸ Open, File ▸ New, File ▸ Close,
    // Edit ▸ Read Only, Ctrl+E, and a drop on the toolbar. So the four things the
    // plan depended on are pinned before the request and compared after it, an
    // extension of the pin HandleExternalChangeAsync takes across its own awaits.

    [Fact]
    public void APlanStillAppliesWhenNothingAboutTheDocumentMoved()
    {
        var clean = new string("# Notes".AsSpan());
        Assert.True(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.Editable, 0, 0));
        // An untitled document has no path, and two nulls are the same document.
        Assert.True(DropHandshake.StillApplies(
            null, null, clean, clean, DropTarget.Editable, DropTarget.Editable, 0, 0));
        // The source view is a document state like any other, and a drop that
        // started there and is still there applies: the view counter has not moved,
        // whatever it happens to stand at.
        Assert.True(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.Editable, 7, 7));
    }

    [Fact]
    public void AViewSwitchedWhileTheDropWasBeingReadTakesNothing()
    {
        // NF-2. The pin held the path, the baseline and the target, and NOT which
        // view was on screen — so Ctrl+E during the wait was invisible to it and
        // the insertion went ahead into the wrong half of the window, silently:
        //
        //   entering source: SetSourceModeAsync awaits TryGetDocumentMarkdownAsync
        //   with _sourceMode still false, so the fragment went into the WYSIWYG
        //   document — and was then overwritten by `SourceBox.Text = latest`, which
        //   is the markdown fetched BEFORE the insertion.
        //
        //   leaving source: it awaits SetDocumentMarkdownAsync(SourceBox.Text) and
        //   then getMarkdown() with _sourceMode still true, so the fragment went
        //   into the source box — which is hidden a few lines later and never read
        //   again.
        //
        // Either way the picture was gone with nothing said.
        //
        // F-A, round 5: the first fix pinned _sourceMode ITSELF, which cannot see
        // either of the two cases above while they are HAPPENING — that is the whole
        // point of the paragraphs above, and the flag does not turn over until the
        // bottom of the method. A continuation landing in either gap read the same
        // bool on both sides and the pin passed. So what is pinned is
        // _viewGeneration, bumped at the top of SetSourceModeAsync before its first
        // await: a pin taken at g refuses at g+1 (the switch is in flight, the flag
        // has not moved yet) and at g+2 (a full round trip out and back, which ends
        // in the view it started in and which the flag also cannot see).
        var clean = new string("# Notes".AsSpan());
        const long g = 4;
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.Editable, viewThen: g, viewNow: g + 1));
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.Editable, viewThen: g, viewNow: g + 2));
    }

    [Fact]
    public void ADocumentClosedWhileTheDropWasBeingReadTakesNothing()
    {
        // The case nothing guarded: File ▸ Close during the wait leaves _closed
        // true, and InsertMarkdownFragment tests _readOnly but not _closed — so the
        // fragment went into the collapsed WebView and marked a closed document
        // dirty. DropTargetNow() reports NoDocument for exactly that state, so the
        // target half of the pin is what catches it.
        var clean = new string("# Notes".AsSpan());
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.NoDocument, 0, 0));
    }

    [Fact]
    public void AWindowMadeReadOnlyWhileTheDropWasBeingReadTakesNothing()
    {
        // InsertMarkdownFragment returns silently in a read-only window, so the
        // picture vanished with nothing said. Now the drop says so.
        var clean = new string("# Notes".AsSpan());
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.Editable, DropTarget.ReadOnly, 0, 0));
        // And the other way round: a window that became editable mid-wait is not the
        // window the plan was made for either — that plan set the picture aside as
        // "not inserted", and its notice has already said so.
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", clean, clean,
            DropTarget.ReadOnly, DropTarget.Editable, 0, 0));
    }

    [Fact]
    public void ADifferentFileOpenedWhileTheDropWasBeingReadDoesNotReceiveIt()
    {
        var clean = new string("# Notes".AsSpan());
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\other.md", clean, clean,
            DropTarget.Editable, DropTarget.Editable, 0, 0));
        // A file opened where there was an untitled document, and a document that
        // lost its path, are both a different document from the one routed.
        Assert.False(DropHandshake.StillApplies(
            null, @"C:\a\notes.md", clean, clean, DropTarget.Editable, DropTarget.Editable, 0, 0));
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", null, clean, clean, DropTarget.Editable, DropTarget.Editable, 0, 0));
    }

    [Fact]
    public void TheSameFileReloadedToIdenticalTextIsStillAChange()
    {
        // The baseline is pinned by REFERENCE, exactly as HandleExternalChangeAsync
        // pins it, so a reassignment to identical text is still a movement and a
        // value comparison would miss it.
        //
        // Which reassignments those are, named honestly (NF-4): SetCleanBaselineAsync
        // is the one that takes the trouble to build a fresh instance, and it is
        // reached by an OPEN and by a RELOAD (LoadDocumentAsync, so both the external
        // -change Reload and the auto-reload). A SAVE does not call it —
        // SaveToPathAsync assigns _cleanMarkdown = markdown directly, as do the
        // encrypt and convert paths — and neither does a Keep, which is
        // AcceptDiskAsBaseline assigning disk.Text. Those two are fresh instances in
        // practice, because the string came from a fresh deserialise or a fresh read,
        // not because anything makes them so.
        var then = new string("# Notes".AsSpan());
        var now = new string("# Notes".AsSpan());
        Assert.Equal(then, now);
        Assert.False(ReferenceEquals(then, now));
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", then, now,
            DropTarget.Editable, DropTarget.Editable, 0, 0));
    }

    [Fact]
    public void AnEmptyDocumentsBaselineMovesWithoutTheReferencePinSeeingIt()
    {
        // NF-4, the exception the comments claimed did not exist. "" is interned, and
        // every route that produces a baseline hands back that one instance: the
        // editor's answer arrives through JsonSerializer.Deserialize<string>
        // (RunEditorAsync), a disk read through GetString, and even
        // SetCleanBaselineAsync's own fresh-instance fallback is
        // new string(_cleanMarkdown.AsSpan()), which returns string.Empty for an
        // empty span.
        Assert.Same(string.Empty, JsonSerializer.Deserialize<string>("\"\""));
        Assert.Same(string.Empty, JsonDocument.Parse("\"\"").RootElement.GetString());
        Assert.Same(string.Empty, new string(string.Empty.AsSpan()));

        // So for an EMPTY document the baseline half of the pin is blind: a save to
        // the existing path, or a reload, moves it and StillApplies still says yes.
        Assert.True(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md",
            JsonSerializer.Deserialize<string>("\"\""), new string(string.Empty.AsSpan()),
            DropTarget.Editable, DropTarget.Editable, 0, 0));

        // Deliberately not given a BASELINE counter of its own: the other three
        // comparisons still guard the insertion, and an empty document saved to its own
        // path is the same document at the same path in the same view — the picture
        // goes where the user is looking. (The VIEW half is a counter, for a different
        // reason: _sourceMode does not move until a switch has finished, so the flag
        // could not see the switch while it was happening at all. An empty baseline
        // that is reassigned to "" has genuinely not moved.) This test exists so the
        // comments and HELP cannot drift back into promising a guarantee this wide.
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\other.md", string.Empty, string.Empty,
            DropTarget.Editable, DropTarget.Editable, 0, 0));
        Assert.False(DropHandshake.StillApplies(
            @"C:\a\notes.md", @"C:\a\notes.md", string.Empty, string.Empty,
            DropTarget.Editable, DropTarget.ReadOnly, 0, 0));
    }

    [Fact]
    public void ADropThatStartedWhileThisOneWasReadingOwnsTheWindowInstead()
    {
        // NF-7. The abandon was one-directional. A drop on the formatted view calls
        // AbandonDroppedRead, which ends the CONTENT route's outstanding read — and
        // a drop on the window does the same. Neither ends the PATH route's
        // DropFiles.ReadAllAsync: there is no waiter to complete, just an await in
        // Window_Drop that keeps going. So a picture dropped on the formatted view
        // while a toolbar drop's file was still coming off disk left both drops
        // inserting into the same document, in read order.
        //
        // The document pin cannot see this: both drops are about the SAME document,
        // so path, baseline, target and view all match. What separates them is only
        // which drop is current, so both routes now claim a generation as they start
        // and the insertion chokepoint checks it.
        //
        // Identity, not order: the counter only goes up in practice, but "not the
        // number I claimed" is the whole question — a comparison that let a higher
        // number through would be a second way to insert into a replaced drop.
        Assert.True(DropHandshake.StillTheCurrentDrop(1, 1));
        Assert.False(DropHandshake.StillTheCurrentDrop(1, 2));
        Assert.False(DropHandshake.StillTheCurrentDrop(2, 1));
    }

    [Fact]
    public void TheInsertionChokepointSaysWhetherTheDropStillOwnsTheWindow()
    {
        // NF-3. The two routes disagreed about what a STALE drop tells the user.
        //
        // The content route's stale check is an early return in HandleDroppedFiles,
        // so DocumentChangedNotice is the last thing flashed and it stays.
        //
        // The path route's is inside InsertDroppedPicturesAsync, which returned void
        // — so Window_Drop carried on regardless: it flashed DocumentChangedNotice
        // and then, one statement later, flashed plan.Notice() over the top of it.
        // FlashStatus ASSIGNS, so the user of a stale drop of photo.png + a.zip was
        // told "Not a picture or a markdown file: a.zip" and never saw that the
        // pictures had been abandoned. It also went on to open documents and
        // Activate() against the same stale plan.
        //
        // So the chokepoint reports, and both callers return on false. This pins the
        // reporting half — the signature, which was Task before the fix — and the
        // exact pair of strings that collided. What it cannot pin is that the
        // callers honour it: Window_Drop and HandleDroppedFiles are UI members with
        // no seam, and that half is stated by reading.
        var insertion = typeof(MainWindow).GetMethod(
            "InsertDroppedPicturesAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(insertion);
        Assert.Equal(typeof(Task<bool>), insertion!.ReturnType);

        var plan = DropRouting.Plan(
            [new DroppedFile("photo.png", Png, 4), new DroppedFile("a.zip", [0x50, 0x4B, 0x03, 0x04], 4)],
            DropTarget.Editable, oneDocument: false);
        Assert.Equal([0], plan.Insert.Select(p => p.Index));
        Assert.Equal("Not a picture or a markdown file: a.zip", plan.Notice());
        Assert.NotEqual(DropHandshake.DocumentChangedNotice, plan.Notice());
    }

    // ===== what the user is told =====

    [Fact]
    public void EveryAbandonedReadHasSomethingToSay()
    {
        // The finding's other half: a superseded drop used to end in a bare return
        // with nothing on screen, and the drop it superseded raised a modal
        // "Couldn't read the image". Both are status lines now, and both name what
        // happened.
        //
        // "inserted or opened", for the same reason DocumentChangedNotice says it
        // (F-C, round 5): the supersede check moved to the ENTRY of the insertion
        // chokepoint, ahead of the empty-plan return, so a drop whose plan only
        // OPENS a document — a dropped .md, on either surface — is abandoned through
        // this notice too. "Nothing from it was inserted" was then what a user was
        // told when what the drop lost was the document it was going to open.
        Assert.Equal("A newer drop replaced this one; nothing from it was inserted or opened.",
            DropHandshake.SupersededNotice);
        // And the one for a document that moved under the read rather than a drop
        // that replaced it: it names both things the drop could have done, because
        // the content route reads a document's bytes through the same wait.
        Assert.Equal("The document changed while the drop was being read; nothing from it was inserted or opened.",
            DropHandshake.DocumentChangedNotice);
        Assert.Equal("Couldn't read photo.png.", DropHandshake.UnreadableNotice(["photo.png"]));
        Assert.Equal("Couldn't read a.png, b.png.", DropHandshake.UnreadableNotice(["a.png", "b.png"]));
    }

    [Fact]
    public void TheNoticeForOneUnreadableFileNamesThatFileAndNothingElse()
    {
        // Renamed in the truth sweep. This was called
        // ...IsTheOneTheOpenPathAlreadyUsed, and said "the document route said
        // exactly this before the pictures shared it". It did not: at 2e47107
        // nothing in src/ or tests/ produced "Couldn't read <name>." at all — the
        // document route's failure was a modal "Couldn't open <name>:" and
        // Insert ▸ Picture's was "Couldn't read the image:". The wording is new and
        // borrows Insert ▸ Picture's opening on purpose, so a dropped picture and a
        // picked one fail in the same voice.
        Assert.Equal("Couldn't read notes.md.", DropHandshake.UnreadableNotice(["notes.md"]));
        // One file, one name: no list punctuation for a list of one.
        Assert.DoesNotContain(",", DropHandshake.UnreadableNotice(["notes.md"]));
    }

    [Fact]
    public void ARefusedDecisionCarriesTheIndicesItsNoticeNeeds()
    {
        // The names come from the drop's file list, so Missing has to be indices
        // into it — the same indices the request used.
        var files = new List<DroppedFile> { new("a.png", Png, 4), new("b.png", null, 4) };
        var decision = DropHandshake.Decide(1, 1, [0, 1], Answer(1, (0, Png), (1, null)));

        Assert.Equal([1], decision.Missing);
        Assert.Equal("Couldn't read b.png.", DropHandshake.UnreadableNotice([.. decision.Missing.Select(i => files[i].Name)]));
    }
}
