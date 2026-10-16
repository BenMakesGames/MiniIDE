using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>The file-watcher diagnostics tab: what <see cref="SolutionWatcher"/> has seen, and which bursts
/// became project-list reloads. Read-only observation — it changes no watcher behavior.
///
/// <para>Pulled on a 1 s timer only while the tab is active (<see cref="StartPolling"/>/<see cref="StopPolling"/>).
/// Nothing is lost while it's hidden: the watcher keeps the counters and recent bursts itself.</para></summary>
public partial class FileWatcherTabViewModel : TabViewModelBase
{
    public const string Id = "file-watcher:";

    /// <summary>Fast enough to feel live, slow enough to be free.</summary>
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(1);

    private static readonly WatchStats NoBaseline = new(null, 0, 0, 0, 0, []);

    private readonly SolutionWatcher _watcher;
    private readonly DispatcherTimer _timer;

    // Reset is a baseline the panel subtracts, not a write to the watcher — the panel can't mutate the service.
    private WatchStats _baseline = NoBaseline;

    // The newest burst currently shown, so an unchanged log isn't rebuilt (and its scroll position kept) each tick.
    private long _shownThrough = -1;

    [ObservableProperty] private string _watchStatus = "Not watching";
    [ObservableProperty] private long _eventsSeen;
    [ObservableProperty] private long _eventsRelevant;
    [ObservableProperty] private long _reloads;
    [ObservableProperty] private long _overflows;

    /// <summary>Recent reloads, newest first.</summary>
    public ObservableCollection<WatchBurstRow> Bursts { get; } = new();

    public override string Header => "File-watcher diagnostics";

    public FileWatcherTabViewModel(SolutionWatcher watcher) : base(Id, filePath: null)
    {
        _watcher = watcher;
        // Default ctor, then Start on activation: the (interval, priority, handler) overload starts immediately.
        _timer = new DispatcherTimer { Interval = PullInterval };
        _timer.Tick += (_, _) => Pull();
    }

    /// <summary>Begins the pull, pulling once now so the tab isn't blank for a second.</summary>
    public void StartPolling()
    {
        Pull();
        _timer.Start();
    }

    public void StopPolling() => _timer.Stop();

    /// <summary>Rebases the counters to now and clears the log, so the next thing you do is the only thing on
    /// screen.</summary>
    [RelayCommand]
    private void Reset()
    {
        _baseline = _watcher.Snapshot();
        Pull();
    }

    private void Pull()
    {
        var stats = _watcher.Snapshot();

        WatchStatus = stats.Root is { } root
            ? $"Watching {root} for solution, project, and launchSettings.json changes"
            : "Not watching — no solution open, or the watch failed to start (Reload solution retries)";

        EventsSeen = stats.EventsSeen - _baseline.EventsSeen;
        EventsRelevant = stats.EventsRelevant - _baseline.EventsRelevant;
        Reloads = stats.Reloads - _baseline.Reloads;
        Overflows = stats.Overflows - _baseline.Overflows;

        var visible = stats.RecentBursts.Where(b => b.Sequence > _baseline.Reloads).ToList();
        var newest = visible.Count == 0 ? 0 : visible[0].Sequence;
        if (newest == _shownThrough && visible.Count == Bursts.Count) return;

        _shownThrough = newest;
        Bursts.Clear();
        foreach (var burst in visible) Bursts.Add(WatchBurstRow.For(burst, stats.Root));
    }
}

/// <summary>One reload in the diagnostics log, formatted for display.</summary>
public sealed class WatchBurstRow
{
    private static readonly IBrush OverflowBrush = new ImmutableSolidColorBrush(Color.FromRgb(244, 71, 71));
    private static readonly IBrush NormalBrush = new ImmutableSolidColorBrush(Color.FromRgb(128, 128, 128));

    private WatchBurstRow(string time, string detail, IBrush timeColor)
    {
        Time = time;
        Detail = detail;
        TimeColor = timeColor;
    }

    public string Time { get; }
    /// <summary>The files that caused the reload — the answer to "why did the project list just refresh?"</summary>
    public string Detail { get; }
    public IBrush TimeColor { get; }

    public static WatchBurstRow For(WatchBurst burst, string? root)
    {
        var paths = string.Join(", ", burst.Paths.Select(p => Relative(p, root)));
        var detail = burst.Overflow
            ? $"OS dropped events — reloaded anyway{(paths.Length == 0 ? "" : $"; also saw {paths}")}"
            : paths;
        return new WatchBurstRow(
            burst.RaisedUtc.ToLocalTime().ToString("HH:mm:ss.fff"),
            detail,
            burst.Overflow ? OverflowBrush : NormalBrush);
    }

    // A path outside the root (or no root) shows in full rather than as a "..\" climb.
    private static string Relative(string path, string? root)
    {
        if (root is null) return path;
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }
}
