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

    /// <summary>
    /// Whether a <c>fileDrop</c> message that has just arrived is older than one
    /// already handled, and so must be ignored.
    /// </summary>
    /// <param name="newestDrop">The highest drop number handled so far; 0 before any
    /// (the editor's counter starts at 1).</param>
    /// <param name="arrivingDrop">The drop number on the message that just arrived.</param>
    public static bool IsSuperseded(long newestDrop, long arrivingDrop) => arrivingDrop < newestDrop;

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
    /// </summary>
    public static TimeSpan ReadTimeout(long bytesRequested) =>
        TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(Math.Max(0, bytesRequested) / (8.0 * 1024 * 1024));

    /// <summary>What the status line says when the editor never answered: not about
    /// one file — the message may never have crossed the bridge — and it names the
    /// way round it.</summary>
    public const string TimedOutNotice = "Couldn't read the dropped file(s) — try Insert ▸ Picture.";

    /// <summary>The status line for files the drop chose and could not get: the
    /// wording the document route already used, extended to name more than one.
    /// </summary>
    public static string UnreadableNotice(IReadOnlyList<string> names) =>
        $"Couldn't read {string.Join(", ", names)}.";
}
