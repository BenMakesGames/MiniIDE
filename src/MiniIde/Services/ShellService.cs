using System;
using System.ComponentModel;
using System.Diagnostics;

namespace MiniIde.Services;

/// <summary>OS-process launches for the path context-menu actions (Explorer reveal, external-app open, terminal-with-Claude).
/// View/VM-agnostic: it never touches Avalonia or the window — callers surface any thrown failure to the UI.
/// Uses <c>UseShellExecute = true</c> (visible window), unlike <see cref="RunService"/>/<see cref="NuGetService"/>
/// which pipe output windowless.</summary>
public sealed class ShellService
{
    // Win32 error the shell reports when the user dismisses a dialog it raised on our behalf (e.g. closing
    // the "How do you want to open this file?" picker). A deliberate choice, not a launch failure.
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED

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

    /// <summary>Opens a path with the program Windows has registered for its extension — the shell's default
    /// verb, exactly as double-clicking it in Explorer would. The <em>shell</em> owns the outcome: with
    /// <c>ErrorDialog = true</c> it raises its own UI for anything it can answer itself — most importantly the
    /// "How do you want to open this file?" picker when the extension resolves to an application that is not
    /// installed — parented to <paramref name="ownerWindow"/>.
    /// <para><b>Blocks until any dialog the shell raises is dismissed</b> — with no timeout (measured: 206s for a picker left
    /// open), so callers must never invoke this on a UI thread.</para>
    /// <para>Dismissing that dialog is a user decision, not a failure, and returns normally. Any other launch
    /// failure throws.</para></summary>
    /// <param name="path">The file to open.</param>
    /// <param name="ownerWindow">Native handle to own the shell's dialogs; <see cref="IntPtr.Zero"/> for unparented.</param>
    public void OpenWithRegisteredApp(string path, IntPtr ownerWindow)
    {
        // No explicit program: with UseShellExecute the path IS the FileName and the shell resolves the handler.
        // ErrorDialog defaults to FALSE, which makes .NET set SEE_MASK_FLAG_NO_UI and suppress the shell's own
        // prompts — turning a recoverable "pick an app" into a silent dead-end. Setting it true is the fix.
        var psi = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            ErrorDialog = true,
            ErrorDialogParentHandle = ownerWindow
        };
        try { Process.Start(psi); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { /* user closed the picker */ }
    }

    /// <summary>Opens a terminal in <paramref name="directory"/>. Tries, in order, <c>wt.exe</c> (Windows Terminal),
    /// <c>pwsh.exe</c> (PowerShell 7+), then <c>powershell.exe</c> (Windows PowerShell, always present on Windows).
    /// Each candidate is attempted on every call — no caching — so an install or uninstall between calls is picked
    /// up immediately. The user-facing label is "Windows Terminal" since that is the preferred host; the fallbacks
    /// are the honest best-effort when wt isn't resolvable. Throws only when the final <c>powershell.exe</c>
    /// launch fails.</summary>
    public void OpenInWindowsTerminal(string directory)
    {
        if (TryStart(new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = true, ArgumentList = { "-d", directory } })) return;
        if (TryStart(new ProcessStartInfo { FileName = "pwsh.exe", WorkingDirectory = directory, UseShellExecute = true })) return;
        Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = directory, UseShellExecute = true });
    }

    // A missing execution alias (wt without the Store app installed, pwsh without PS7) surfaces as Win32Exception
    // from Process.Start — fast, with no window flash — so try/catch is cheap enough to re-run on every click.
    private static bool TryStart(ProcessStartInfo psi)
    {
        try { Process.Start(psi); return true; }
        catch (Win32Exception) { return false; }
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
