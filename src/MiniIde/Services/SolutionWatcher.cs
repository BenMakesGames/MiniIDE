using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MiniIde.Models;

namespace MiniIde.Services;

/// <summary>Tells the project list when it may be out of date. Watches only the handful of files the list is
/// built from — the solution file, project files, launch settings, and MiniIDE's own <c>.miniide/</c> settings
/// (stars) — and raises one debounced
/// <see cref="Changed"/> per burst. It carries no paths: the response is always "reload the list", which is
/// cheap and idempotent, so there is nothing for a consumer to triage.
///
/// <para>Source files are deliberately not watched. Editor tabs revalidate on activation and window focus, and
/// the Roslyn snapshot reconciles against disk before every semantic query; neither needs a feed.</para>
///
/// <para>What it saw is readable through <see cref="Snapshot"/>, for the file-watcher diagnostics tab. That is
/// observation only — nothing about <see cref="Changed"/> depends on it.</para>
///
/// <para><b>Threading</b>: <see cref="Changed"/> is raised on a threadpool thread (from the debounce timer).
/// Subscribers marshal what they must.</para></summary>
public sealed class SolutionWatcher : IDisposable
{
    /// <summary>How long a burst is coalesced. A <c>git checkout</c> or an agent touching several project files
    /// should be one reload, not one per file.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Bounded, drop-oldest: an agent churning project files would otherwise grow this forever.</summary>
    private const int RecentBurstCapacity = 50;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    // Everything below is written from FileSystemWatcher and timer threads and read from the UI thread.
    private readonly Lock _gate = new();
    private string? _root;
    private long _eventsSeen;
    private long _eventsRelevant;
    private long _reloads;
    private long _overflows;
    private readonly HashSet<string> _burstPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _burstOverflow;
    private readonly LinkedList<WatchBurst> _recentBursts = new();

    /// <summary>Raised once per debounced burst of relevant changes, on a threadpool thread.</summary>
    public event Action? Changed;

    /// <summary>Starts watching the directory containing <paramref name="solutionPath"/>, replacing any previous
    /// watch. Returns whether the watch is live; it never throws, because a solution that can't be watched should
    /// still open (its list just won't follow disk until it's reloaded).</summary>
    public bool Start(string solutionPath)
    {
        Stop();
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        if (root is null || !Directory.Exists(root)) return false;

        try
        {
            _debounce = new Timer(_ => Raise(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, e) => Record(e.FullPath);
            watcher.Created += (_, e) => Record(e.FullPath);
            watcher.Deleted += (_, e) => Record(e.FullPath);
            // Either end of a rename can be the relevant one: an atomic save writes a temp file and renames it
            // over the real name.
            watcher.Renamed += (_, e) => Record(e.OldFullPath, e.FullPath);
            // The OS dropped events; one of them may have mattered. A reload is cheap, so just do one.
            watcher.Error += (_, _) => RecordOverflow();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            lock (_gate) _root = root;
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
        lock (_gate)
        {
            _root = null;
            _burstPaths.Clear();
            _burstOverflow = false;
        }
    }

    /// <summary>A consistent copy of the counters and recent bursts. Never the live collections.</summary>
    public WatchStats Snapshot()
    {
        lock (_gate)
            return new WatchStats(_root, _eventsSeen, _eventsRelevant, _reloads, _overflows, _recentBursts.ToList());
    }

    /// <summary>Whether a change to <paramref name="path"/> can alter the project list: membership (the solution
    /// file), a project's kind or packages (its project file), its launch profiles, or its star (a settings file
    /// in <c>.miniide/</c>, e.g. edited by hand or by another MiniIDE window).</summary>
    public static bool IsRelevant(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase)
               || (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(Path.GetDirectoryName(path)) is { } folder
                   && folder.Equals(LocalSolutionSettings.FolderName, StringComparison.OrdinalIgnoreCase));
    }

    // One OS event, which for a rename carries two paths. Counted once either way.
    private void Record(params string[] paths)
    {
        var relevant = paths.Where(IsRelevant).ToList();
        lock (_gate)
        {
            _eventsSeen++;
            if (relevant.Count == 0) return;
            _eventsRelevant++;
            _burstPaths.UnionWith(relevant);
        }
        Schedule();
    }

    private void RecordOverflow()
    {
        lock (_gate)
        {
            _overflows++;
            _burstOverflow = true;
        }
        Schedule();
    }

    // Trailing-edge debounce: each event pushes the deadline out, so a burst fires once after it settles. An
    // in-flight event can land after Stop disposed the timer — that signal is moot, so swallow the race rather
    // than throw out of a threadpool callback.
    private void Schedule()
    {
        try { _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void Raise()
    {
        lock (_gate)
        {
            _reloads++;
            var paths = _burstPaths.Order(StringComparer.OrdinalIgnoreCase).ToList();
            _recentBursts.AddFirst(new WatchBurst(_reloads, DateTime.UtcNow, paths, _burstOverflow));
            while (_recentBursts.Count > RecentBurstCapacity) _recentBursts.RemoveLast();
            _burstPaths.Clear();
            _burstOverflow = false;
        }
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
