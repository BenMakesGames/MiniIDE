using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>The Disk panel: the OS-driven reconcile made visible. Read-only observation — it changes no
/// watcher or reconcile behavior, and nothing in <c>Services/</c> knows it exists. All the aggregation, ratio
/// maths, and formatting live here; the services do increments plus one snapshot read.
///
/// <para><b>Two different clocks, on purpose.</b> The counter pull is timer-gated to tab selection — a 1 s
/// repaint for a panel nobody is looking at is pure background cost. The signal log is <em>not</em>: its
/// subscription is live for the app's lifetime, because tying it to selection would mean tabbing away during
/// an agent burst and losing exactly the signals you wanted to see.</para></summary>
public partial class DiskInsightViewModel : ViewModelBase
{
    /// <summary>Bounded, drop-oldest: an agent rewriting a repo would otherwise grow this forever.</summary>
    private const int LogCapacity = 50;

    /// <summary>Fast enough to feel live, slow enough to be free.</summary>
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(1);

    private static readonly WatchStats NoWatchBaseline = new(false, null, 0, 0, 0, 0, 0);
    private static readonly ReconcileStats NoReconcileBaseline = new(0, 0, 0, 0, 0, 0);

    private readonly WorkspaceService _workspace;
    private readonly SolutionWatcher _watcher;
    private readonly Func<IEnumerable<EditorTabViewModel>> _editorTabs;
    private readonly DispatcherTimer _timer;

    // Reset is a baseline the *panel* subtracts, not a mutation of the services' counters. It keeps the debug
    // panel from being able to write to a service at all, and it can't race a running reconcile.
    private WatchStats _watchBaseline = NoWatchBaseline;
    private ReconcileStats _reconcileBaseline = NoReconcileBaseline;

    // ── Watch state ───────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isWatching;
    [ObservableProperty] private string _watchStatus = "Not watching";

    // ── Feed counters ─────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private long _eventsSeen;
    [ObservableProperty] private long _eventsPruned;
    [ObservableProperty] private long _eventsMuted;
    [ObservableProperty] private long _signalsRaised;
    [ObservableProperty] private long _overflows;

    // ── Reconcile funnel + modes ──────────────────────────────────────────────────────────────────────
    [ObservableProperty] private long _documentsStatted;
    [ObservableProperty] private long _documentsRead;
    [ObservableProperty] private long _documentsForked;
    [ObservableProperty] private string _readRatio = "—";
    [ObservableProperty] private string _forkRatio = "—";
    [ObservableProperty] private long _drains;
    [ObservableProperty] private long _fullRescans;
    [ObservableProperty] private long _structuralReloads;

    // ── Live dirty state ──────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _pendingPaths = "(none)";
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _needsFullRescan;
    [ObservableProperty] private string _staleTabs = "(no open editor tabs)";

    /// <summary>The signal log, newest first. Bounded to <see cref="LogCapacity"/>.</summary>
    public ObservableCollection<DiskSignalRow> Signals { get; } = new();

    /// <param name="editorTabs">Enumerates the open editor tabs. Injected as a callback, mirroring
    /// <see cref="ProblemsViewModel"/>, so this panel can read the per-tab half of the two dirty tracks without
    /// knowing anything about how tabs are kept.</param>
    public DiskInsightViewModel(
        WorkspaceService workspace,
        SolutionWatcher watcher,
        Func<IEnumerable<EditorTabViewModel>> editorTabs)
    {
        _workspace = workspace;
        _watcher = watcher;
        _editorTabs = editorTabs;

        // Subscribed for the app's lifetime, deliberately independent of tab selection (see the class remarks)
        // and of MainWindowViewModel's own Changed subscriber, which owns the dirty tracks. This one only reads.
        _watcher.Changed += OnSignal;

        // Default ctor, then Start on selection: the (interval, priority, handler) overload starts the timer
        // immediately, which would poll for a panel that has never been shown.
        _timer = new DispatcherTimer { Interval = PullInterval };
        _timer.Tick += (_, _) => Pull();
    }

    /// <summary>Begins the counter pull, and pulls once immediately so the panel isn't blank for a second.
    /// Called when the Disk tab becomes selected.</summary>
    public void StartPolling()
    {
        Pull();
        _timer.Start();
    }

    /// <summary>Stops the counter pull. The signal log keeps accumulating regardless — that is the point of
    /// it being event-driven.</summary>
    public void StopPolling() => _timer.Stop();

    /// <summary>Rebases every counter to now and clears the log, so the next thing you do is the only thing on
    /// screen. That "watch this one burst" framing is most of the panel's diagnostic value.</summary>
    [RelayCommand]
    private void Reset()
    {
        _watchBaseline = _watcher.Snapshot();
        _reconcileBaseline = _workspace.Stats();
        Signals.Clear();
        Pull();
    }

    /// <summary>Reads both services' snapshots plus the stale-tab list into the bound properties.
    /// <see cref="WorkspaceService.DiskIsWatched"/> and the watch root are not observable, so the panel learns
    /// them here rather than by binding to them and waiting for a change that never notifies.</summary>
    public void Pull()
    {
        var watch = _watcher.Snapshot();
        var reconcile = _workspace.Stats();
        var dirty = _workspace.Dirty();

        IsWatching = watch.IsWatching;
        WatchStatus = watch switch
        {
            { IsWatching: true, Root: { } root } => $"Watching {root}",
            // The silent degradation this panel exists to surface: Start returned false, DiskIsWatched stayed
            // false, and the app has been full-rescan polling ever since with nothing on screen to say so.
            { IsWatching: false } when _workspace.IsLoaded => "Not watching — the reconcile is full-rescan polling",
            _ => "Not watching",
        };

        EventsSeen = watch.EventsSeen - _watchBaseline.EventsSeen;
        EventsPruned = watch.EventsPruned - _watchBaseline.EventsPruned;
        EventsMuted = watch.EventsMuted - _watchBaseline.EventsMuted;
        SignalsRaised = watch.SignalsRaised - _watchBaseline.SignalsRaised;
        Overflows = watch.Overflows - _watchBaseline.Overflows;

        DocumentsStatted = reconcile.DocumentsStatted - _reconcileBaseline.DocumentsStatted;
        DocumentsRead = reconcile.DocumentsRead - _reconcileBaseline.DocumentsRead;
        DocumentsForked = reconcile.DocumentsForked - _reconcileBaseline.DocumentsForked;
        Drains = reconcile.Drains - _reconcileBaseline.Drains;
        FullRescans = reconcile.FullRescans - _reconcileBaseline.FullRescans;
        StructuralReloads = reconcile.StructuralReloads - _reconcileBaseline.StructuralReloads;

        // read/stat is the stamp gate's payoff; fork/read is the content gate's. Lower is better for both.
        ReadRatio = Ratio(DocumentsRead, DocumentsStatted);
        ForkRatio = Ratio(DocumentsForked, DocumentsRead);

        NeedsFullRescan = dirty.NeedsFullRescan;
        PendingCount = dirty.PendingPaths.Count;
        PendingPaths = dirty.PendingPaths.Count == 0
            ? "(none)"
            : string.Join(Environment.NewLine, dirty.PendingPaths
                .Select(p => DiskSignalRow.Relative(p, watch.Root))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        var tabs = _editorTabs().ToList();
        StaleTabs = tabs.Count == 0
            ? "(no open editor tabs)"
            : string.Join(Environment.NewLine, tabs.Select(t =>
                $"{(t.IsStale ? "stale " : "fresh ")} {DiskSignalRow.Relative(t.FilePath ?? t.Header, watch.Root)}"));
    }

    private static string Ratio(long numerator, long denominator) =>
        denominator == 0 ? "—" : $"{100.0 * numerator / denominator:0.#}%";

    // Fires on a threadpool thread (the debounce timer), so the append to a bound collection must be
    // marshalled — mirroring MainWindowViewModel.OnDiskChanged. The root is read here rather than off a cached
    // field so a signal logged before the panel's first Pull still shows shortened paths.
    private void OnSignal(DiskChangeSignal signal)
    {
        var row = DiskSignalRow.For(signal, _watcher.Snapshot().Root);
        Dispatcher.UIThread.Post(() =>
        {
            Signals.Insert(0, row);
            while (Signals.Count > LogCapacity) Signals.RemoveAt(Signals.Count - 1);
        });
    }
}
