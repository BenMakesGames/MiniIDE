using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using MiniIde.Models;
using MiniIde.ViewModels;

namespace MiniIde.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    private readonly OutputEditorBinder _outputEditors = new();

    public MainWindow()
    {
        InitializeComponent();
        KeyDown += OnGlobalKeyDown;
        // Re-entering MiniIde is exactly when something done elsewhere (a `git init`) should show.
        Activated += OnWindowActivated;
        // The watcher holds an OS handle and a debounce timer; a closing window wants neither. Live runs go too —
        // including those whose tabs were closed: nothing else reaps them, so each one would outlive us as an
        // orphan process (see StopAllRuns).
        Closed += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.Watcher.Dispose();
            vm.StopAllRuns();
        };
    }

    // Activated can fire before the DataContext is wired at startup; guard rather than assume Vm is set.
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.RefreshGit();
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.O) { e.Handled = true; await OpenSolutionDialogAsync(); }
    }

    private async void OnOpenSolutionClick(object? sender, RoutedEventArgs e) => await OpenSolutionDialogAsync();

    // Refreshes the project list and restarts the watcher.
    private async void OnReloadSolutionClick(object? sender, RoutedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) { Vm.Status = "No solution open"; return; }
        await Vm.OpenSolutionCommand.ExecuteAsync(path);
    }

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is TabViewModelBase tab)
            await tab.CloseCommand.ExecuteAsync(null);
    }

    // Middle-click anywhere on a tab header closes that tab, via the same CloseCommand the ✕ button runs. The
    // middle button has no Click/Tapped event in Avalonia — it only surfaces as PointerUpdateKind on a press — so
    // this is a PointerPressed handler. Bubbling: we *want* to mark the press handled, but only in the
    // middle-button branch, so left-click selection and the right-click ContextMenu are untouched.
    private async void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c) return;
        if (e.GetCurrentPoint(c).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        if (c.DataContext is not TabViewModelBase tab) return;

        e.Handled = true;
        await tab.CloseCommand.ExecuteAsync(null);
    }

    private static string? GetTargetPath(object? dataContext) => dataContext switch
    {
        ProjectItemViewModel project => project.Path,
        MainWindowViewModel vm => vm.Solution.SolutionPath,
        _ => null
    };

    // With no solution loaded, every solution-scoped item is disabled; only "Open new solution..."
    // stays live so a solution can be opened from the menu at startup.
    private void OnSolutionCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var hasSolution = Vm.Solution.SolutionPath is not null;
        foreach (var item in cm.Items)
            if (item is MenuItem mi && mi.Name != "SolutionCtxOpenNew")
                mi.IsEnabled = hasSolution;
    }

    private void OnCtxOpenWithClaudeClick(object? sender, RoutedEventArgs e)
    {
        var slnPath = Vm.Solution.SolutionPath;
        var dir = slnPath is null ? null : Path.GetDirectoryName(slnPath);
        if (dir is null) { Vm.Status = "No solution open"; return; }
        try { Vm.Shell.OpenTerminalWithClaude(dir); }
        catch (Exception ex) { Vm.Status = $"Open with Claude Code failed: {ex.Message}"; }
    }

    private void OnCtxOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        var target = GetTargetPath((sender as MenuItem)?.DataContext);
        if (target is null) return;
        try { Vm.Shell.RevealInExplorer(target, isFolder: false); }
        catch (Exception ex) { Vm.Status = $"Open in Explorer failed: {ex.Message}"; }
    }

    // GetTargetPath hands back a .sln(x) or .csproj file; the terminal opens in its containing folder.
    private void OnCtxOpenInWindowsTerminalClick(object? sender, RoutedEventArgs e)
    {
        var target = GetTargetPath((sender as MenuItem)?.DataContext);
        var dir = target is null ? null : Path.GetDirectoryName(target);
        if (dir is null) return;
        try { Vm.Shell.OpenInWindowsTerminal(dir); }
        catch (Exception ex) { Vm.Status = $"Open in Windows Terminal failed: {ex.Message}"; }
    }

    private async void OnCtxCopyAbsolutePathClick(object? sender, RoutedEventArgs e)
    {
        var target = GetTargetPath((sender as MenuItem)?.DataContext);
        if (target is null) return;
        await CopyToClipboardAsync(target);
    }

    private async void OnCtxCopyRelativePathClick(object? sender, RoutedEventArgs e)
    {
        var target = GetTargetPath((sender as MenuItem)?.DataContext);
        if (target is null) return;
        await CopyToClipboardAsync(Vm.Solution.ToRelativePath(target));
    }

    private async Task CopyToClipboardAsync(string text)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { Vm.Status = "Copy failed: clipboard unavailable"; return; }
            await clipboard.SetTextAsync(text);
            Vm.Status = $"Copied {text}";
        }
        catch (Exception ex) { Vm.Status = $"Copy failed: {ex.Message}"; }
    }

    // ── Double-click in NuGet tabs ──
    //
    // PointerPressed + ClickCount == 2 rather than DoubleTapped: the latter only fires when both presses resolve
    // to the same source element, so it drops near row edges and right of the text (see docs/avalonia.md).
    // Tunnel-registered so it runs before the item consumes the press; we must NOT set e.Handled, or selection
    // would no longer commit on this press.
    //
    // Hooked on attach, not once: the TabControl discards a tab's content when a tab of another type is shown
    // and realizes a fresh control on the way back. Remove-then-add keeps a re-attach of the same control from
    // stacking a second handler.

    private void HookDoubleClick(object? sender, EventHandler<PointerPressedEventArgs> handler)
    {
        if (sender is not Control control) return;
        control.RemoveHandler(PointerPressedEvent, handler);
        control.AddHandler(PointerPressedEvent, handler, RoutingStrategies.Tunnel);
    }

    private void OnNuGetPackagesAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        HookDoubleClick(sender, OnNuGetPackagesPointerPressed);

    private void OnNuGetVersionsAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        HookDoubleClick(sender, OnNuGetVersionsPointerPressed);

    // ClickCount counts presses of any button, so a double right-click would also read as 2 — only a left
    // double-click is an "open".
    private static bool IsLeftDoubleClick(object? sender, PointerPressedEventArgs e) =>
        e.ClickCount == 2 && e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;

    private async void OnNuGetPackagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftDoubleClick(sender, e)) return;
        if (sender is ListBox { SelectedItem: PackageEntry entry, DataContext: NuGetTabViewModel tab })
            await tab.OpenMetadataAsync(entry);
    }

    // The first press already committed SelectedVersion via the two-way binding — do not re-assign here. ApplyAsync
    // no-ops if SelectedPackage or SelectedVersion is null, so guarding those is not this handler's job.
    private async void OnNuGetVersionsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftDoubleClick(sender, e)) return;
        if (sender is ListBox { DataContext: NuGetTabViewModel tab })
            await tab.ApplyCommand.ExecuteAsync(null);
    }

    // With no solution open, the name reads "<no solution>"; double-clicking it is a shortcut to open one.
    private async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm.Solution.SolutionPath is null) await OpenSolutionDialogAsync();
    }

    // The TabControl reuses one realized TextEditor across all output tabs and swaps its DataContext, so binding
    // must run on every DataContextChanged, not once on attach — and must be undone on detach, since switching to
    // a tab of a different DataType realizes a different control and discards this one (see docs/avalonia.md).
    // OutputEditorBinder owns that lifecycle.
    private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Bind(editor);
    }

    private void OnEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Bind(editor);
    }

    private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Unbind(editor);
    }

    private async Task OpenSolutionDialogAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Solution",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Solution") { Patterns = new[] { "*.slnx", "*.sln" } }
            }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        // A solution is already open — launch a new MiniIDE instance for the selection
        // rather than replacing the current one, so both stay open side by side.
        if (Vm.Solution.SolutionPath is not null) { LaunchNewInstance(path); return; }

        await Vm.OpenSolutionCommand.ExecuteAsync(path);
    }

    private void LaunchNewInstance(string solutionPath)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) { Vm.Status = "Could not locate the MiniIDE executable"; return; }
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            psi.ArgumentList.Add(solutionPath);
            Process.Start(psi);
        }
        catch (Exception ex) { Vm.Status = $"Open new solution failed: {ex.Message}"; }
    }
}
