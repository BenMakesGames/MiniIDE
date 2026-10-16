using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

    /// <summary>The left panel: one card per project, in the solution's (name-sorted) order.</summary>
    public ObservableCollection<ProjectItemViewModel> Projects { get; } = new();
    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();

    [ObservableProperty] private TabViewModelBase? _activeTab;
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
            RefreshGitState();
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
        foreach (var card in cards)
            foreach (var run in _liveRuns.Where(r => SamePath(_runProjects[r], card.Path)))
                if (!card.LiveRuns.Contains(run)) card.LiveRuns.Add(run);
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
            foreach (var tab in Tabs.OfType<NuGetTabViewModel>()) tab.ReloadPackages();
        }
        catch (Exception ex) { Status = ex.Message; }
    });

    private static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private ProjectItemViewModel? CardFor(string projectPath) =>
        Projects.FirstOrDefault(p => SamePath(p.Path, projectPath));

    // ── Tabs ──────────────────────────────────────────────────────────────────────────────────────────

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

        AddTab(tab);
        ActiveTab = tab;
    }

    // Every tab enters through here, so none can be added without its close wiring.
    private void AddTab(TabViewModelBase tab)
    {
        tab.RequestClose += CloseTabAsync;
        Tabs.Add(tab);
    }

    /// <summary>Activates <paramref name="tab"/>, putting it back on the strip first if it was closed — which is
    /// how a run whose tab was closed is reached again from its project card.</summary>
    public void ShowTab(TabViewModelBase tab)
    {
        if (!Tabs.Contains(tab)) Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>Gets-or-creates the tab with identity <paramref name="tabId"/> and activates it. Dedup keys on
    /// <see cref="TabViewModelBase.TabId"/>, never the header, so two tabs can share a title while staying
    /// distinct.</summary>
    private T ShowOrCreateTab<T>(string tabId, Func<T> create) where T : TabViewModelBase
    {
        var tab = Tabs.OfType<T>().FirstOrDefault(t => t.TabId == tabId);
        if (tab is null)
        {
            tab = create();
            AddTab(tab);
        }
        ActiveTab = tab;
        return tab;
    }

    private OutputTabViewModel ShowOutputTab(string tabId, string header) =>
        ShowOrCreateTab(tabId, () => new OutputTabViewModel(tabId, header));

    // Returns Task (not async) so it still satisfies the RequestClose Func<,Task> contract: with save-on-close
    // gone under the read-only law, nothing here awaits.
    private Task CloseTabAsync(TabViewModelBase tab)
    {
        // A run tab's process deliberately survives its tab: only the tab's own Stop ends a run. Its card keeps
        // a button for it, which is the way back. An analysis, on the other hand, has nobody left to report to.
        (tab as ProblemsTabViewModel)?.Cancel();
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        return Task.CompletedTask;
    }

    /// <summary>Editor tabs revalidate when shown. The stamp gate inside means an unchanged file costs a stat,
    /// so activating a tab is never a disk read for nothing.</summary>
    partial void OnActiveTabChanged(TabViewModelBase? oldValue, TabViewModelBase? newValue)
    {
        (oldValue as FileWatcherTabViewModel)?.StopPolling();
        (newValue as FileWatcherTabViewModel)?.StartPolling();
        if (newValue is EditorTabViewModel tab) _ = RevalidateAsync(tab);
    }

    // Fire-and-forget from a property setter, so it must swallow into the status bar: an exception escaping
    // here would be an unobserved task, not something a caller could catch.
    private async Task RevalidateAsync(EditorTabViewModel tab)
    {
        try { await tab.RevalidateFromDiskAsync(); }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Focus-time refresh (wired to the window's <c>Activated</c> event): whatever the user did
    /// elsewhere — an agent editing the file on screen, a <c>git init</c> — shows when they come back. Cheap: a
    /// stat for the active tab and a directory probe for git.</summary>
    public async Task RefreshFromDiskAsync()
    {
        RefreshGitState();
        if (ActiveTab is EditorTabViewModel active) await RevalidateAsync(active);
    }

    /// <summary>Backs the explicit "Reload solution" command's snapshot half: forces a full workspace rebuild
    /// and reloads every open editor tab. The project list is the command's own job.</summary>
    public async Task ReloadWorkspaceAsync()
    {
        try
        {
            await Workspace.ReloadIfLoadedAsync();
            await ReloadEditorTabsAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
    }

    /// <summary>Brings every open editor tab up to date with disk, eagerly — for the moments that deliberately
    /// bypass revalidate-on-show: an explicit "Reload solution", and the refresh a rename does after its own
    /// writes. Each tab stamp-gates its own read and content-compares before touching the buffer, so an
    /// unchanged file is a stat and a no-op — no flicker, no caret reset.</summary>
    private async Task ReloadEditorTabsAsync()
    {
        foreach (var tab in Tabs.OfType<EditorTabViewModel>().ToList())
            await tab.RevalidateFromDiskAsync();
    }

    // ── Project actions (IProjectHost) ────────────────────────────────────────────────────────────────

    /// <summary>Runs on the project's Problems tab, creating it on first use. Re-asking refreshes the existing
    /// tab rather than opening a second one — that is what "Find problems" means a second time.</summary>
    public void FindProblems(ProjectItemViewModel project)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        var tab = ShowOrCreateTab(ProblemsTabViewModel.TabIdFor(project.Path),
            () => new ProblemsTabViewModel(project.Entry, Workspace, EnsureWorkspaceReadyAsync, OpenAsync));
        if (tab.RefreshCommand.CanExecute(null)) tab.RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private void ShowAbout() => ShowOrCreateTab(AboutTabViewModel.Id, () => new AboutTabViewModel());

    [RelayCommand]
    private void ShowFileWatcher() =>
        ShowOrCreateTab(FileWatcherTabViewModel.Id, () => new FileWatcherTabViewModel(Watcher));

    public void ManageNuGet(ProjectItemViewModel project) =>
        ShowOrCreateTab(NuGetTabViewModel.TabIdFor(project.Path),
            () => new NuGetTabViewModel(project.Entry, NuGet,
                () => ShowOutputTab("nuget:", "NuGet - Output"),
                ShowOutputTab));

    // ── Runs ──────────────────────────────────────────────────────────────────────────────────────────
    //
    // A run tab owns one process at a time. Closing the tab doesn't stop it; the tab's Stop does, and so does
    // the IDE closing. While a run is live, its project card shows a button that brings its tab back.

    /// <summary>Every run whose process is still going, whether or not its tab is open. The shutdown path's
    /// list: run children are started with <c>CreateNoWindow</c>, so Windows does not reap them with us.</summary>
    private readonly List<OutputTabViewModel> _liveRuns = new();

    /// <summary>Which project each run tab belongs to, by path — a run outlives any one card instance.</summary>
    private readonly Dictionary<OutputTabViewModel, string> _runProjects = new();

    /// <summary>How many runs this session has started per project <em>and</em> selection — the same things
    /// the header names, so the numbering groups exactly as the labels read. Monotonic: a number is never handed
    /// out twice, so a header always names the same run it did a minute ago.</summary>
    private readonly Dictionary<string, int> _runsStarted = new(StringComparer.Ordinal);

    /// <summary>Opens a brand-new tab that owns its own process and launches the run into it, so repeat runs
    /// are concurrent instances rather than replacements. The tab keeps the project and selection it was born
    /// with, which is what lets its own Run button restart the same thing after it exits.</summary>
    public void StartRun(ProjectItemViewModel project, RunSelection selection)
    {
        var entry = project.Entry;
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
        AddTab(tab);
        ActiveTab = tab;
        Launch(entry, selection, tab, label);
    }

    /// <summary>Keeps the live-run lists in step with each tab's process — including a run restarted from its
    /// own tab, which brings the card's button back.</summary>
    private void OnRunTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OutputTabViewModel.IsRunLive) || sender is not OutputTabViewModel tab) return;
        var card = CardFor(_runProjects[tab]);
        if (tab.IsRunLive)
        {
            if (!_liveRuns.Contains(tab)) _liveRuns.Add(tab);
            if (card is not null && !card.LiveRuns.Contains(tab)) card.LiveRuns.Add(tab);
        }
        else
        {
            _liveRuns.Remove(tab);
            card?.LiveRuns.Remove(tab);
        }
    }

    /// <summary>Starts one process into <paramref name="tab"/> and returns as soon as it is launched — the
    /// waiting is done by a detached continuation, so nothing that starts a run stays busy for its lifetime.</summary>
    private void Launch(ProjectEntry project, RunSelection selection, OutputTabViewModel tab, string label)
    {
        // The status bar is last-write-wins and concurrent runs share it, so every run message names its
        // project and selection — whichever one wins is at least attributable.
        Status = project.Kind == ProjectKind.Tst ? $"Testing {label}..." : $"Running {label}...";
        try
        {
            var session = Run.Start(project, selection, tab.Append);
            tab.AttachRun(session);
            _ = ReportExitAsync(session, label);
        }
        catch (Exception ex) { Status = $"{label}: {ex.Message}"; }
    }

    // Started from the UI thread, so the continuation resumes there (Avalonia installs a SynchronizationContext)
    // and Status is written on the thread its bindings expect.
    private async Task ReportExitAsync(RunSession session, string label)
    {
        try
        {
            await session.Completion;
            Status = $"{label} finished";
        }
        catch (Exception ex) { Status = $"{label}: {ex.Message}"; }
    }

    /// <summary>Kills every run still going, open tab or not. Wired to the window's <c>Closed</c>.</summary>
    public void StopAllRuns()
    {
        foreach (var tab in _liveRuns.ToList()) tab.StopRun();
    }

    // ── Git ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // Pull, push and init, and nothing else. The bar's whole state is one question — is this solution inside
    // a repository, and where is its root — and that question is POLLED (see RefreshGitState's callers), never
    // signalled. docs/git.md has the reasoning.

    /// <summary>The open solution's own directory, or null when no solution is open. Observable because the
    /// bar distinguishes "no solution" (a disabled pull/push pair) from "a solution that isn't in a repo"
    /// (the +Git button), and <see cref="SolutionService.SolutionPath"/> raises nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersGitInit), nameof(PullTip), nameof(PushTip))]
    [NotifyCanExecuteChangedFor(nameof(GitInitCommand))]
    private string? _solutionDirectory;

    /// <summary>The repository root the git commands run in — the nearest ancestor of the solution directory
    /// holding a <c>.git</c>, or null when there is none (or no solution). Not the solution directory: a
    /// solution in <c>&lt;repo&gt;/src</c> must pull the outer repository, not create a nested one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffersGitInit), nameof(PullTip), nameof(PushTip))]
    [NotifyCanExecuteChangedFor(nameof(GitPullCommand), nameof(GitPushCommand), nameof(GitInitCommand))]
    private string? _repositoryRoot;

    /// <summary>Whether a git operation is in flight. All three commands share it, so a pull can't be raced
    /// by a push into the same tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PullTip), nameof(PushTip), nameof(InitTip))]
    [NotifyCanExecuteChangedFor(nameof(GitPullCommand), nameof(GitPushCommand), nameof(GitInitCommand))]
    private bool _isGitBusy;

    /// <summary>Whether the bar shows <b>+Git</b> instead of the pull/push pair. True only when a solution is
    /// open <em>and</em> it is not in a repository — which is what makes the no-solution case fall through to
    /// a disabled pair rather than offering to initialize a repository nowhere.</summary>
    public bool OffersGitInit => SolutionDirectory is not null && RepositoryRoot is null;

    public string PullTip => GitTip("Pull from the remote (git pull)");
    public string PushTip => GitTip("Push to the remote (git push)");
    public string InitTip => IsGitBusy ? GitBusyReason : "Create a git repository here (git init)";

    private const string GitBusyReason = "A git operation is already running";

    // Why pull/push are greyed out, when they are — a disabled button with no reason on it is a dead control.
    private string GitTip(string enabledText) =>
        IsGitBusy ? GitBusyReason
        : SolutionDirectory is null ? "Open a solution to use git"
        : RepositoryRoot is null ? "This solution is not in a git repository"
        : enabledText;

    private bool CanRunGitOnRepository => RepositoryRoot is not null && !IsGitBusy;
    private bool CanGitInit => SolutionDirectory is not null && RepositoryRoot is null && !IsGitBusy;

    [RelayCommand(CanExecute = nameof(CanRunGitOnRepository))]
    private Task GitPullAsync() => RunGitAsync(RepositoryRoot, "pull");

    [RelayCommand(CanExecute = nameof(CanRunGitOnRepository))]
    private Task GitPushAsync() => RunGitAsync(RepositoryRoot, "push");

    /// <summary>Initializes a repository in the <em>solution</em> directory. Safe to target that rather than
    /// an ancestor because the button is only offered when no ancestor is a repository — so there is no way
    /// to nest one by accident.</summary>
    [RelayCommand(CanExecute = nameof(CanGitInit))]
    private async Task GitInitAsync()
    {
        // Only a clean exit means a repository now exists; a failed init must leave +Git on the bar.
        if (await RunGitAsync(SolutionDirectory, "init") == 0) RefreshGitState();
    }

    /// <summary>Recomputes the repository state from disk. Called from the three moments that can change it:
    /// a solution opening, the window being activated (<see cref="RefreshFromDiskAsync"/>, which is what
    /// picks up a repository created or deleted outside the IDE), and our own successful <c>git init</c>.</summary>
    private void RefreshGitState()
    {
        SolutionDirectory = Solution.SolutionPath is { } path ? Path.GetDirectoryName(path) : null;
        RepositoryRoot = GitService.FindRepositoryRoot(SolutionDirectory);
    }

    /// <summary>The one path all three commands funnel through: activate the shared <c>Git - Output</c> tab,
    /// echo the command, run it, report the outcome. Returns the exit code, or null when git could not be
    /// launched at all.
    ///
    /// <para>The buffer is appended to, never cleared — the Git tab is a transcript of the session's git
    /// activity, and each entry is legible because its own command line sits above it.</para></summary>
    private async Task<int?> RunGitAsync(string? workingDirectory, params string[] arguments)
    {
        if (workingDirectory is null) { Status = "No solution open"; return null; }

        var tab = ShowOutputTab("git:", "Git - Output");
        var command = $"git {string.Join(' ', arguments)}";
        // A blank line before every echo but the first, so consecutive transcripts don't run together.
        if (tab.Document.TextLength > 0) tab.Append("");
        tab.Append($"> {command}");

        IsGitBusy = true;
        try
        {
            var exit = await GitService.RunAsync(workingDirectory, arguments, tab.Append);
            Status = exit == 0 ? $"{command} finished" : $"{command} failed (exit {exit})";
            return exit;
        }
        catch (Exception ex)
        {
            // Overwhelmingly: git isn't on PATH. Report it and stay usable rather than throwing out of a command.
            Status = $"{command}: {ex.Message}";
            tab.Append($"[{ex.Message}]");
            return null;
        }
        // In a finally so a throw can't leave all three buttons permanently dead.
        finally { IsGitBusy = false; }
    }

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
            await ReloadEditorTabsAsync();
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
        if (outcome.Move is { } move) await RehomeMovedTabAsync(move.OldPath, move.NewPath);
        await Workspace.ReconcileWithDiskAsync();
        await ReloadEditorTabsAsync();

        var count = outcome.ChangedFiles.Count;
        var files = count == 1 ? "file" : "files";
        Status = outcome.Move is { } m
            ? $"Renamed to {newName} across {count} {files}; {Path.GetFileName(m.OldPath)} → {Path.GetFileName(m.NewPath)}"
            : $"Renamed to {newName} across {count} {files}";
    }

    /// <summary>Re-homes any tab open on a renamed file (its <c>TabId</c> is get-only, so it must be closed and
    /// reopened). A case-only rename leaves the case-folded <c>FileId</c> unchanged, so nothing needs doing.</summary>
    private async Task RehomeMovedTabAsync(string oldPath, string newPath)
    {
        var oldId = TabViewModelBase.FileId(oldPath);
        if (string.Equals(oldId, TabViewModelBase.FileId(newPath), StringComparison.Ordinal)) return;

        var open = Tabs.FirstOrDefault(t => t.TabId == oldId);
        if (open is null) return;
        var wasActive = ReferenceEquals(ActiveTab, open);
        await CloseTabAsync(open);
        if (wasActive) await OpenFileAsync(newPath);
    }
}
