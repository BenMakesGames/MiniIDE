using System.Collections.Generic;

namespace MiniIde.Models;

/// <summary>A single consistent read of <see cref="Services.SolutionWatcher"/>'s since-start counters plus its
/// live state. One record assembled under the watcher's own lock rather than a property per counter, so a
/// reader can't see a torn view where the numbers disagree with each other across a repaint.
///
/// <para>Counters are cumulative since app start; "watch this one burst" is the reader's job (subtract a
/// baseline), not the service's.</para></summary>
/// <param name="IsWatching">Whether a watch is live right now. Distinguishes "no solution open" and "the watch
/// failed to start" from "the feed is simply quiet" — today those look identical from outside.</param>
/// <param name="Root">The directory the watch covers, or null when nothing is watched.</param>
/// <param name="EventsSeen">Raw OS events, before any filtering.</param>
/// <param name="EventsPruned">Events dropped for living under an <see cref="IdeDirectories.Pruned"/> segment
/// (a build writing <c>obj/</c>). A high count next to a flat <paramref name="SignalsRaised"/> is the pruning
/// optimization earning its keep.</param>
/// <param name="EventsMuted">Events dropped as the app's own writes (the rename apply).</param>
/// <param name="SignalsRaised">Debounced bursts actually raised. Compare against
/// <paramref name="EventsSeen"/> to see coalescing working.</param>
/// <param name="Overflows">Times the OS reported dropped events. Non-zero is the answer to "does the fallback
/// poll earn its keep?"</param>
public record WatchStats(
    bool IsWatching,
    string? Root,
    long EventsSeen,
    long EventsPruned,
    long EventsMuted,
    long SignalsRaised,
    long Overflows);

/// <summary>A single consistent read of <see cref="Services.WorkspaceService"/>'s since-start reconcile
/// counters. The funnel (<paramref name="DocumentsStatted"/> → <paramref name="DocumentsRead"/> →
/// <paramref name="DocumentsForked"/>) is the reconcile's whole thesis made observable: read/stat is the stamp
/// gate's payoff, fork/read is the content gate's.
///
/// <para>Increments happen in funnel order, so <c>Read &lt;= Statted</c> and <c>Forked &lt;= Read</c> hold for
/// any read of this record.</para></summary>
/// <param name="DocumentsStatted">Documents offered to the overlay (past the file-path guard) — each costs a
/// <c>stat</c>.</param>
/// <param name="DocumentsRead">Documents whose stamp moved, so they were read in full.</param>
/// <param name="DocumentsForked">Documents whose content genuinely differed, so the snapshot forked. The
/// expensive one — a fork busts the cached compilation.</param>
/// <param name="Drains">Reconciles that drained the pending-set (the O(changed) path).</param>
/// <param name="FullRescans">Reconciles that fell back to the whole-solution pass (cold start, structural
/// change, overflow, or no watcher at all).</param>
/// <param name="StructuralReloads">Full <c>MSBuildWorkspace</c> teardowns + rebuilds. Its own number rather
/// than an inference, because it is by far the most expensive event here.</param>
public record ReconcileStats(
    long DocumentsStatted,
    long DocumentsRead,
    long DocumentsForked,
    long Drains,
    long FullRescans,
    long StructuralReloads);

/// <summary>What the reconcile currently *owes* — the live half of the two-track design, as opposed to the
/// cumulative counters. Read under the dirty-track's own lock; <paramref name="PendingPaths"/> is a copy,
/// never the live set, so a reader cannot mutate a dirty track by observing it.</summary>
/// <param name="PendingPaths">Paths the watcher reported changed that no reconcile has drained yet.</param>
/// <param name="NeedsFullRescan">Whether the next reconcile will take the whole-solution fallback.</param>
public record DiskDirtyState(IReadOnlyList<string> PendingPaths, bool NeedsFullRescan);
