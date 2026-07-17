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
    public SolutionWatcher Watcher { get; }
    public SearchService Search { get; }
    public NuGetService NuGet { get; }
    public RunService Run { get; }
    public ShellService Shell { get; }
    public SyntaxHighlightService Highlight { get; }

    public FindResultsViewModel Find { get; }
    public ProblemsViewModel Problems { get; }
    public DiskInsightViewModel DiskInsight { get; }
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
        Watcher = new SolutionWatcher();
        Watcher.Changed += OnDiskChanged;
        Search = new SearchService();
        NuGet = new NuGetService();
        Run = new RunService();
        Shell = new ShellService();
        Highlight = new SyntaxHighlightService();
        Workspace.Progress += m => Dispatcher.UIThread.Post(() => Status = m);
        Find = new FindResultsViewModel(Search, Solution, OpenAsync);
        Problems = new ProblemsViewModel(Workspace, Solution, EnsureWorkspaceReadyAsync, OpenAsync);
        // Constructed at startup, not lazily on first show: its Changed subscription is the panel's signal log,
        // and it has to be live before any solution opens or the first burst goes unrecorded.
        DiskInsight = new DiskInsightViewModel(Workspace, Watcher, () => Tabs.OfType<EditorTabViewModel>());
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
            await LoadTreeAsync(path);
            SolutionName = Path.GetFileNameWithoutExtension(path);
            Problems.NotifyCanRefreshChanged();
            // (Re)point the OS change feed at this solution — this is also the restart on "Reload solution"
            // and on opening a different solution. If it can't start, the workspace reconcile keeps polling.
            Workspace.DiskIsWatched = Watcher.Start(path);
            Status = $"Loaded {Path.GetFileName(path)} ({Solution.Projects.Count} projects)";
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Rebuilds the tree and the project list from disk. Shared by the initial open and the
    /// structural-change refresh, so the two can't drift. The startup project is re-resolved by path rather
    /// than re-defaulted: an external tool adding a file must not silently move the user's F5 target.</summary>
    private async Task LoadTreeAsync(string path)
    {
        var previousStartup = StartupProject?.Path;
        var projectNodes = await Solution.LoadAsync(path);
        Tree.Clear();
        foreach (var n in projectNodes) Tree.Add(n);
        Projects.Clear();
        foreach (var e in Solution.Projects) Projects.Add(e);
        NuGetVm.SetProjects(Solution.Projects);
        StartupProject = Projects.FirstOrDefault(p =>
                             string.Equals(p.Path, previousStartup, StringComparison.OrdinalIgnoreCase))
                         ?? PickDefaultStartup(Solution.Projects);
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

    // ── Disk-change routing ───────────────────────────────────────────────────────────────────────────
    //
    // One signal, two INDEPENDENT dirty tracks: the workspace's pending-set (drained by the next semantic
    // query) and each tab's IsStale flag (consumed when the tab is next shown). They are separate because a
    // single shared set would let the first consumer to drain it erase the other's mark.
    //
    // Only the visible surfaces react now — the active tab and, on a structural change, the tree. The
    // snapshot stays lazy: reconciling it on every watcher tick would pay the expensive part for edits no
    // query ever asks about.

    /// <summary>Handles one debounced burst from <see cref="Watcher"/>. Arrives on a threadpool thread: the
    /// workspace's dirty-track pushes are thread-safe and happen here, and everything touching tabs or the
    /// tree hops to the UI thread.</summary>
    private void OnDiskChanged(DiskChangeSignal signal)
    {
        if (signal.Overflow || signal.Structural) Workspace.RequestFullRescan();
        else Workspace.MarkPathsChanged(signal.Paths);

        Dispatcher.UIThread.Post(async () =>
        {
            try { await ApplyDiskChangeToViewAsync(signal); }
            catch (Exception ex) { Status = ex.Message; }
        });
    }

    private async Task ApplyDiskChangeToViewAsync(DiskChangeSignal signal)
    {
        // An overflow means the OS dropped events, so no path list can be trusted: every tab is suspect.
        // Marking them stale is cheap — each one costs a stat when it's next shown, not a read.
        var changed = signal.Paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs.OfType<EditorTabViewModel>())
            if (tab.FilePath is not null && (signal.Overflow || changed.Contains(Path.GetFullPath(tab.FilePath))))
                tab.IsStale = true;

        // Structural changes are the one thing the tree can't learn any other way — a new file simply never
        // appears until something rebuilds the nodes. Overflow may have hidden a structural change, so it
        // refreshes too.
        if ((signal.Structural || signal.Overflow) && Solution.SolutionPath is not null)
            await LoadTreeAsync(Solution.SolutionPath);

        // The live push: the file the user is looking at updates the instant it changes, with no focus
        // change. That is the read-alongside-an-agent payoff. Background tabs stay lazy.
        if (ActiveTab is EditorTabViewModel active && active.IsStale) await active.RevalidateFromDiskAsync();
    }

    /// <summary>The lazy half: a background tab is re-read only when it's shown, and only if flagged. The
    /// stamp gate inside means an unchanged file costs a stat, so activating a tab is never a disk read for
    /// nothing.</summary>
    partial void OnActiveTabChanged(TabViewModelBase? value)
    {
        if (value is not EditorTabViewModel { IsStale: true } tab) return;
        _ = RevalidateActivatedTabAsync(tab);
    }

    // Fire-and-forget from a property setter, so it must swallow into the status bar: an exception escaping
    // here would be an unobserved task, not something a caller could catch.
    private async Task RevalidateActivatedTabAsync(EditorTabViewModel tab)
    {
        try { await tab.RevalidateFromDiskAsync(); }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Focus-time safety-net (wired to the window's <c>Activated</c> event). The watcher handles the
    /// steady state now, so this exists for what it might have missed while we were unfocused —
    /// <c>FileSystemWatcher</c> is best-effort and a dropped event has no other backstop.
    ///
    /// <para>Both tracks are re-armed to distrust the feed rather than to re-read: every tab is marked stale
    /// (one stat each, paid lazily on activation) and the snapshot is told to rescan (a manifest walk plus one
    /// stat per document — the "bounded stat-walk" that replaces the old O(bytes-in-the-solution) focus). With
    /// nothing changed since we left, that is zero reads. The active tab and the snapshot (only if already
    /// loaded — focus must never trigger the cold MSBuild load) are brought current now. Errors report to the
    /// status bar rather than throwing out of the <c>async void</c> event handler.</para></summary>
    public async Task RefreshFromDiskAsync()
    {
        try
        {
            foreach (var tab in Tabs.OfType<EditorTabViewModel>()) tab.IsStale = true;
            Workspace.RequestFullRescan();
            if (ActiveTab is EditorTabViewModel active) await active.RevalidateFromDiskAsync();
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

    /// <summary>Brings every open editor tab up to date with disk, eagerly. Reserved for the moments that
    /// deliberately bypass laziness: an explicit "Reload solution", and the refresh a rename does after its own
    /// writes. The steady state goes through the stale flag instead.
    ///
    /// <para>Each tab stamp-gates its own read and content-compares before touching the buffer, so an unchanged
    /// file is a stat and a no-op — no flicker, no caret reset. Covers non-Roslyn tabs (a <c>.md</c>, a
    /// <c>.json</c>) that the snapshot reconcile doesn't. A file that vanished or can't be read is left as-is
    /// until the next refresh.</para></summary>
    private async Task ReloadDriftedTabsAsync()
    {
        foreach (var tab in Tabs.OfType<EditorTabViewModel>().ToList())
            await tab.RevalidateFromDiskAsync();
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
            await ReloadDriftedTabsAsync();
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

        // This is the app's one first-party multi-file write, and it refreshes the view itself below. Mute the
        // watcher over exactly the paths it touches, or the resulting burst would queue a redundant reconcile
        // racing that refresh. The mute expires (it is not consumed), so a later external edit to these same
        // files is still caught.
        Watcher.MuteWrites(SelfWrittenPaths(outcome));

        try { RenameService.ApplyToDisk(outcome); }
        catch (Exception ex) { Status = $"Rename failed writing to disk: {ex.Message}"; return; }

        // Reflect the writes back into the view. The focus reconcile refreshes the snapshot and open tabs on
        // content drift, but it does NOT touch the solution tree, and a moved file's tab is keyed to a path
        // that no longer exists — so a file move needs an explicit tree-node replace + tab re-home here.
        if (outcome.Move is { } move) await ApplyFileMoveToViewAsync(move.OldPath, move.NewPath);
        // Muting the watcher means nothing marked these paths changed, so this reconcile has to ask for the
        // full pass itself — otherwise it would drain an empty pending-set and skip the rename entirely.
        Workspace.RequestFullRescan(); // structural (a move) rebuilds; content-only overlays
        await Workspace.ReconcileWithDiskAsync();
        await ReloadDriftedTabsAsync();

        var count = outcome.ChangedFiles.Count;
        var files = count == 1 ? "file" : "files";
        Status = outcome.Move is { } m
            ? $"Renamed to {newName} across {count} {files}; {Path.GetFileName(m.OldPath)} → {Path.GetFileName(m.NewPath)}"
            : $"Renamed to {newName} across {count} {files}";
    }

    // Every path the rename apply is about to touch: each rewritten file, plus both ends of the move (the
    // File.Move raises events on the source and the destination).
    private static IEnumerable<string> SelfWrittenPaths(RenameOutcome outcome)
    {
        foreach (var file in outcome.ChangedFiles) yield return file.Path;
        if (outcome.Move is not { } move) yield break;
        yield return move.OldPath;
        yield return move.NewPath;
    }

    /// <summary>Propagates a file rename into the view after the disk move: replaces the moved file's tree node
    /// (its <c>Name</c>/<c>Path</c> are <c>init</c>-only, so it can't be mutated in place) and re-homes any tab
    /// open on the old path (its <c>TabId</c> is get-only, so it must be closed and reopened). A case-only
    /// rename leaves the case-folded <c>FileId</c> unchanged, so the tab identity is stable and only the tree
    /// node needs replacing.</summary>
    private async Task ApplyFileMoveToViewAsync(string oldPath, string newPath)
    {
        ReplaceTreeFileNode(oldPath, newPath);

        var oldId = TabViewModelBase.FileId(oldPath);
        if (string.Equals(oldId, TabViewModelBase.FileId(newPath), StringComparison.Ordinal)) return;

        var open = Tabs.FirstOrDefault(t => t.TabId == oldId);
        if (open is null) return;
        var wasActive = ReferenceEquals(ActiveTab, open);
        await CloseTabAsync(open);
        if (wasActive) await OpenFileAsync(newPath);
    }

    private void ReplaceTreeFileNode(string oldPath, string newPath)
    {
        var oldFull = Path.GetFullPath(oldPath);
        foreach (var root in Tree)
            if (TryReplaceFileNode(root, oldFull, newPath)) return;
    }

    // Walks a subtree replacing the file node whose path matches. TreeNode has no parent back-pointer, so the
    // replace happens from the parent while iterating its Children (an ObservableCollection, so the swap is what
    // the UI observes). Case-insensitive path match, matching FindDocument / FileId's normalization.
    private static bool TryReplaceFileNode(TreeNode parent, string oldFull, string newPath)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (child.Kind == NodeKind.File && child.Path is not null &&
                string.Equals(Path.GetFullPath(child.Path), oldFull, StringComparison.OrdinalIgnoreCase))
            {
                parent.Children[i] = new TreeNode { Name = Path.GetFileName(newPath), Kind = NodeKind.File, Path = newPath };
                return true;
            }
            if (TryReplaceFileNode(child, oldFull, newPath)) return true;
        }
        return false;
    }
}
