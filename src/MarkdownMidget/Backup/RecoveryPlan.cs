using System.Collections.Generic;
using System.Linq;

namespace MarkdownMidget.Backup;

/// <summary>What to do with the snapshots a crashed session left behind.</summary>
internal sealed record RecoveryPlan(
    BackupSnapshot? Here,
    IReadOnlyList<BackupSnapshot> Elsewhere,
    IReadOnlyList<BackupSnapshot> GivenUp)
{
    /// <summary>
    /// After this many attempts a snapshot stops being restored automatically. A
    /// document that crashes the app while loading would otherwise be handed back on
    /// every launch, crashing it every time — the app becomes unusable and the file
    /// is what's doing it. The snapshot stays on disk; we just stop force-feeding it.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>
    /// The launching window takes one snapshot if it isn't already showing something
    /// (a file on the command line, say); the rest open in their own windows, which
    /// is where they came from.
    /// </summary>
    public static RecoveryPlan Decide(IReadOnlyList<BackupSnapshot> orphans, bool windowIsFree)
    {
        // Only the ones we haven't already reported: past the limit they are still
        // kept on disk, but the user has been told once and doesn't need telling
        // again every time they open the app.
        var givenUp = orphans.Where(o => o.RecoveryAttempts >= MaxAttempts && !o.GiveUpReported).ToList();
        var usable = orphans.Where(o => o.RecoveryAttempts < MaxAttempts).ToList();

        if (usable.Count == 0) return new RecoveryPlan(null, [], givenUp);
        if (!windowIsFree) return new RecoveryPlan(null, usable, givenUp);
        return new RecoveryPlan(usable[0], usable.Skip(1).ToList(), givenUp);
    }

    /// <summary>Everything this plan will actually put in front of the user.</summary>
    public int RestoreCount => (Here is null ? 0 : 1) + Elsewhere.Count;
}
