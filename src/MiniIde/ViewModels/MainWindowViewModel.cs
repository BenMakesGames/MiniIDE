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

    /// <summary>The Run flyout's two groups. Both are rebuilt only when their <em>inputs</em> change (a
    /// solution load, a different startup project); picking a row mutates the rows' <see cref="RunChoice.IsSelected"/>
    /// instead, so the collections stay stable while the flyout is open.</summary>
    public ObservableCollection<RunChoice> Configurations { get; } = new();
    public ObservableCollection<RunChoice> LaunchProfiles { get; } = new();

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
            RefreshGitState();
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
        // Re-resolved by name for the same reason the startup project is re-resolved by path, with sharper
        // teeth: `dotnet run -c <gone>` doesn't fail, it quietly builds a DEBUG-less binary into a bin folder
        // of that name. So a configuration whose name has disappeared falls back rather than being carried.
        SelectedConfiguration = PickConfiguration(SelectedConfiguration);
        StartupProject = Projects.FirstOrDefault(p =>
                             string.Equals(p.Path, previousStartup, StringComparison.OrdinalIgnoreCase))
                         ?? PickDefaultStartup(Solution.Projects);
        // Unconditional, because a reload that leaves the startup project unchanged raises no
        // OnStartupProjectChanged and would otherwise keep the previous solution's configurations.
        RefreshRunOptions();
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
        // A closing output tab takes its own run down with it (no confirmation dialog yet). Nothing consults
        // shared state, so every other live run keeps going.
        (tab as OutputTabViewModel)?.StopRun();
        // No save-on-close: the editor is a read-only window onto disk, so a closing tab has nothing to persist.
        Tabs.Remove(tab);
        if (ActiveTab == tab) ActiveTab = Tabs.Count > 0 ? Tabs[0] : null;
        return Task.CompletedTask;
    }

    /// <summary>Kills every run still going. Wired to the window's <c>Closed</c>: run children are started with
    /// <c>UseShellExecute = false</c> / <c>CreateNoWindow</c>, so Windows does not reap them with us and each
    /// live run would otherwise outlive the IDE as an orphan.</summary>
    public void StopAllRuns()
    {
        foreach (var tab in Tabs.OfType<OutputTabViewModel>()) tab.StopRun();
    }

    /// <summary>How many runs this session has started per project <em>and</em> selection, keyed by normalized
    /// path plus configuration and profile — the same things the header names, so the numbering groups exactly
    /// as the labels read. Monotonic: a number is never handed out twice, so closing "(2)" doesn't make the
    /// next run reuse that label and a header always names the same run it did a minute ago.</summary>
    private readonly Dictionary<string, int> _runsStarted = new(StringComparer.Ordinal);

    /// <summary>Allocates the identity and header for one fresh run tab. The id stays in the <c>run:</c>
    /// namespace <see cref="TabViewModelBase.TabId"/> documents, made unique per invocation; the header is
    /// plain for a project's first run under a selection and numbered from its second on.</summary>
    private (string TabId, string Header) NextRunTab(string projectPath, string label, RunSelection selection)
    {
        var key = $"{Path.GetFullPath(projectPath).ToLowerInvariant()}|{selection.Configuration}|{selection.LaunchProfile}";
        var n = _runsStarted[key] = _runsStarted.GetValueOrDefault(key) + 1;
        return ($"run:{key}#{n}", n == 1 ? $"{label} - Output" : $"{label} - Output ({n})");
    }

    private bool CanPlay() => StartupProject?.IsRunnable == true;

    /// <summary>Runs the startup project under the header's current selection. Nothing here is gated on a run
    /// already being live: repeat presses are meant to launch concurrent instances, each in its own tab.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    public void Play()
    {
        if (StartupProject is not { } project) { Status = "No startup project"; return; }
        StartRun(project, SelectionFor(project, SelectedConfiguration, SelectedLaunchProfile));
    }

    /// <summary>Runs <paramref name="project"/> under <paramref name="configuration"/> — the solution tree's
    /// right-click Run. Deliberately writes none of the header's selections: the tree is a way to run something
    /// once, not a way to re-point the F5 target.</summary>
    /// <param name="configuration">A name <see cref="SolutionService.Configurations"/> declares. An unknown one
    /// does not fail — <c>dotnet run -c &lt;unknown&gt;</c> quietly builds a DEBUG-less binary — so callers must
    /// offer only what the solution has.</param>
    public void RunProject(ProjectEntry project, string configuration)
    {
        // The header's launch profile belongs to the startup project: it was read from that project's
        // launchSettings.json, and naming a profile another project doesn't declare is a hard `dotnet run`
        // error. So it carries only when the tree row *is* the startup project — which is exactly the case
        // where "same as the Run button" has to mean the same process arguments too.
        var profile = project == StartupProject ? SelectedLaunchProfile : null;
        StartRun(project, SelectionFor(project, configuration, profile));
    }

    // A test project runs under `dotnet test`, which has no --launch-profile at all. Dropping the profile in
    // this one place keeps it authoritative rather than trusting each entry point to have offered none.
    private static RunSelection SelectionFor(ProjectEntry project, string configuration, string? launchProfile) =>
        new(configuration, project.Kind == ProjectKind.Tst ? null : launchProfile);

    /// <summary>Opens a brand-new tab that owns its own process and launches the run into it, so repeat runs
    /// are concurrent instances rather than replacements. The tab keeps the project and selection it was born
    /// with, which is what lets its own Run button restart the same thing after it exits.</summary>
    private void StartRun(ProjectEntry project, RunSelection selection)
    {
        var name = Path.GetFileNameWithoutExtension(project.Path);
        var label = $"{name} [{Describe(selection)}]";
        var (tabId, header) = NextRunTab(project.Path, label, selection);
        var tab = new OutputTabViewModel(tabId, header, t => Launch(project, selection, t, label));
        tab.RequestClose += CloseTabAsync;
        Tabs.Add(tab);
        ActiveTab = tab;
        Launch(project, selection, tab, label);
    }

    /// <summary>Starts one process into <paramref name="tab"/> and returns as soon as it is launched — the
    /// waiting is done by a detached continuation. Nothing that starts a run may stay busy for the run's
    /// lifetime: an async command that awaited the process would sit disabled until it exited, which is the
    /// one thing a second, concurrent run needs the Run button not to do.</summary>
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

    partial void OnStartupProjectChanged(ProjectEntry? value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        RefreshRunOptions();
    }

    // ── Run selection ─────────────────────────────────────────────────────────────────────────────────
    //
    // Two independent axes, never one merged list: a configuration decides what is compiled (`-c`), a profile
    // decides what environment it launches into (`-lp`). Both are session-only — there is no settings store
    // anywhere in the app, and the startup project is likewise only preserved across in-session reloads.

    private const string DefaultConfiguration = "Debug";

    /// <summary>Label for the profile row that means "pass no <c>-lp</c> at all", which is where every session
    /// starts and the only way back once a profile has been picked.</summary>
    private const string NoProfileLabel = "(dotnet default)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunLabel))]
    private string _selectedConfiguration = DefaultConfiguration;

    [ObservableProperty] private string? _selectedLaunchProfile;

    /// <summary>Whether the flyout has a profile group to show. False for a project with no launch settings and
    /// for every test project, since <c>dotnet test</c> takes no profile.</summary>
    [ObservableProperty] private bool _hasLaunchProfiles;

    /// <summary>The split button's primary label. It names the configuration so a Release run is not
    /// indistinguishable from a Debug one before it starts; the profile stays in the flyout, where the
    /// button's width isn't at stake.</summary>
    public string RunLabel => $"▶ Run ({SelectedConfiguration})";

    [RelayCommand]
    private void SelectConfiguration(string? configuration)
    {
        if (configuration is not null) SelectedConfiguration = configuration;
    }

    [RelayCommand]
    private void SelectLaunchProfile(string? profile) => SelectedLaunchProfile = profile;

    partial void OnSelectedConfigurationChanged(string value) => SyncRunOptionSelection();
    partial void OnSelectedLaunchProfileChanged(string? value) => SyncRunOptionSelection();

    /// <summary>Re-resolves what the flyout can offer and rebuilds both groups. Called after a solution load
    /// and whenever the startup project changes, because those two events own the inputs: the solution owns
    /// the configurations, the startup project owns the profiles.</summary>
    private void RefreshRunOptions()
    {
        var profiles = StartupProject is { Kind: not ProjectKind.Tst } project
            ? LaunchProfileReader.Read(project.Path)
            : Array.Empty<string>();

        // Passing a profile the project doesn't have is a hard error (unlike an unknown configuration, which
        // fails silently), so a carried-over name is dropped rather than risked.
        if (SelectedLaunchProfile is { } selected && !profiles.Contains(selected, StringComparer.Ordinal))
            SelectedLaunchProfile = null;

        Configurations.Clear();
        foreach (var configuration in Solution.Configurations)
            Configurations.Add(new RunChoice(configuration, configuration, SelectConfigurationCommand,
                string.Equals(configuration, SelectedConfiguration, StringComparison.OrdinalIgnoreCase)));

        LaunchProfiles.Clear();
        if (profiles.Count > 0)
        {
            LaunchProfiles.Add(new RunChoice(NoProfileLabel, null, SelectLaunchProfileCommand, SelectedLaunchProfile is null));
            foreach (var profile in profiles)
                LaunchProfiles.Add(new RunChoice(profile, profile, SelectLaunchProfileCommand,
                    string.Equals(profile, SelectedLaunchProfile, StringComparison.Ordinal)));
        }
        HasLaunchProfiles = LaunchProfiles.Count > 0;
    }

    /// <summary>Moves the radio marks to the current selections without touching either collection — see
    /// <see cref="RunChoice.IsSelected"/> for why the rows are updated rather than rebuilt.</summary>
    private void SyncRunOptionSelection()
    {
        foreach (var choice in Configurations)
            choice.IsSelected = string.Equals(choice.Value, SelectedConfiguration, StringComparison.OrdinalIgnoreCase);
        foreach (var choice in LaunchProfiles)
            choice.IsSelected = string.Equals(choice.Value, SelectedLaunchProfile, StringComparison.Ordinal);
    }

    /// <summary>The configuration to select given <paramref name="preferred"/>: itself when the solution still
    /// declares it, otherwise <c>Debug</c>, otherwise whatever the solution does declare. Never returns a name
    /// the solution doesn't have — that is the whole point.</summary>
    private string PickConfiguration(string? preferred)
    {
        var configurations = Solution.Configurations;
        foreach (var c in configurations)
            if (string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var c in configurations)
            if (string.Equals(c, DefaultConfiguration, StringComparison.OrdinalIgnoreCase)) return c;
        return configurations.Count > 0 ? configurations[0] : DefaultConfiguration;
    }

    /// <summary>How a selection reads in a tab header and a status message. One helper so the two can't
    /// drift.</summary>
    private static string Describe(RunSelection selection) =>
        selection.LaunchProfile is { } profile
            ? $"{selection.Configuration}, {profile}"
            : selection.Configuration;


    // ── Git ───────────────────────────────────────────────────────────────────────────────────────────
    //
    // Pull, push and init, and nothing else. The bar's whole state is one question — is this solution inside
    // a repository, and where is its root — and `.git` is pruned out of the disk feed by design, so that
    // question is POLLED (see RefreshGitState's callers), never signalled. docs/git.md has the reasoning.

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
    /// picks up a repository created or deleted outside the IDE), and our own successful <c>git init</c>.
    /// Deliberately not wired to <see cref="Watcher"/> — <c>.git</c> is pruned, so it produces no signal to
    /// wire to.</summary>
    private void RefreshGitState()
    {
        SolutionDirectory = Solution.SolutionPath is { } path ? Path.GetDirectoryName(path) : null;
        RepositoryRoot = GitService.FindRepositoryRoot(SolutionDirectory);
    }

    /// <summary>Gets-or-creates the one shared <c>Git - Output</c> tab and activates it. Same shape as
    /// <see cref="ResolveNuGetOutput"/>, including passing no starter: that leaves <c>HasRun</c> false, so
    /// the tab shows no run strip.</summary>
    private OutputTabViewModel ResolveGitOutput()
    {
        var tab = GetOrCreateOutputTab("git:", "Git - Output");
        ActiveTab = tab;
        return tab;
    }

    /// <summary>The one path all three commands funnel through: activate the tab, echo the command, run it,
    /// report the outcome. Returns the exit code, or null when git could not be launched at all.
    ///
    /// <para>The buffer is appended to, never cleared — the Git tab is a transcript of the session's git
    /// activity, and each entry is legible because its own command line sits above it.</para></summary>
    private async Task<int?> RunGitAsync(string? workingDirectory, params string[] arguments)
    {
        if (workingDirectory is null) { Status = "No solution open"; return null; }

        var tab = ResolveGitOutput();
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
            // First, and cheap: `.git` is pruned out of the watcher, so a repository created or deleted
            // outside the IDE can only ever be learned about here. Ahead of the awaits so a failing
            // reconcile can't swallow it.
            RefreshGitState();
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
    /// owns its own results and status; this just decides what to hand it. <paramref name="subject"/> is the
    /// clicked identifier text, shown in the panel's context banner (<c>Usages of "X"</c>).</summary>
    public async Task FindReferencesAsync(string file, int caretOffset, string? subject)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace...";
        await EnsureWorkspaceReadyAsync();
        Status = "Finding references...";
        var references = await Workspace.FindReferencesAsync(file, caretOffset);
        if (references is null) { Find.ShowNoResults("No symbol found"); Status = "No symbol found"; return; }
        Find.ShowResults(references, "reference", $"Usages of \"{subject}\"");
        Status = Find.Status;
    }

    /// <summary>Fills the Find panel with the in-source implementers/overrides of the symbol at
    /// <paramref name="caretOffset"/>. Mirrors <see cref="FindReferencesAsync"/>: the panel owns its results and
    /// status; this decides what to hand it. <paramref name="subject"/> feeds the context banner.</summary>
    public async Task FindImplementationsAsync(string file, int caretOffset, string? subject)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace (first use may take a while)...";
        await EnsureWorkspaceReadyAsync();
        Status = "Finding implementations...";
        var results = await Workspace.FindImplementationsAsync(file, caretOffset);
        if (results is null) { Find.ShowNoResults("No symbol found"); Status = "No symbol found"; return; }
        Find.ShowResults(results, "implementation", $"Implementations of \"{subject}\"");
        Status = Find.Status;
    }

    /// <summary>Fills the Find panel with the in-source derived types (transitively) of the type at
    /// <paramref name="caretOffset"/>. Mirrors <see cref="FindReferencesAsync"/>. <paramref name="subject"/>
    /// feeds the context banner.</summary>
    public async Task FindSubclassesAsync(string file, int caretOffset, string? subject)
    {
        if (Solution.SolutionPath is null) { Status = "No solution open"; return; }
        Status = "Loading workspace (first use may take a while)...";
        await EnsureWorkspaceReadyAsync();
        Status = "Finding subclasses...";
        var results = await Workspace.FindSubclassesAsync(file, caretOffset);
        if (results is null) { Find.ShowNoResults("No symbol found"); Status = "No symbol found"; return; }
        Find.ShowResults(results, "subclass", $"Subclasses of \"{subject}\"", "es");
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
