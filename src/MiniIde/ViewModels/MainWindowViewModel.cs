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

public partial class MainWindowViewModel : ViewModelBase
{
    public SolutionService Solution { get; }
    public WorkspaceService Workspace { get; }
    public SearchService Search { get; }
    public NuGetService NuGet { get; }
    public RunService Run { get; }
    public ShellService Shell { get; }
    public SyntaxHighlightService Highlight { get; }

    public FindResultsViewModel Find { get; }
    public ProblemsViewModel Problems { get; }
    public NuGetViewModel NuGetVm { get; }

    public ObservableCollection<TreeNode> Tree { get; } = new();
    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();
    public ObservableCollection<ProjectEntry> Projects { get; } = new();

    [ObservableProperty] private TabViewModelBase? _activeTab;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private ProjectEntry? _startupProject;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(WindowTitle))] private string? _solutionName = "<no solution>";

    public string WindowTitle => Solution.SolutionPath is null ? "MiniIDE" : $"{SolutionName} - MiniIDE";

    /// <summary>Raised to ask the view to reveal a location — open its file, place the caret, scroll, focus.
    /// The view owns that because only it has the realized editor; every navigation source (find results,
    /// problems, go-to-definition) funnels through here rather than reaching into the editor itself.</summary>
    public event Func<SourceLocation, Task>? RequestOpen;

    public MainWindowViewModel()
    {
        Solution = new SolutionService();
        Workspace = new WorkspaceService();
        Search = new SearchService();
        NuGet = new NuGetService();
        Run = new RunService();
        Shell = new ShellService();
        Highlight = new SyntaxHighlightService();
        Workspace.Progress += m => Dispatcher.UIThread.Post(() => Status = m);
        Find = new FindResultsViewModel(Search, Solution, OpenAsync);
        Problems = new ProblemsViewModel(Workspace, Solution, EnsureWorkspaceReadyAsync, OpenAsync);
        NuGetVm = new NuGetViewModel(NuGet, ResolveNuGetOutput, ResolveNuGetMetadataTab);
    }

    private Task OpenAsync(SourceLocation location) => RequestOpen?.Invoke(location) ?? Task.CompletedTask;

    /// <summary>Gets-or-creates the shared <c>NuGet - Output</c> tab and activates it. Keeps
    /// <see cref="NuGetViewModel"/> ignorant of tab mechanics (Open Decision #3). The fixed <c>nuget:</c>
    /// identity means a package restore never collides with a project literally named "NuGet".</summary>
    private OutputTabViewModel ResolveNuGetOutput()
    {
        var tab = GetOrCreateOutputTab("nuget:", "NuGet - Output");
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Per-package metadata tab counterpart to <see cref="ResolveNuGetOutput"/>. Same "get-or-create +
    /// activate" contract; the caller supplies its own <c>nuget-meta:{id-lower}</c>-shaped id and header so
    /// each package gets a distinct tab that dedups on re-double-click.</summary>
    private OutputTabViewModel ResolveNuGetMetadataTab(string tabId, string header)
    {
        var tab = GetOrCreateOutputTab(tabId, header);
        ActiveTab = tab;
        return tab;
    }

    [RelayCommand]
    public async Task OpenSolutionAsync(string path)
    {
        try
        {
            Status = "Loading solution...";
            var projectNodes = await Solution.LoadAsync(path);
            Tree.Clear();
            foreach (var n in projectNodes) Tree.Add(n);
            Projects.Clear();
            foreach (var e in Solution.Projects) Projects.Add(e);
            NuGetVm.SetProjects(Solution.Projects);
            StartupProject = PickDefaultStartup(Solution.Projects);
            SolutionName = Path.GetFileNameWithoutExtension(path);
            Problems.NotifyCanRefreshChanged();
            Status = $"Loaded {Path.GetFileName(path)} ({Solution.Projects.Count} projects)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static ProjectEntry? PickDefaultStartup(IReadOnlyList<ProjectEntry> projects)
    {
        foreach (var e in projects) if (e.IsRunnable) return e;
        return projects.Count > 0 ? projects[0] : null;
    }

    /// <summary>Opens (or re-activates) the tab for <paramref name="path"/>. Genuinely async: the file read
    /// happens off the UI thread. A file that can't be read reports to the status bar rather than throwing
    /// into an <c>async void</c> event handler and taking the app down.</summary>
    public async Task OpenFileAsync(string path)
    {
        var id = TabViewModelBase.FileId(path);
        var existing = Tabs.FirstOrDefault(t => t.TabId == id);
        if (existing is not null) { ActiveTab = existing; return; }

        TabViewModelBase tab;
        try { tab = await TabViewModelBase.CreateForFileAsync(path); }
        catch (Exception ex) { Status = $"Could not open {Path.GetFileName(path)}: {ex.Message}"; return; }

        tab.RequestClose += CloseTabAsync;
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>Reuses the existing output tab with this identity, or creates one (appended at the end,
    /// mirroring <see cref="OpenFileAsync"/> — Open Decision #4). Dedup keys on <see cref="TabViewModelBase.TabId"/>,
    /// never the header, so two output tabs can share a title while staying distinct.</summary>
    private OutputTabViewModel GetOrCreateOutputTab(string tabId, string header)
    {
        var existing = Tabs.OfType<OutputTabViewModel>().FirstOrDefault(t => t.TabId == tabId);
        if (existing is not null) return existing;
        var tab = new OutputTabViewModel(tabId, header);
        tab.RequestClose += CloseTabAsync;
        Tabs.Add(tab);
        return tab;
    }

    // Returns Task (not async) so it still satisfies the RequestClose Func<,Task> contract: with save-on-close
    // gone under the read-only law, nothing here awaits.
    private Task CloseTabAsync(TabViewModelBase tab)
    {
        // Closing the tab that owns the live run silently stops its process (no confirmation dialog yet).
        if (ReferenceEquals(_liveRunTab, tab) && Run.IsRunning) { Run.Stop(); _liveRunTab = null; }
        // No save-on-close: the editor is a read-only window onto disk, so a closing tab has nothing to persist.
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
        return Task.CompletedTask;
    }

    /// <summary>The output tab that owns the currently-live <see cref="RunService"/> process. Cleared only when
    /// the finalizing run still owns it — under single-run, starting run B kills run A, so A's continuation runs
    /// after B has become the live tab; A must not clear B's tracking (see the ticket's kill-race gotcha).</summary>
    private OutputTabViewModel? _liveRunTab;

    private bool CanPlay() => StartupProject?.IsRunnable == true;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public async Task PlayAsync()
    {
        if (StartupProject is null) { Status = "No startup project"; return; }
        var name = Path.GetFileNameWithoutExtension(StartupProject.Path);
        var tab = GetOrCreateOutputTab("run:" + Path.GetFullPath(StartupProject.Path).ToLowerInvariant(), $"{name} - Output");
        tab.Clear();
        ActiveTab = tab;
        _liveRunTab = tab;
        Status = StartupProject.Kind == ProjectKind.Tst ? $"Testing {name}..." : $"Running {name}...";
        try { await Run.RunAsync(StartupProject, tab.Append); Status = "Run finished"; }
        catch (Exception ex) { Status = ex.Message; }
        finally { if (ReferenceEquals(_liveRunTab, tab)) _liveRunTab = null; }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public void Stop() { Run.Stop(); Status = "Stopped"; }

    partial void OnStartupProjectChanged(ProjectEntry? value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ── Semantic queries ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Brings the Roslyn workspace up to date with the authoritative disk before a semantic query:
    /// loads it if needed, then reconciles the snapshot against current file contents. Every semantic query
    /// goes through this — skipping it means resolving against stale text, which after any length-changing
    /// external edit silently returns positions for the wrong symbol. The reconcile reads disk itself (the
    /// view holds no editable buffer to hand it), so nothing needs snapshotting before the first await.</summary>
    private async Task EnsureWorkspaceReadyAsync(CancellationToken ct = default)
    {
        if (Solution.SolutionPath is null) return;
        await Workspace.EnsureLoadedAsync(Solution.SolutionPath, ct);
        await Workspace.ReconcileWithDiskAsync(ct);
    }

    /// <summary>Focus-time refresh (wired to the window's <c>Activated</c> event): reflects any external edits
    /// back into the view so it's never frozen on stale text. Reloads every open editor tab whose file drifted
    /// on disk, and — only if the workspace was already loaded — reconciles the semantic snapshot too (focus
    /// must never trigger the cold MSBuild load; that stays lazy, on first query). Errors report to the status
    /// bar rather than throwing out of the <c>async void</c> event handler.</summary>
    public async Task RefreshFromDiskAsync()
    {
        try
        {
            await ReloadDriftedTabsAsync();
            if (Workspace.IsLoaded) await Workspace.ReconcileWithDiskAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Backs the explicit "Reload solution" command's snapshot half: forces a full workspace rebuild
    /// (so structural changes are picked up) and reloads any drifted open tabs. The tree/project reload is the
    /// command's own job; this refreshes the semantic snapshot that reload would otherwise leave stale.</summary>
    public async Task ReloadWorkspaceAsync()
    {
        try
        {
            await Workspace.ReloadIfLoadedAsync();
            await ReloadDriftedTabsAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Re-reads every open editor tab from disk and reflects any change back into the view. Content is
    /// compared inside <see cref="EditorTabViewModel.ReloadFromDisk"/> (on the UI thread, where the document is
    /// safe to read), so an unchanged file is a no-op — no flicker, no caret reset. Covers non-Roslyn tabs
    /// (a <c>.md</c>, a <c>.json</c>) that the snapshot reconcile doesn't. A file that vanished or can't be read
    /// is left as-is until the next refresh.</summary>
    private async Task ReloadDriftedTabsAsync()
    {
        foreach (var tab in Tabs.OfType<EditorTabViewModel>().ToList())
        {
            if (tab.FilePath is null || !File.Exists(tab.FilePath)) continue;
            try { tab.ReloadFromDisk(await File.ReadAllTextAsync(tab.FilePath)); }
            catch { /* transient IO or a file mid-write; try again on the next focus */ }
        }
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

    /// <summary>Fills the Find panel with references to the symbol at <paramref name="caretOffset"/>. The panel
    /// owns its own results and status; this just decides what to hand it.</summary>
    public async Task FindReferencesAsync(string file, int caretOffset)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace...";
        await EnsureWorkspaceReadyAsync();
        Status = "Finding references...";
        var references = await Workspace.FindReferencesAsync(file, caretOffset);
        if (references is null) { Find.ShowNoResults("No symbol found"); Status = "No symbol found"; return; }
        Find.ShowReferences(references);
        Status = Find.Status;
    }
}
