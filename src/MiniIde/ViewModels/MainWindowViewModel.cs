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

    private async Task CloseTabAsync(TabViewModelBase tab)
    {
        // Closing the tab that owns the live run silently stops its process (no confirmation dialog yet).
        if (ReferenceEquals(_liveRunTab, tab) && Run.IsRunning) { Run.Stop(); _liveRunTab = null; }
        if (tab.IsDirty) await tab.SaveAsync();
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
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

    /// <summary>Brings the Roslyn workspace up to date with what the user is actually looking at: loads it if
    /// needed, then overlays every unsaved editor buffer. Every semantic query goes through this — skipping it
    /// means resolving against the on-disk text, which after any length-changing edit silently returns
    /// positions for the wrong symbol.
    ///
    /// <para>The buffers are snapshotted <b>before</b> the first await, on the caller's thread: AvaloniaEdit's
    /// <c>TextDocument.Text</c> is thread-affine and every caller here originates on the UI thread.</para></summary>
    private async Task EnsureWorkspaceReadyAsync(CancellationToken ct = default)
    {
        if (Solution.SolutionPath is null) return;
        var buffers = UnsavedBuffers();
        await Workspace.EnsureLoadedAsync(Solution.SolutionPath, ct);
        await Workspace.SyncDocumentsAsync(buffers, ct);
    }

    private List<(string Path, string Text)> UnsavedBuffers() =>
        Tabs.OfType<EditorTabViewModel>()
            .Where(t => t.IsDirty && t.FilePath is not null)
            .Select(t => (t.FilePath!, t.Document.Text))
            .ToList();

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
