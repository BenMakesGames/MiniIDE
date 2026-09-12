using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MiniIde.Services;

/// <summary>The whole of the IDE's git knowledge: where the repository is, and how to run one git command
/// into an output tab. Deliberately tiny — this is pull, push and init, not a VCS integration.
///
/// <para><c>.git</c> is in <see cref="Models.IdeDirectories.Pruned"/> and stays there, so no repository
/// creation or deletion can ever arrive as a <see cref="SolutionWatcher"/> signal. Repository state is
/// therefore <em>polled</em> by the caller at solution-open, window-activation and post-init; see
/// <c>docs/git.md</c>.</para></summary>
public static class GitService
{
    /// <summary>The nearest directory at or above <paramref name="startDirectory"/> that holds a <c>.git</c>
    /// entry, or <c>null</c> when none does up to the filesystem root.
    ///
    /// <para>Answered from the filesystem rather than <c>git rev-parse --show-toplevel</c>: it costs no
    /// process launch (this runs on every window activation) and still gives a correct answer on a machine
    /// with no git installed. A <c>.git</c> <em>file</em> counts as well as a directory — linked worktrees
    /// and submodules store a <c>gitdir:</c> pointer file, and rejecting those would blank the buttons
    /// inside a perfectly valid worktree.</para></summary>
    public static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory)) return null;

        DirectoryInfo? directory;
        // A path that isn't a legal one (or that we can't resolve) is "no repository", not a crash: this is
        // called from a property refresh, not from a command the user could see fail.
        try { directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); }
        catch (Exception) { return null; }

        for (; directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker)) return directory.FullName;
        }
        return null;
    }

    /// <summary>Runs one <c>git</c> command in <paramref name="workingDirectory"/>, pumping both standard
    /// streams line-by-line into <paramref name="log"/>, and returns its exit code. Mirrors
    /// <see cref="NuGetService.RestoreAsync"/>; the two differences that matter are below.
    ///
    /// <para>Throws when git can't be launched at all (not on <c>PATH</c>) and returns a non-zero code for a
    /// command that ran and failed — the caller tells those apart.</para></summary>
    public static async Task<int> RunAsync(
        string workingDirectory, IReadOnlyList<string> arguments, Action<string> log, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Arguments go through ArgumentList, never a joined string — a repository path with a space in it
        // would otherwise arrive at git as two arguments.
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        // No console is attached, so a credential prompt would block forever — and with every git button
        // disabled for the operation's duration that is an unrecoverable UI state. This makes git fail fast
        // with a message into the tab instead. (Environment requires UseShellExecute = false, which we set
        // above for the redirects anyway.) Git Credential Manager may still raise its own GUI dialog for an
        // HTTPS remote; that is a window the user can answer or dismiss, not a hang.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
