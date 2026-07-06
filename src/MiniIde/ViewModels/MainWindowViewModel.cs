using System;
using System.Collections.ObjectModel;
using System.IO;
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
    public SyntaxHighlightService Highlight { get; }

    public OutputViewModel Output { get; } = new();
    public FindResultsViewModel Find { get; }
    public NuGetViewModel NuGetVm { get; }

    public ObservableCollection<TreeNode> Tree { get; } = new();
    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();
    public ObservableCollection<ProjectEntry> Projects { get; } = new();

    [ObservableProperty] private TabViewModelBase? _activeTab;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private ProjectEntry? _startupProject;
    [ObservableProperty] private string? _solutionName = "<no solution>";

    public event Func<string, int, int, Task>? RequestOpen;

    public MainWindowViewModel()
    {
        Solution = new SolutionService();
        Workspace = new WorkspaceService();
        Search = new SearchService();
        NuGet = new NuGetService();
        Run = new RunService();
        Highlight = new SyntaxHighlightService();
        Workspace.Progress += m => Dispatcher.UIThread.Post(() => Status = m);
        Find = new FindResultsViewModel(Search, Solution, async (f, l, c) =>
        {
            if (RequestOpen is not null) await RequestOpen(f, l, c);
        });
        NuGetVm = new NuGetViewModel(NuGet, Output);
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
            Status = $"Loaded {Path.GetFileName(path)} ({Solution.Projects.Count} projects)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    private static ProjectEntry? PickDefaultStartup(System.Collections.Generic.IReadOnlyList<ProjectEntry> projects)
    {
        foreach (var e in projects) if (e.IsRunnable) return e;
        return projects.Count > 0 ? projects[0] : null;
    }

    public async Task OpenFileAsync(string path, int line = 1, int col = 1)
    {
        TabViewModelBase? tab = null;
        foreach (var t in Tabs) if (t.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)) { tab = t; break; }
        if (tab is null)
        {
            tab = TabViewModelBase.CreateForFile(path);
            tab.RequestClose += CloseTabAsync;
            Tabs.Add(tab);
        }
        ActiveTab = tab;
        await Task.CompletedTask;
    }

    private async Task CloseTabAsync(TabViewModelBase tab)
    {
        if (tab.IsDirty) await tab.SaveAsync();
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
    }

    private bool CanPlay() => StartupProject?.IsRunnable == true;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public async Task PlayAsync()
    {
        if (StartupProject is null) { Status = "No startup project"; return; }
        Output.Clear();
        var name = Path.GetFileNameWithoutExtension(StartupProject.Path);
        Status = StartupProject.Kind == ProjectKind.Tst ? $"Testing {name}..." : $"Running {name}...";
        try { await Run.RunAsync(StartupProject, Output.Append); Status = "Run finished"; }
        catch (Exception ex) { Status = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    public void Stop() { Run.Stop(); Status = "Stopped"; }

    partial void OnStartupProjectChanged(ProjectEntry? value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public async Task<(string, int, int)?> GoToDefinitionAsync(string file, int position)
    {
        if (Solution.SolutionPath is null) return null;
        Status = "Loading workspace (first use may take a while)...";
        await Workspace.EnsureLoadedAsync(Solution.SolutionPath);
        Status = "Resolving...";
        var result = await Workspace.GoToDefinitionAsync(file, position);
        Status = result is null ? "No definition found" : "Done";
        return result;
    }

    /// <summary>
    /// Returns reference locations (possibly empty), or <c>null</c> when no symbol resolves at the position.
    /// The null case sets a "No symbol found" status; callers use it to skip populating results.
    /// </summary>
    public async Task<System.Collections.Generic.IReadOnlyList<(string, int, int, string)>?> FindReferencesAsync(string file, int position)
    {
        if (Solution.SolutionPath is null) return System.Array.Empty<(string, int, int, string)>();
        Status = "Loading workspace...";
        await Workspace.EnsureLoadedAsync(Solution.SolutionPath);
        Status = "Finding references...";
        var refs = await Workspace.FindReferencesAsync(file, position);
        if (refs is null) { Status = "No symbol found"; return null; }
        Status = $"{refs.Count} reference(s)";
        return refs;
    }
}
