using System;
using System.Collections.Generic;

namespace MiniIde.Models;

/// <summary>One consistent read of <see cref="Services.SolutionWatcher"/>'s counters and recent bursts, taken
/// under the watcher's lock so the numbers can't disagree with each other across a repaint. Counters are
/// cumulative since app start; "watch this one burst" is the reader's job (subtract a baseline).</summary>
/// <param name="Root">The directory being watched, or null when nothing is — no solution open, or the watch
/// failed to start (the list then won't follow disk until Reload solution).</param>
/// <param name="EventsSeen">Raw OS events, before filtering.</param>
/// <param name="EventsRelevant">Events on a file the project list reads. Compare against
/// <paramref name="EventsSeen"/> to see the filter working, and against <paramref name="Reloads"/> to see the
/// debounce coalescing.</param>
/// <param name="Reloads">Debounced bursts raised as <c>Changed</c> — each one is a project-list reload.</param>
/// <param name="Overflows">Times the OS reported dropped events.</param>
/// <param name="RecentBursts">The last few bursts, newest first.</param>
public record WatchStats(
    string? Root,
    long EventsSeen,
    long EventsRelevant,
    long Reloads,
    long Overflows,
    IReadOnlyList<WatchBurst> RecentBursts);

/// <summary>One debounced burst that became a reload: what was touched, and whether the OS dropped events.</summary>
/// <param name="Sequence">The burst's 1-based number since app start — equal to <see cref="WatchStats.Reloads"/>
/// at the moment it was raised.</param>
/// <param name="RaisedUtc">When the debounce settled and the reload was raised.</param>
/// <param name="Paths">The relevant files touched in the burst. Empty for an overflow-only burst.</param>
/// <param name="Overflow">The OS dropped events during the burst, so <paramref name="Paths"/> may be
/// incomplete.</param>
public record WatchBurst(long Sequence, DateTime RaisedUtc, IReadOnlyList<string> Paths, bool Overflow);
