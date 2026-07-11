using System;
using System.Diagnostics;

namespace MiniIde.Services;

/// <summary>OS-process launches for the path context-menu actions (Explorer reveal, terminal-with-Claude).
/// View/VM-agnostic: it never touches Avalonia or the window — callers surface any thrown failure to the UI.
/// Uses <c>UseShellExecute = true</c> (visible window), unlike <see cref="RunService"/>/<see cref="NuGetService"/>
/// which pipe output windowless.</summary>
public sealed class ShellService
{
    // Set once wt.exe fails to launch (absent execution alias); subsequent invocations skip straight to
    // PowerShell for the app's lifetime. Resetting between app runs is fine.
    private bool _wtUnavailable;

    /// <summary>Reveals a path in Explorer. Folders open into the folder; files/projects are revealed-and-selected
    /// via <c>/select,</c>. Throws on launch failure.</summary>
    public void RevealInExplorer(string path, bool isFolder)
    {
        var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
        if (isFolder) psi.ArgumentList.Add(path);
        else { psi.ArgumentList.Add("/select,"); psi.ArgumentList.Add(path); }
        Process.Start(psi);
    }

    /// <summary>Opens a terminal in <paramref name="directory"/> running <c>claude</c>. Prefers Windows Terminal;
    /// on a wt.exe launch failure (absent execution alias → <see cref="System.ComponentModel.Win32Exception"/>)
    /// falls back to PowerShell for the app's lifetime. Throws only when PowerShell also fails to launch — a
    /// successful wt <em>or</em> PowerShell launch is a success.</summary>
    public void OpenTerminalWithClaude(string directory)
    {
        if (!_wtUnavailable)
        {
            try
            {
                var wt = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = true };
                wt.ArgumentList.Add("-d");
                wt.ArgumentList.Add(directory);
                wt.ArgumentList.Add("claude");
                Process.Start(wt);
                return;
            }
            catch (Exception) { _wtUnavailable = true; } // wt not installed — fall through to PowerShell
        }

        var ps = new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = directory, UseShellExecute = true };
        ps.ArgumentList.Add("-NoExit");
        ps.ArgumentList.Add("-Command");
        ps.ArgumentList.Add("claude");
        Process.Start(ps);
    }
}
