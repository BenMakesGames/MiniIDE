using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
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
    public WorkspaceService Workspace { get; }
    public SolutionWatcher Watcher { get; }
    public NuGetService NuGet { get; }
    public RunService Run { get; }
    public ShellService Shell { get; }
    public SyntaxHighlightService Highlight { get; }

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

    /// <summary>Raised to ask the view to reveal a location — open its file, place the caret, scroll, focus.
    /// The view owns that because only it has the realized editor; every navigation source (problems,
    /// go-to-definition) funnels through here rather than reaching into the editor itself.</summary>
    public event Func<SourceLocation, Task>? RequestOpen;

    public MainWindowViewModel()
    {
        Solution = new SolutionService();
        Workspace = new WorkspaceService();
        Watcher = new SolutionWatcher();
        Watcher.Changed += OnProjectFilesChanged;
        NuGet = new NuGetService();
        Run = new RunService();
        Shell = new ShellService();
        Highlight = new SyntaxHighlightService();
        Tabs = new TabStrip(msg => Status = msg);
        _runs = new RunRegistry(Run, Tabs, msg => Status = msg, CardFor);
        Git = new GitBar(() => Tabs.ShowOutput("git:", "Git - Output"), msg => Status = msg);
    }

    private Task OpenAsync(SourceLocation location) => RequestOpen?.Invoke(location) ?? Task.CompletedTask;

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
    // Every tab op lives in TabStrip. The forwards here are the ones callers (code-behind, project cards,
    // the rename flow) need, each the one-liner it would otherwise have to spell out as `Tabs.X`.

    public Task OpenFileAsync(string path) => Tabs.OpenFileAsync(path);

    public void ShowTab(TabViewModelBase tab) => Tabs.Show(tab);

    /// <summary>Focus-time refresh (wired to the window's <c>Activated</c> event): whatever the user did
    /// elsewhere — an agent editing the file on screen, a <c>git init</c> — shows when they come back. Cheap: a
    /// stat for the active tab and a directory probe for git.</summary>
    public async Task RefreshFromDiskAsync()
    {
        Git.Refresh(Solution.SolutionPath);
        await Tabs.RevalidateActiveAsync();
    }

    /// <summary>Backs the explicit "Reload solution" command's snapshot half: forces a full workspace rebuild
    /// and reloads every open editor tab. The project list is the command's own job.</summary>
    public async Task ReloadWorkspaceAsync()
    {
        try
        {
            await Workspace.ReloadIfLoadedAsync();
            await Tabs.ReloadEditorsAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    // ── Project actions (IProjectHost) ────────────────────────────────────────────────────────────────

    /// <summary>Runs on the project's Problems tab, creating it on first use. Re-asking refreshes the existing
    /// tab rather than opening a second one — that is what "Find problems" means a second time.</summary>
    public void FindProblems(ProjectItemViewModel project)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        var tab = Tabs.GetOrCreate(ProblemsTabViewModel.TabIdFor(project.Path),
            () => new ProblemsTabViewModel(project.Entry, Workspace, EnsureWorkspaceReadyAsync, OpenAsync));
        if (tab.RefreshCommand.CanExecute(null)) tab.RefreshCommand.Execute(null);
    }

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

    // ── Semantic queries ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Brings the Roslyn workspace up to date with the authoritative disk before a semantic query:
    /// loads it if needed, then reconciles the snapshot against current file contents. Every semantic query
    /// goes through this — skipping it means resolving against stale text, which after any length-changing
    /// external edit silently returns positions for the wrong symbol.</summary>
    private async Task EnsureWorkspaceReadyAsync(CancellationToken ct = default)
    {
        if (Solution.SolutionPath is null) return;
        await Workspace.EnsureLoadedAsync(Solution.SolutionPath, ct);
        await Workspace.ReconcileWithDiskAsync(ct);
    }

    /// <summary>Navigates to the definition of the symbol at <paramref name="caretOffset"/>, reporting the
    /// outcome to the status bar. Owns the whole flow — the view supplies the caret and nothing else.</summary>
    public async Task GoToDefinitionAsync(string file, int caretOffset)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace (first use may take a while)...";
        await EnsureWorkspaceReadyAsync();
        Status = "Resolving...";
        var definition = await Workspace.GoToDefinitionAsync(file, caretOffset);
        if (definition is null) { Status = "No definition found"; return; }
        Status = "Done";
        await OpenAsync(definition);
    }

    /// <summary>Renames the symbol at <paramref name="caretOffset"/> solution-wide, writing every reference (and
    /// the type-matched file) to disk. Owns the whole flow: reconcile → freshness-guard the clicked offset →
    /// prompt (via <paramref name="promptForNewName"/>, which the view fulfils with the modal dialog) → compute
    /// against fresh disk → apply → reflect the writes back into the view.
    ///
    /// <para><paramref name="viewText"/> is the editor's current text; the caret offset indexes it, but the
    /// refactor resolves on fresh disk. If the two have drifted, we do not guess a symbol from a stale offset —
    /// the tab is reloaded and the user is asked to re-invoke on the refreshed token.</para></summary>
    public async Task RenameSymbolAsync(
        string file, int caretOffset, string viewText, Func<string, Task<string?>> promptForNewName)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace (first use may take a while)...";
        await EnsureWorkspaceReadyAsync();

        // Freshness gate (guards resolution, not just the write): the offset is into the view's text, but the
        // snapshot now reflects disk. If they differ, resolving would land on the wrong token — refresh instead.
        string diskText;
        try { diskText = await File.ReadAllTextAsync(file); }
        catch (Exception ex) { Status = $"Could not read {Path.GetFileName(file)}: {ex.Message}"; return; }
        if (!string.Equals(viewText, diskText, StringComparison.Ordinal))
        {
            await Tabs.ReloadEditorsAsync();
            Status = "File changed on disk — view refreshed. Re-invoke Rename on the symbol.";
            return;
        }

        var target = await Workspace.DescribeRenameTargetAsync(file, caretOffset);
        if (target is null) { Status = "No symbol to rename here"; return; }
        if (!target.InSolution) { Status = "Only symbols defined in this solution can be renamed"; return; }

        var newName = await promptForNewName(target.Name);
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, target.Name, StringComparison.Ordinal))
        { Status = "Rename cancelled"; return; }

        RenameOutcome outcome;
        try { outcome = await Workspace.ComputeRenameAsync(file, caretOffset, newName); }
        catch (Exception ex) { Status = $"Rename failed: {ex.Message}"; return; }

        switch (outcome.Status)
        {
            case RenameStatus.NoSymbol: Status = "No symbol to rename here"; return;
            case RenameStatus.NotInSolution: Status = "Only symbols defined in this solution can be renamed"; return;
            case RenameStatus.Conflicts:
                Status = $"Rename blocked — {string.Join("; ", outcome.Conflicts)}. Nothing written.";
                return;
        }

        try { RenameService.ApplyToDisk(outcome); }
        catch (Exception ex) { Status = $"Rename failed writing to disk: {ex.Message}"; return; }

        // Reflect the writes back into the view. A moved file's tab is keyed to a path that no longer exists,
        // so it is re-homed; everything else is a stamp-gated revalidate.
        if (outcome.Move is { } move) await Tabs.RehomeMovedAsync(move.OldPath, move.NewPath);
        await Workspace.ReconcileWithDiskAsync();
        await Tabs.ReloadEditorsAsync();

        var count = outcome.ChangedFiles.Count;
        var files = count == 1 ? "file" : "files";
        Status = outcome.Move is { } m
            ? $"Renamed to {newName} across {count} {files}; {Path.GetFileName(m.OldPath)} → {Path.GetFileName(m.NewPath)}"
            : $"Renamed to {newName} across {count} {files}";
    }

}
