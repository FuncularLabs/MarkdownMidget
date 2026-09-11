using System;
using System.Collections.Generic;
using System.Linq;

namespace MarkdownMidget;

/// <summary>What the host does with one <c>droppedFileBytes</c> answer.</summary>
internal enum DropReply
{
    /// <summary>Every index the host asked about came back with bytes: embed or open
    /// them.</summary>
    Apply,

    /// <summary>The answer is about the read being waited on, but at least one file
    /// could not be read. NONE of them go in — the all-or-nothing a failed path read
    /// has always had — and <see cref="DropReplyDecision.Missing"/> names which.
    /// </summary>
    Refuse,

    /// <summary>No answer came back inside <see cref="DropHandshake.ReadTimeout"/>.
    /// Refuse, but for a reason the user can act on rather than one about a
    /// particular file — the message may never have crossed the bridge at all.
    /// </summary>
    TimedOut,

    /// <summary>Not about the read being waited on at all: a straggler from a
    /// superseded drop, a second answer to one already taken, or an answer that named
    /// no drop. Nothing happens, and nothing is said — the drop that owns the window
    /// speaks for it.</summary>
    Discard,
}

/// <summary>
/// What one <c>droppedFileBytes</c> answer amounts to.
/// </summary>
/// <param name="Outcome">Apply, Refuse, TimedOut or Discard.</param>
/// <param name="Bytes">The bytes of each index asked about; empty unless
/// <paramref name="Outcome"/> is Apply.</param>
/// <param name="Missing">The indices asked about that came back null, malformed, or
/// not at all — indices into the drop's own file list, so the status line can name
/// them. Empty unless <paramref name="Outcome"/> is Refuse or TimedOut; on TimedOut
/// it is every index asked for, since nothing was heard about any of them.</param>
internal sealed record DropReplyDecision(
    DropReply Outcome,
    IReadOnlyDictionary<int, byte[]> Bytes,
    IReadOnlyList<int> Missing);

/// <summary>
/// The generation token of the two-phase drop handshake (issue #6), as pure
/// decisions the window only wires up.
///
/// The editor numbers every drop. The number travels out with the <c>fileDrop</c>
/// message, back in on the host's request for bytes, and out again with the answer,
/// so a second drop landing while the first is still being read cannot be answered
/// with the wrong files. These decisions used to live inline in MainWindow, where
/// nothing could test them, and they were wrong:
///
/// <list type="bullet">
/// <item>A <c>fileDrop</c> message is not necessarily in drop order. Reading 20
/// heads takes longer than reading one, so a 20-file drop 1 can post AFTER a 1-file
/// drop 2. Handling drop 1 then cancelled drop 2's read and waited on an editor
/// whose counter had already moved — which answers all-null. The newest drop was
/// silently abandoned and the superseded one raised the error.
/// (<see cref="IsSuperseded"/>.)</item>
/// <item>A reply is acted on only when it is about the read actually outstanding.
/// (<see cref="Decide"/>.)</item>
/// <item>And the wait for a reply is bounded, because the editor's promise to answer
/// is one the bridge can break without telling either side.
/// (<see cref="ReadTimeout"/>.)</item>
/// </list>
/// </summary>
internal static class DropHandshake
{
    /// <summary>The decision for a drop whose plan wants no bytes at all (every file
    /// refused, or a picture the window cannot take): apply nothing and refuse
    /// nothing, so the drop's own status line is what the user sees.</summary>
    public static readonly DropReplyDecision NothingToRead =
        new(DropReply.Apply, new Dictionary<int, byte[]>(), []);

    /// <summary>The decision that touches nothing and says nothing: a read the window
    /// has abandoned, and every answer that is not about the read outstanding.</summary>
    public static readonly DropReplyDecision Discarded =
        new(DropReply.Discard, new Dictionary<int, byte[]>(), []);

    /// <summary>What the status line says when a drop is abandoned because the user
    /// dropped again before it finished. It used to say nothing at all.</summary>
    public const string SupersededNotice = "A newer drop replaced this one; nothing from it was inserted.";

    /// <summary>What the status line says when the DOCUMENT moved while the drop was
    /// being read — a different file opened, the document closed, Read Only turned
    /// on, a save or reload that reset the baseline, or Ctrl+E, which moves the
    /// insertion to the other view. Not "a newer drop replaced this one": no newer
    /// drop exists, the window the drop was routed for simply stopped being the
    /// window on screen. Names opening as well as inserting, because the content
    /// route reads a dropped DOCUMENT's bytes through the same wait.</summary>
    public const string DocumentChangedNotice =
        "The document changed while the drop was being read; nothing from it was inserted or opened.";

    /// <summary>
    /// Whether a drop plan may still be applied to the document, after the wait for
    /// the bytes it needs.
    ///
    /// A plan is made from <c>DropTargetNow()</c> BEFORE the request for bytes goes
    /// out and applied AFTER the answer comes back — up to
    /// <see cref="ReadTimeout"/> later, with no busy overlay over the window. Every
    /// menu is live in that gap: File ▸ Open, File ▸ New, File ▸ Close, Edit ▸ Read
    /// Only, and a drop on the toolbar. Applying a stale plan then put the picture
    /// into whatever document had arrived in the meantime, or — with nothing testing
    /// <c>_closed</c> on the insert path — into a closed document, which the user
    /// cannot see and which is now dirty.
    ///
    /// Four things are compared, and they are the four the plan depended on:
    ///
    /// <list type="bullet">
    /// <item>The path. A different file (or an untitled document where a file was,
    /// or the reverse) is not the document that was routed.</item>
    /// <item>The clean baseline, BY REFERENCE, the same pin
    /// <c>HandleExternalChangeAsync</c> takes across its own awaits: a reassignment
    /// to identical text is still a movement, and a value comparison would miss it.
    /// <c>SetCleanBaselineAsync</c> is the writer that takes the trouble to build a
    /// fresh instance, and an open or a reload goes through it. A SAVE does not —
    /// <c>SaveToPathAsync</c> assigns <c>_cleanMarkdown</c> directly, as do the
    /// encrypt and convert paths — and neither does a Keep, which is
    /// <c>AcceptDiskAsBaseline</c> assigning <c>disk.Text</c>; both are fresh
    /// instances because of where the string came from, not because anything makes
    /// them so. And for an EMPTY document none of them is: <c>""</c> is interned, so
    /// every route hands back the same <c>string.Empty</c> and a reassignment is
    /// invisible here (<c>AnEmptyDocumentsBaselineMovesWithoutTheReferencePinSeeingIt</c>
    /// pins it). The other three comparisons still guard that case, and an empty
    /// document saved to its own path is the same document at the same path in the
    /// same view, so it is left as it is rather than given a counter of its
    /// own.</item>
    /// <item>What the window can take. Read Only turned on mid-wait, or the document
    /// closed, both change this and both mean the insert must not happen.</item>
    /// <item>Which VIEW is on screen. <c>InsertMarkdownFragment</c> routes by
    /// <c>_sourceMode</c>, and <c>SetSourceModeAsync</c> yields twice with
    /// <c>_sourceMode</c> still at its old value, so a drop continuation landing in
    /// either gap inserted into the half of the window that was about to be
    /// discarded — into a hidden source box on the way out, or into the WYSIWYG
    /// document on the way in, where <c>SourceBox.Text = latest</c> then overwrote
    /// it with markdown fetched before the insertion. Nothing was said either
    /// time.</item>
    /// </list>
    ///
    /// A window that became MORE permissive mid-wait fails too, and deliberately: a
    /// plan made against a read-only window set its pictures aside as "not inserted"
    /// and already said so in the status line, so letting them in afterwards would
    /// contradict the notice the user just read.
    /// </summary>
    public static bool StillApplies(
        string? pathThen, string? pathNow,
        string? cleanThen, string? cleanNow,
        DropTarget targetThen, DropTarget targetNow,
        bool sourceThen, bool sourceNow) =>
        string.Equals(pathThen, pathNow, StringComparison.Ordinal)
        && ReferenceEquals(cleanThen, cleanNow)
        && targetThen == targetNow
        && sourceThen == sourceNow;

    /// <summary>
    /// Whether a <c>fileDrop</c> message that has just arrived is older than one
    /// already handled, and so must be ignored.
    /// </summary>
    /// <param name="newestDrop">The highest drop number handled so far; 0 before any
    /// (the editor's counter starts at 1).</param>
    /// <param name="arrivingDrop">The drop number on the message that just arrived.</param>
    public static bool IsSuperseded(long newestDrop, long arrivingDrop) => arrivingDrop < newestDrop;

    /// <summary>
    /// Whether a drop number is one the editor issued, and so one whose answer can
    /// be matched to a request.
    ///
    /// The counter starts at 1. 0 is what <c>ParseMessage</c> reports for a message
    /// that did not say which drop it is — and <see cref="Decide"/> reads 0 as "no
    /// read outstanding", which is exactly what makes a duplicate answer harmless.
    /// The other side of that: a request made under drop 0 could never be matched
    /// either, so waiting for its answer only ever ends in <see cref="ReadTimeout"/>.
    /// The caller refuses it up front instead.
    /// </summary>
    public static bool CanBeAnswered(long drop) => drop > 0;

    /// <summary>
    /// Whether the drop that claimed <paramref name="generationThen"/> is still the
    /// drop the window belongs to.
    ///
    /// This is not <see cref="IsSuperseded"/>, which is about the EDITOR's counter
    /// and one route. This counter is the host's, and both surfaces bump it as they
    /// start, because the abandon was one-directional: a drop on either surface ends
    /// the CONTENT route's outstanding read (there is a waiter to complete), and
    /// neither ends the PATH route's <c>DropFiles.ReadAllAsync</c> — nothing is
    /// waiting on it, an await in <c>Window_Drop</c> simply carries on. Two drops
    /// then inserted into the same document, in read order, and the document pin
    /// could not tell: both were about the SAME document, so path, baseline, target
    /// and view all matched.
    ///
    /// Identity, not order. The counter only goes up in practice, but the question
    /// is "is this still the number I claimed" — letting a higher one through would
    /// be a second way into a drop that has already been replaced.
    /// </summary>
    public static bool StillTheCurrentDrop(long generationThen, long generationNow) =>
        generationThen == generationNow;

    /// <summary>
    /// What to do with one answer.
    /// </summary>
    /// <param name="outstandingDrop">The drop whose read is being waited on, or 0
    /// when none is — which is also what the host leaves behind once an answer has
    /// been taken, so a duplicate matches nothing.</param>
    /// <param name="replyDrop">The drop the answer says it is about; 0 when it did
    /// not say, which matches no read.</param>
    /// <param name="requested">The indices the host asked for, as indices into the
    /// drop's file list.</param>
    /// <param name="reply">The parsed answer.</param>
    public static DropReplyDecision Decide(
        long outstandingDrop, long replyDrop, IReadOnlyList<int> requested, DropBytesMessage reply)
    {
        // 0 is no read and no drop; anything else has to match exactly. Both halves
        // matter: a straggler about an older drop, and a second answer about the one
        // already taken, arrive as ordinary messages and look identical from here.
        if (outstandingDrop <= 0 || replyDrop != outstandingDrop) return Discarded;

        var bytes = new Dictionary<int, byte[]>();
        var missing = new List<int>();
        // Only the indices asked about: bytes for anything else are not an extra
        // insertion. Distinct, so a duplicated request is one decision, not two.
        foreach (var index in requested.Distinct())
        {
            // Absent and null are the same answer — "could not be read" — and an
            // empty array is not: a zero-length file was read, and is refused later
            // for not sniffing as a picture rather than here.
            if (reply.Bytes.TryGetValue(index, out var read) && read is not null) bytes[index] = read;
            else missing.Add(index);
        }

        return missing.Count > 0
            ? new DropReplyDecision(DropReply.Refuse, new Dictionary<int, byte[]>(), missing)
            : new DropReplyDecision(DropReply.Apply, bytes, []);
    }

    /// <summary>The decision for a read that cannot even be asked for — no WebView,
    /// so no answer will ever come. Said at once rather than waited for.</summary>
    public static DropReplyDecision Unreadable(IReadOnlyList<int> requested) =>
        new(DropReply.Refuse, new Dictionary<int, byte[]>(), [.. requested.Distinct()]);

    /// <summary>The decision for a read that never answered inside
    /// <see cref="ReadTimeout"/>.</summary>
    public static DropReplyDecision TimedOut(IReadOnlyList<int> requested) =>
        new(DropReply.TimedOut, new Dictionary<int, byte[]>(), [.. requested.Distinct()]);

    /// <summary>
    /// How many bytes a phase-two request is asking the editor to produce, which is
    /// what <see cref="ReadTimeout"/> is scaled by.
    /// </summary>
    /// <param name="files">The drop, in drop order.</param>
    /// <param name="indices">The indices the host chose; one that is not in the drop
    /// contributes nothing rather than throwing, and a duplicate counts once.</param>
    public static long BytesRequested(IReadOnlyList<DroppedFile> files, IReadOnlyList<int> indices)
    {
        long total = 0;
        foreach (var index in indices.Distinct())
        {
            if (index < 0 || index >= files.Count) continue;
            var size = files[index].Size;
            // -1 is "the message did not say". The editor always sends File.size, so
            // this is the malformed case — and a field that went missing must not
            // SHORTEN the wait for a file that could be anything up to the ceiling.
            total += size < 0 ? DropRouting.MaxPictureBytes : size;
        }
        return total;
    }

    /// <summary>
    /// How long to wait for the editor's answer before giving the window back:
    /// <b>10 seconds plus one second for every 8 MB asked for</b> (so 10 s for a
    /// drop of nothing, 18 s at the 64 MB picture ceiling, which is the most one
    /// picture can ask for).
    ///
    /// The wait exists because "the editor always answers" is not a promise the
    /// editor can keep. The answer crosses the WebView2 bridge as ONE message — about
    /// 85 MB of base64 at the ceiling — and a post that the bridge refuses is a post
    /// the host never hears about. It posts a small failure message when it can (see
    /// postAnswer in file-drop.js), but if the bridge itself is gone nothing there
    /// helps, and an unbounded await left the window waiting forever with nothing on
    /// screen to say why.
    ///
    /// The shape is grace plus throughput, not a flat number: 8 MB/s is far slower
    /// than any disk, so a big picture behind a slow read is never cut off, while a
    /// bridge that has stopped answering costs a pause rather than a hang.
    ///
    /// And clamped at <b>ten minutes</b>, because the total is not bounded by
    /// anything. Only PICTURES have a ceiling (<see cref="DropRouting.MaxPictureBytes"/>);
    /// a dropped document is opened, and File ▸ Open has never capped what it opens,
    /// so a <c>fileDrop</c> message stating a document of <c>long.MaxValue</c> bytes
    /// reaches <see cref="BytesRequested"/> unchanged. Unclamped, that is past
    /// TimeSpan's own range, and the OverflowException lands inside
    /// <c>async void HandleDroppedFiles</c>, where nothing catches it: a malformed
    /// message would take the process down. Ten minutes is far above any ORDINARY
    /// drop — ten pictures at the ceiling is 640 MB, which is 90 s — but it is not
    /// out of honest reach: the ceiling is per picture and nothing caps the count,
    /// so about 74 pictures at it get there for real. That is a mis-drag rather
    /// than a malformed message, and the clamp is what bounds both.
    /// </summary>
    public static TimeSpan ReadTimeout(long bytesRequested) =>
        TimeSpan.FromSeconds(Math.Min(600, 10 + Math.Max(0, bytesRequested) / (8.0 * 1024 * 1024)));

    /// <summary>What the status line says when the editor never answered: not about
    /// one file — the message may never have crossed the bridge — and it names the
    /// way round it.</summary>
    public const string TimedOutNotice = "Couldn't read the dropped file(s) — try Insert ▸ Picture.";

    /// <summary>The status line for files the drop chose and could not get.
    ///
    /// New wording, not a reused one: nothing at 2e47107 produced
    /// "Couldn't read &lt;name&gt;." — the nearest thing was Insert ▸ Picture's modal
    /// "Couldn't read the image:", and the document route's failure was
    /// "Couldn't open &lt;name&gt;:". It borrows that opening on purpose, so a dropped
    /// picture and a picked one fail in the same voice, and it names every file
    /// because a drop can choose more than one.
    /// </summary>
    public static string UnreadableNotice(IReadOnlyList<string> names) =>
        $"Couldn't read {string.Join(", ", names)}.";
}
