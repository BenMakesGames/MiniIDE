using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MiniIde.Models;

namespace MiniIde.Services;

/// <summary>The OS file-change feed for the open solution: a <see cref="FileSystemWatcher"/> over the solution
/// root, pruned to <see cref="IdeDirectories.Pruned"/> and debounced into a coalesced
/// <see cref="DiskChangeSignal"/>. It replaces polling as the *trigger* for reconciliation — external tools
/// own the writes under the read-only law, and this is how the view learns of them without re-reading the
/// solution on every focus.
///
/// <para><b>Best-effort, by design of the OS.</b> The watcher's internal buffer can overrun under a burst (an
/// agent rewriting hundreds of files, a <c>git checkout</c>) and events can be missed under load. That is why
/// <see cref="DiskChangeSignal.Overflow"/> exists and why <see cref="WorkspaceService"/> keeps its
/// whole-solution pass: this service makes that pass rare, it does not make it unnecessary. Anything built on
/// top must stay correct if a signal never arrives.</para>
///
/// <para><b>Threading</b>: events arrive on threadpool threads and <see cref="Changed"/> is raised on one
/// (from the debounce timer) — never the UI thread. Subscribers marshal what they must.</para></summary>
public sealed class SolutionWatcher : IDisposable
{
    /// <summary>How long a burst is coalesced before signalling. Agents and git write in bursts; without this
    /// a hundred-file rewrite would be a hundred reconciles. Short enough to feel live on a single save.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the app's own writes stay muted after <see cref="MuteWrites"/>. Generous, because it
    /// must outlast the OS's delivery lag plus <see cref="Debounce"/>; bounded, because a *later* external
    /// edit to the same file must still be caught. Expiry, not consumption, is the release: one write can
    /// raise several <c>Changed</c> events, so a consume-once mute would leak the second one through.</summary>
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _muted = new(StringComparer.OrdinalIgnoreCase);
    private bool _structural;
    private bool _overflow;
    private StructuralReason? _reason;

    // Counted before _gate is taken, on threadpool threads — hence Interlocked. Moving them inside the lock
    // would drag the prune check in with them and defeat the very optimization the pruned count measures.
    private long _eventsSeen;
    private long _eventsPruned;

    // Counted where the work already happens under _gate, so plain increments; read back under the same lock.
    private long _eventsMuted;
    private long _signalsRaised;
    private long _overflows;

    // Written by Start/Stop, read from IsPruned on threadpool threads and from Snapshot on the UI thread.
    private volatile FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private volatile string? _root;

    /// <summary>Raised once per debounced burst, on a threadpool thread.</summary>
    public event Action<DiskChangeSignal>? Changed;

    /// <summary>Starts watching the directory containing <paramref name="solutionPath"/>, replacing any
    /// previous watch. Returns whether the watch is actually live — <c>false</c> means the caller must keep
    /// polling (see <see cref="WorkspaceService.DiskIsWatched"/>); it never throws, because a solution that
    /// can't be watched should still open.</summary>
    public bool Start(string solutionPath)
    {
        Stop();
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        if (root is null || !Directory.Exists(root)) return false;

        try
        {
            _root = root;
            _debounce = new Timer(Fire, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                // Size alongside LastWrite so a same-mtime resize still reports; FileName/DirectoryName are
                // what make create/delete/rename (the structural cases) observable at all.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
                // Well above the 8 KB default: every extra KB is headroom against the overflow path, and the
                // recovery from overflow (a full rescan) is far more expensive than the buffer.
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => Record(e.FullPath, kind: null);
            watcher.Created += (_, e) => Record(e.FullPath, StructuralKind.Created);
            watcher.Deleted += (_, e) => Record(e.FullPath, StructuralKind.Deleted);
            watcher.Renamed += (_, e) =>
            {
                Record(e.OldFullPath, StructuralKind.Renamed);
                Record(e.FullPath, StructuralKind.Renamed);
            };
            watcher.Error += (_, _) => SignalOverflow();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
        _root = null;
        lock (_gate)
        {
            _paths.Clear();
            _muted.Clear();
            _structural = false;
            _overflow = false;
            _reason = null;
        }
    }

    /// <summary>A single consistent read of the counters and the live state, for observation only. Cumulative
    /// since app start — a reader wanting "just this burst" subtracts a baseline of its own, which keeps this
    /// service ignorant of who is watching and why.</summary>
    public WatchStats Snapshot()
    {
        lock (_gate)
            return new WatchStats(
                _watcher is not null,
                _root,
                Interlocked.Read(ref _eventsSeen),
                Interlocked.Read(ref _eventsPruned),
                _eventsMuted,
                _signalsRaised,
                _overflows);
    }

    /// <summary>Mutes watcher-driven reconciliation for <paramref name="paths"/> — the app's own writes.
    /// Call it <em>before</em> the write. The one caller is the rename apply, which does its own explicit
    /// post-apply refresh; letting the watcher also react would be redundant work racing that refresh.
    ///
    /// <para>Deliberately not a blanket first-party mute: NuGet's <c>.csproj</c> write is a structural change
    /// the watcher <em>should</em> catch, because nothing else tells the workspace to rebuild. Only writes
    /// that refresh the view themselves belong here.</para></summary>
    public void MuteWrites(IEnumerable<string> paths)
    {
        var until = DateTime.UtcNow + SelfWriteWindow;
        lock (_gate)
            foreach (var p in paths) _muted[Path.GetFullPath(p)] = until;
    }

    // `kind` is the structural classification the OS event carries, or null for a plain content change.
    private void Record(string fullPath, StructuralKind? kind)
    {
        Interlocked.Increment(ref _eventsSeen);
        if (IsPruned(fullPath)) { Interlocked.Increment(ref _eventsPruned); return; }
        lock (_gate)
        {
            if (IsMuted(fullPath)) { _eventsMuted++; return; }
            _paths.Add(fullPath);
            // A project file's *content* is structural even though nothing was created or deleted: it decides
            // what compiles, so an overlay can't express it (WorkspaceService fingerprints .csproj by hash for
            // the same reason).
            if ((kind ?? (IsProjectFile(fullPath) ? StructuralKind.ProjectChanged : null)) is { } structural)
            {
                _structural = true;
                // First cause wins: later events in the burst don't overwrite it. What collapsed the tree is
                // the thing that made the burst structural, not whatever happened to land last.
                _reason ??= new StructuralReason(fullPath, structural);
            }
        }
        Schedule();
    }

    private void SignalOverflow()
    {
        lock (_gate) { _overflow = true; _overflows++; }
        Schedule();
    }

    // Trailing-edge debounce: each event pushes the deadline out, so a burst fires once after it settles.
    // An in-flight event can land after Stop disposed the timer — that signal is moot, so swallow the race
    // rather than throw out of a threadpool callback (which would take the process down).
    private void Schedule()
    {
        try { _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Fire(object? _)
    {
        DiskChangeSignal signal;
        lock (_gate)
        {
            if (_paths.Count == 0 && !_overflow) return;
            // Stamped here, not in Record: the signal's identity is the burst, and the burst ends here.
            signal = new DiskChangeSignal(_paths.ToArray(), _structural, _overflow, DateTime.UtcNow, _reason);
            _signalsRaised++;
            _paths.Clear();
            _structural = false;
            _overflow = false;
            _reason = null;
        }
        Changed?.Invoke(signal);
    }

    // FileSystemWatcher has no subtree exclusion, so pruning is ours: bin/obj churn during a build would
    // otherwise be a reconcile storm. Matches the tree walk and the manifest fingerprint, which prune the
    // same set — a path they never look at must never trigger a reconcile either.
    private bool IsPruned(string fullPath)
    {
        var root = _root;
        if (root is null) return true;
        var relative = Path.GetRelativePath(root, fullPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (IdeDirectories.Pruned.Contains(segment)) return true;
        return false;
    }

    private static bool IsProjectFile(string path) =>
        Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);

    // Assumes _gate is held. Self-cleaning: an expired entry is dropped on the first event that consults it,
    // which is exactly when its file is being touched again.
    private bool IsMuted(string fullPath)
    {
        if (!_muted.TryGetValue(fullPath, out var until)) return false;
        if (DateTime.UtcNow <= until) return true;
        _muted.Remove(fullPath);
        return false;
    }

    public void Dispose() => Stop();
}
