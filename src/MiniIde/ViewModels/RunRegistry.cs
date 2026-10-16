using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>Owns every run tab's process bookkeeping — the three correlated collections that would otherwise
/// have to stay coherent across <see cref="MainWindowViewModel"/>: the live-run list (for shutdown and card
/// re-attach), the per-tab project path (so a run outlives any one card instance), and the monotonic
/// per-(project, selection) start counter (for stable run numbering across a session).
///
/// <para>All three move together on every start and exit, and the correctness invariant — "a tab is in
/// <see cref="_liveRuns"/> iff its <see cref="OutputTabViewModel.IsRunLive"/> is true, and <see cref="_runProjects"/>
/// has an entry for every run tab the registry still owns" — lives in one place rather than in three
/// collaborating methods.</para>
///
/// <para>Lifetime: a run tab enters the registry on <see cref="Start"/> and leaves — handler unsubscribed,
/// project entry evicted — on <see cref="Forget"/>, which fires when the tab is closed <em>after</em> its run
/// has finished. A run outlives its tab, but we don't let a closed tab outlive its run silently: when a run
/// exits with its tab closed, the tab is <b>re-opened</b> (<see cref="OnRunTabPropertyChanged"/>) so the final
/// output — in particular an exception stack — isn't lost. Eviction then waits on the user closing the tab
/// a second time, caught by <see cref="OnTabsCollectionChanged"/>.</para></summary>
public sealed class RunRegistry
{
    private readonly RunService _runService;
    private readonly TabStrip _tabs;
    private readonly Action<string> _setStatus;
    private readonly Func<string, ProjectItemViewModel?> _cardFor;

    /// <summary>Every run whose process is still going, whether or not its tab is open. The shutdown path's
    /// list: run children are started with <c>CreateNoWindow</c>, so Windows does not reap them with us.</summary>
    private readonly List<OutputTabViewModel> _liveRuns = new();

    /// <summary>Which project each run tab belongs to, by path — a run outlives any one card instance.</summary>
    private readonly Dictionary<OutputTabViewModel, string> _runProjects = new();

    /// <summary>How many runs this session has started per project <em>and</em> selection — the same things
    /// the header names, so the numbering groups exactly as the labels read. Monotonic: a number is never handed
    /// out twice, so a header always names the same run it did a minute ago.</summary>
    private readonly Dictionary<string, int> _runsStarted = new(StringComparer.Ordinal);

    public RunRegistry(
        RunService runService,
        TabStrip tabs,
        Action<string> setStatus,
        Func<string, ProjectItemViewModel?> cardFor)
    {
        _runService = runService;
        _tabs = tabs;
        _setStatus = setStatus;
        _cardFor = cardFor;
        // Eviction entry: a closed-while-dead tab (close-while-live-then-exits brings the tab back on screen
        // first and only reaches Forget when the user closes it a second time, which also lands here).
        _tabs.Tabs.CollectionChanged += OnTabsCollectionChanged;
    }

    /// <summary>Every live run paired with its project path. The project-list reload iterates this to re-attach
    /// a run to its (possibly fresh) card after the solution changes.</summary>
    public IEnumerable<(OutputTabViewModel Tab, string ProjectPath)> LiveRuns =>
        _liveRuns.Select(t => (t, _runProjects[t]));

    /// <summary>Opens a brand-new tab that owns its own process and launches the run into it, so repeat runs
    /// are concurrent instances rather than replacements. The tab keeps the project and selection it was born
    /// with, which is what lets its own Run button restart the same thing after it exits.</summary>
    public void Start(ProjectEntry entry, RunSelection selection)
    {
        var description = ProjectItemViewModel.Describe(selection);
        var label = $"{entry.Name} [{description}]";
        var key = $"{Path.GetFullPath(entry.Path).ToLowerInvariant()}|{selection.Configuration}|{selection.LaunchProfile}";
        var n = _runsStarted[key] = _runsStarted.GetValueOrDefault(key) + 1;

        var tab = new OutputTabViewModel(
            $"run:{key}#{n}",
            n == 1 ? $"{label} - Output" : $"{label} - Output ({n})",
            t => Launch(entry, selection, t, label),
            n == 1 ? description : $"{description} ({n})");
        _runProjects[tab] = entry.Path;
        tab.PropertyChanged += OnRunTabPropertyChanged;
        _tabs.Open(tab);
        _tabs.ActiveTab = tab;
        Launch(entry, selection, tab, label);
    }

    /// <summary>Kills every run still going, open tab or not. Wired to the window's <c>Closed</c>.</summary>
    public void StopAll()
    {
        foreach (var tab in _liveRuns.ToList()) tab.StopRun();
    }

    /// <summary>Keeps the live-run lists in step with each tab's process — including a run restarted from its
    /// own tab, which brings the card's button back. On exit, if the tab was closed while the run was going,
    /// it is <b>re-opened and activated</b>: the final lines of output (an exception stack, a non-zero exit)
    /// are usually the whole reason the run matters, and a closed tab would silently swallow them. A tab that
    /// was still open on exit is left alone — <see cref="TabStrip.Show"/> would steal focus for no reason.</summary>
    private void OnRunTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OutputTabViewModel.IsRunLive) || sender is not OutputTabViewModel tab) return;
        var card = _cardFor(_runProjects[tab]);
        if (tab.IsRunLive)
        {
            if (!_liveRuns.Contains(tab)) _liveRuns.Add(tab);
            if (card is not null && !card.LiveRuns.Contains(tab)) card.LiveRuns.Add(tab);
        }
        else
        {
            _liveRuns.Remove(tab);
            card?.LiveRuns.Remove(tab);
            if (!_tabs.Tabs.Contains(tab)) _tabs.Show(tab);
        }
    }

    /// <summary>The other eviction entry — the tab was closed while its run was already dead, so the
    /// IsRunLive edge has already fired. Only acts on run tabs the registry still owns and only on tabs that
    /// are not live; a tab closed while its run is going stays wired, and <see cref="OnRunTabPropertyChanged"/>
    /// will evict it when the process finally exits.</summary>
    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is null) return;
        foreach (var tab in e.OldItems.OfType<OutputTabViewModel>())
            if (_runProjects.ContainsKey(tab) && !tab.IsRunLive) Forget(tab);
    }

    private void Forget(OutputTabViewModel tab)
    {
        tab.PropertyChanged -= OnRunTabPropertyChanged;
        _runProjects.Remove(tab);
    }

    /// <summary>Starts one process into <paramref name="tab"/> and returns as soon as it is launched — the
    /// waiting is done by a detached continuation, so nothing that starts a run stays busy for its lifetime.</summary>
    private void Launch(ProjectEntry project, RunSelection selection, OutputTabViewModel tab, string label)
    {
        // The status bar is last-write-wins and concurrent runs share it, so every run message names its
        // project and selection — whichever one wins is at least attributable.
        _setStatus(project.Kind == ProjectKind.Tst ? $"Testing {label}..." : $"Running {label}...");
        try
        {
            var session = _runService.Start(project, selection, tab.Append);
            tab.AttachRun(session);
            _ = ReportExitAsync(session, label);
        }
        catch (Exception ex) { _setStatus($"{label}: {ex.Message}"); }
    }

    // Started from the UI thread, so the continuation resumes there (Avalonia installs a SynchronizationContext)
    // and the status write lands on the thread its bindings expect.
    private async Task ReportExitAsync(RunSession session, string label)
    {
        try
        {
            await session.Completion;
            _setStatus($"{label} finished");
        }
        catch (Exception ex) { _setStatus($"{label}: {ex.Message}"); }
    }
}
