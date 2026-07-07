using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MiniIde.Models;

namespace MiniIde.Services;

public class RunService
{
    private Process? _current;
    public bool IsRunning => _current is not null && !_current.HasExited;

    public async Task RunAsync(ProjectEntry entry, Action<string> log, CancellationToken ct = default)
    {
        Stop();
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
        // `dotnet run` requires --project. See docs/tickets/complete for the fix-dotnet-test-invocation ticket.
        if (entry.Kind == ProjectKind.Tst)
        {
            psi.ArgumentList.Add(entry.Path);
        }
        else
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(entry.Path);
        }
        _current = Process.Start(psi)!;
        _current.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        _current.ErrorDataReceived += (_, e) => { if (e.Data is not null) log("[err] " + e.Data); };
        _current.BeginOutputReadLine();
        _current.BeginErrorReadLine();
        await using var _ = ct.Register(Stop);
        await _current.WaitForExitAsync(ct);
        log($"[exit {_current.ExitCode}]");
    }

    public void Stop()
    {
        try { if (_current is not null && !_current.HasExited) _current.Kill(entireProcessTree: true); } catch { }
        _current = null;
    }
}
