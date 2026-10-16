using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiniIde.Models;

namespace MiniIde.Services;

/// <summary>Knows how to phrase a <c>dotnet</c> invocation for a <see cref="ProjectEntry"/>, and nothing
/// else: a run's lifetime belongs to the <see cref="RunSession"/> it hands back, not to this service. That
/// is what makes concurrent runs possible — the service holds no "current process", so starting a run
/// neither kills nor can be confused with any other.</summary>
public class RunService
{
    /// <summary>Launches <paramref name="entry"/> under <paramref name="selection"/> and hands back the
    /// session that owns the process. Output (both streams) is pushed to <paramref name="log"/> from the
    /// reader threads, so the callback must be safe to call off the UI thread. Throws if the process can't be
    /// started; the caller surfaces that.</summary>
    public RunSession Start(ProjectEntry entry, RunSelection selection, Action<string> log, CancellationToken ct = default)
    {
        var verb = entry.Kind == ProjectKind.Tst ? "test" : "run";
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(entry.Path)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(verb);
        // VSTest `dotnet test` takes the project path positionally and rejects --project (MSB1001);
        // `dotnet run` requires --project.
        if (entry.Kind == ProjectKind.Tst)
        {
            psi.ArgumentList.Add(entry.Path);
        }
        else
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(entry.Path);
            // The launch profile rides in this same branch for the same reason: `dotnet test` has no
            // --launch-profile at all, so a profile is a run-only argument.
            if (selection.LaunchProfile is { } profile)
            {
                psi.ArgumentList.Add("-lp");
                psi.ArgumentList.Add(profile);
            }
        }
        // The configuration is the one argument both verbs accept.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(selection.Configuration);
        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log("[err] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new RunSession(process, log, ct);
    }
}

/// <summary>One launched run, owning exactly one <c>dotnet</c> process. Everything about the run is read
/// from this handle's own process — never from shared state — so a second run started while this one is
/// live cannot make this one report the wrong exit code (or throw reading one that hasn't happened).</summary>
public sealed class RunSession
{
    private readonly Process _process;

    internal RunSession(Process process, Action<string> log, CancellationToken ct)
    {
        _process = process;
        Completion = WaitForExitAsync(log, ct);
    }

    /// <summary>Completes when this run's process has exited — after its <c>[exit &lt;code&gt;]</c> line has
    /// been logged. Cancelling the token passed to <see cref="RunService.Start"/> stops the run and faults
    /// this with an <see cref="OperationCanceledException"/> instead.</summary>
    public Task Completion { get; }

    /// <summary>Kills this run's process tree, and only this one. A no-op once it has exited, so the stop
    /// and shutdown paths can call it unconditionally.</summary>
    public void Stop()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
    }

    private async Task WaitForExitAsync(Action<string> log, CancellationToken ct)
    {
        await using var _ = ct.Register(Stop);
        await _process.WaitForExitAsync(ct);
        log($"[exit {_process.ExitCode}]");
    }
}
