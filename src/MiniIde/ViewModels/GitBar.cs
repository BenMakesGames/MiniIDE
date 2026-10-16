using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>Pull, push and init, and nothing else. The bar's whole state is one question — is this solution
/// inside a repository, and where is its root — and that question is <b>polled</b> (see
/// <see cref="Refresh"/>'s callers in <see cref="MainWindowViewModel"/>), never signalled.
/// <c>docs/git.md</c> has the reasoning.</summary>
public partial class GitBar : ViewModelBase
{
    private const string GitBusyReason = "A git operation is already running";

    private readonly Func<OutputTabViewModel> _resolveOutputTab;
    private readonly Action<string> _setStatus;

    public GitBar(Func<OutputTabViewModel> resolveOutputTab, Action<string> setStatus)
    {
        _resolveOutputTab = resolveOutputTab;
        _setStatus = setStatus;
    }

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
        if (await RunGitAsync(SolutionDirectory, "init") == 0)
            RepositoryRoot = GitService.FindRepositoryRoot(SolutionDirectory);
    }

    /// <summary>Recomputes the repository state from disk. Called from the three moments that can change it:
    /// a solution opening, the window being activated (<see cref="MainWindowViewModel.RefreshFromDiskAsync"/>,
    /// which is what picks up a repository created or deleted outside the IDE), and our own successful
    /// <c>git init</c>.</summary>
    public void Refresh(string? solutionPath)
    {
        SolutionDirectory = solutionPath is { } path ? Path.GetDirectoryName(path) : null;
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
        if (workingDirectory is null) { _setStatus("No solution open"); return null; }

        var tab = _resolveOutputTab();
        var command = $"git {string.Join(' ', arguments)}";
        // A blank line before every echo but the first, so consecutive transcripts don't run together.
        if (tab.Document.TextLength > 0) tab.Append("");
        tab.Append($"> {command}");

        IsGitBusy = true;
        try
        {
            var exit = await GitService.RunAsync(workingDirectory, arguments, tab.Append);
            _setStatus(exit == 0 ? $"{command} finished" : $"{command} failed (exit {exit})");
            return exit;
        }
        catch (Exception ex)
        {
            // Overwhelmingly: git isn't on PATH. Report it and stay usable rather than throwing out of a command.
            _setStatus($"{command}: {ex.Message}");
            tab.Append($"[{ex.Message}]");
            return null;
        }
        // In a finally so a throw can't leave all three buttons permanently dead.
        finally { IsGitBusy = false; }
    }
}
