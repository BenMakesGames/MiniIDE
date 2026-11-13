using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IProjectHost
{
    public SolutionService Solution { get; }
    public SolutionWatcher Watcher { get; }
    public NuGetService NuGet { get; }
    public RunService Run { get; }
    public ShellService Shell { get; }

    private readonly RunRegistry _runs;

    /// <summary>The git bar: pull, push, init, and the one question (is this solution in a repo?) that drives
    /// the three buttons. The XAML binds through this property (<c>Git.OffersGitInit</c>, <c>Git.PullTip</c>,
    /// <c>Git.GitPullCommand</c>, …).</summary>
    public GitBar Git { get; }

    /// <summary>The left panel: one card per project, in the solution's (name-sorted) order.</summary>
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = new();

    /// <summary>The tab strip — collection, active selection, and the operations on both. XAML binds through
    /// it (<c>Tabs.Tabs</c>, <c>Tabs.ActiveTab</c>), the same pattern <see cref="Git"/> uses.</summary>
    public TabStrip Tabs { get; }

    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(WindowTitle))] private string? _solutionName = "<no solution>";

    public string WindowTitle => Solution.SolutionPath is null ? "MiniIDE" : $"{SolutionName} - MiniIDE";

    public MainWindowViewModel()
    {
        Solution = new SolutionService();
        Watcher = new SolutionWatcher();
        Watcher.Changed += OnProjectFilesChanged;
        NuGet = new NuGetService();
        Run = new RunService();
        Shell = new ShellService();
        Tabs = new TabStrip();
        _runs = new RunRegistry(Run, Tabs, msg => Status = msg, CardFor);
        Git = new GitBar(() => Tabs.ShowOutput("git:", "Git - Output"), msg => Status = msg);
    }

    [RelayCommand]
    public async Task OpenSolutionAsync(string path)
    {
        try
        {
            Status = "Loading solution...";
            LoadLocalSettings(path);
            await LoadProjectsAsync(path);
            SolutionName = Path.GetFileNameWithoutExtension(path);
            // (Re)point the change feed at this solution — this is also the restart on "Reload solution".
            var watching = Watcher.Start(path);
            Git.Refresh(path);
            var loaded = $"Loaded {Path.GetFileName(path)} ({Solution.Projects.Count} projects)";
            // An unreadable settings file already put its own message in the status bar; don't bury it.
            if (!_settingsUnreadable)
                Status = watching ? loaded : $"{loaded}; not watching for project changes — use Reload solution";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Re-reads the project list from disk and reconciles the cards against it by path: a card for a
    /// project still in the solution is updated in place (keeping its selections and run buttons), new
    /// projects get new cards, and departed ones are dropped. Shared by the initial open and every change the
    /// watcher reports, so the two can't drift.</summary>
    private async Task LoadProjectsAsync(string path)
    {
        await Solution.LoadAsync(path);

        var existing = Projects.ToDictionary(p => p.Path, StringComparer.OrdinalIgnoreCase);
        var cards = new List<ProjectItemViewModel>();
        foreach (var entry in Solution.Projects)
        {
            if (existing.TryGetValue(entry.Path, out var card)) card.Update(entry, Solution.Configurations);
            else card = new ProjectItemViewModel(entry, Solution.Configurations, this);
            card.IsStarred = _starred.Contains(entry.Path);
            cards.Add(card);
        }
        ShowInOrder(cards);

        // A live run belongs to its project's card. Re-attached by path, so a card created by this reload (the
        // project was briefly gone from the solution) still shows the runs it had.
        foreach (var (run, projectPath) in _runs.LiveRuns)
            foreach (var card in cards)
                if (SamePath(projectPath, card.Path) && !card.LiveRuns.Contains(run))
                    card.LiveRuns.Add(run);
    }

    /// <summary>Shows <paramref name="cards"/> starred-first, and within each group in the solution service's
    /// order (runnable first, then by name). Always ranked against that canonical order, never the on-screen one —
    /// otherwise an unstarred project would stay wherever its star had put it.
    ///
    /// <para>Rebuilt only when membership or order actually changed: clearing a list of unchanged cards would
    /// re-realize every one of them, closing any open Run flyout under the user's cursor.</para></summary>
    private void ShowInOrder(IEnumerable<ProjectItemViewModel> cards)
    {
        var rank = Solution.Projects
            .Select((entry, index) => (entry.Path, index))
            .ToDictionary(x => x.Path, x => x.index, StringComparer.OrdinalIgnoreCase);
        var ordered = cards
            .OrderBy(c => !c.IsStarred)
            .ThenBy(c => rank.GetValueOrDefault(c.Path, int.MaxValue))
            .ToList();
        if (ordered.SequenceEqual(Projects)) return;
        Projects.Clear();
        foreach (var card in ordered) Projects.Add(card);
    }

    // ── Stars ─────────────────────────────────────────────────────────────────────────────────────────
    //
    // Personal, per solution, kept in .miniide/<Solution>.local.json (see LocalSolutionSettings).

    /// <summary>Starred projects' full paths. Kept for paths not currently in the solution too, so a project
    /// that briefly drops out (a branch switch) gets its star back when it returns.</summary>
    private readonly HashSet<string> _starred = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the settings file could not be read. Saving then would overwrite whatever is in it — a
    /// hand-edit with one typo would cost every star — so starring is refused until it's fixed.</summary>
    private bool _settingsUnreadable;

    private void LoadLocalSettings(string solutionPath)
    {
        _starred.Clear();
        _settingsUnreadable = false;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
            foreach (var relative in LocalSolutionSettings.Load(solutionPath).StarredProjects)
                _starred.Add(Path.GetFullPath(Path.Combine(dir, relative)));
        }
        catch (Exception ex)
        {
            _settingsUnreadable = true;
            Status = $"Could not read {LocalSolutionSettings.PathFor(solutionPath)}: {ex.Message}";
        }
    }

    public void SetStarred(ProjectItemViewModel project, bool starred)
    {
        if (Solution.SolutionPath is not { } solutionPath) return;
        if (_settingsUnreadable)
        {
            Status = $"Not saving stars: fix or delete {LocalSolutionSettings.PathFor(solutionPath)}, then reload";
            return;
        }

        if (starred) _starred.Add(project.Path);
        else _starred.Remove(project.Path);
        project.IsStarred = starred;
        ShowInOrder(Projects.ToList());

        var dir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var relative = _starred
            .Select(p => Path.GetRelativePath(dir, p))
            .Order(StringComparer.OrdinalIgnoreCase) // stable file contents: no diff noise from set order
            .ToArray();
        try { new LocalSolutionSettings(relative).Save(solutionPath); }
        catch (Exception ex) { Status = $"Could not save stars: {ex.Message}"; }
    }

    /// <summary>A solution file, project file, launch settings, or settings file changed on disk. Arrives on a
    /// threadpool thread; the reload and everything that follows it happen on the UI thread. Our own star saves
    /// land here too — re-reading what we just wrote is a no-op, so they aren't filtered out.</summary>
    private void OnProjectFilesChanged() => Dispatcher.UIThread.Post(async () =>
    {
        if (Solution.SolutionPath is not { } path) return;
        try
        {
            LoadLocalSettings(path);
            await LoadProjectsAsync(path);
            foreach (var tab in Tabs.Tabs.OfType<NuGetTabViewModel>()) tab.ReloadPackages();
        }
        catch (Exception ex) { Status = ex.Message; }
    });

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private ProjectItemViewModel? CardFor(string projectPath) =>
        Projects.FirstOrDefault(p => SamePath(p.Path, projectPath));

    // ── Tabs ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // Every tab op lives in TabStrip. The forward here is the one project cards need.

    public void ShowTab(TabViewModelBase tab) => Tabs.Show(tab);

    /// <summary>Focus-time refresh (wired to the window's <c>Activated</c> event): a <c>git init</c> done
    /// elsewhere shows when the user comes back. Cheap: a directory probe.</summary>
    public void RefreshGit() => Git.Refresh(Solution.SolutionPath);

    // ── Project actions (IProjectHost) ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowAbout() => Tabs.GetOrCreate(AboutTabViewModel.Id, () => new AboutTabViewModel());

    [RelayCommand]
    private void ShowFileWatcher() =>
        Tabs.GetOrCreate(FileWatcherTabViewModel.Id, () => new FileWatcherTabViewModel(Watcher));

    public void ManageNuGet(ProjectItemViewModel project) =>
        Tabs.GetOrCreate(NuGetTabViewModel.TabIdFor(project.Path),
            () => new NuGetTabViewModel(project.Entry, NuGet,
                () => Tabs.ShowOutput("nuget:", "NuGet - Output"),
                Tabs.ShowOutput));

    // ── Runs ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // A run tab owns one process at a time. Closing the tab doesn't stop it; the tab's Stop does, and so does
    // the IDE closing. While a run is live, its project card shows a button that brings its tab back. Every
    // bit of per-run state (live list, project-by-tab, per-selection start counter) lives in RunRegistry.

    public void StartRun(ProjectItemViewModel project, RunSelection selection) =>
        _runs.Start(project.Entry, selection);

    public void StopAllRuns() => _runs.StopAll();
}
