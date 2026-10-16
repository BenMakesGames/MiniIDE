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
using Avalonia.Threading;
using AvaloniaEdit;
using MiniIde.Models;
using MiniIde.ViewModels;

namespace MiniIde.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    // One binder per editor kind — never a merged registry (see TabEditorBinder). The code binder's
    // registry therefore holds only code-tab editors, which is what makes FindActiveEditor unambiguous.
    private readonly TabEditorBinder<EditorTabViewModel, RoslynColorizer> _codeEditors;
    private readonly TabEditorBinder<OutputTabViewModel, OutputEditorState> _outputEditors;

    // Both editor DataTemplates share one set of lifecycle handlers; each binder ignores editors showing a
    // tab of the other kind, so offering every event to every binder is correct and keeps the window from
    // growing a parallel handler trio per editor kind.
    private readonly ITabEditorBinder[] _binders;

    public MainWindow()
    {
        InitializeComponent();
        // Deferred: Vm isn't set at ctor time — the classify call resolves it when the colorizer first runs.
        _codeEditors = TabEditorBinder.ForCodeTabs(src => Vm.Highlight.ClassifyAsync(src));
        _outputEditors = TabEditorBinder.ForOutputTabs();
        _binders = [_codeEditors, _outputEditors];

        DataContextChanged += (_, _) =>
        {
            if (Vm is not null) Vm.RequestOpen += Reveal;
        };
        KeyDown += OnGlobalKeyDown;
        // Re-entering MiniIde is exactly when something done elsewhere (an agent's edit, a `git init`) should
        // show. External tools own the writes under the read-only law.
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
    private async void OnWindowActivated(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.RefreshFromDiskAsync();
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.O) { e.Handled = true; await OpenSolutionDialogAsync(); }
        else if (e.Key == Key.F12 && !shift) { e.Handled = true; await GoToDefinitionAsync(); }
    }

    /// <summary>Shows a location: opens its file, then puts the caret exactly on the hit, scrolls it into view,
    /// and focuses the editor. The view owns this because only it has the realized editor; every navigation
    /// source (problems, go-to-definition) reaches it through <see cref="MainWindowViewModel.RequestOpen"/>.</summary>
    private async Task Reveal(SourceLocation location)
    {
        await Vm.OpenFileAsync(location.File);

        // Opening a file (or switching tabs) only queues the work: the TabControl realizes the TextEditor and
        // the binder points it at the new document during the next layout pass. Positioning the caret before
        // that would either find no editor at all or write into the outgoing tab's document, so yield until
        // layout has run.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var editor = FindActiveEditor();
        if (editor?.Document is null) return;

        editor.CaretOffset = OffsetOf(editor.Document, location);
        editor.ScrollTo(location.Line, location.Column);
        editor.Focus();
    }

    /// <summary>The offset of <paramref name="location"/>, clamped into the document. A hit can name a position
    /// that no longer exists — the file may have changed on disk since the analysis ran, or a diagnostic may
    /// point one past the last column — and <c>TextDocument.GetOffset</c> throws on an out-of-range line.</summary>
    private static int OffsetOf(AvaloniaEdit.Document.TextDocument document, SourceLocation location)
    {
        var line = document.GetLineByNumber(Math.Clamp(location.Line, 1, document.LineCount));
        return line.Offset + Math.Clamp(location.Column - 1, 0, line.Length);
    }

    /// <summary>The editor showing the active tab, or null when the active tab isn't a code tab. Only code
    /// editors are ever registered with <see cref="_codeEditors"/>, so a non-null result implies
    /// <c>ActiveTab</c> is an <see cref="EditorTabViewModel"/> (and its <c>FilePath</c> is non-null).</summary>
    private TextEditor? FindActiveEditor() => _codeEditors.EditorFor(Vm.ActiveTab);

    private async void OnOpenSolutionClick(object? sender, RoutedEventArgs e) => await OpenSolutionDialogAsync();

    private async void OnReloadSolutionClick(object? sender, RoutedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) { Vm.Status = "No solution open"; return; }
        await Vm.OpenSolutionCommand.ExecuteAsync(path); // refreshes the project list and restarts the watcher...
        await Vm.ReloadWorkspaceAsync();                 // ...and the semantic snapshot + open tabs
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
        TabViewModelBase tab => tab.FilePath,
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

    // async void like its siblings: the shell call blocks for the entire life of any dialog Windows raises
    // (the "how do you want to open this?" picker), so it must run off the UI thread or the window freezes
    // behind the picker. The window handle owns that picker, so it centres on and draws in front of MiniIDE.
    private async void OnCtxOpenExternallyClick(object? sender, RoutedEventArgs e)
    {
        var target = GetTargetPath((sender as MenuItem)?.DataContext);
        if (target is null) return;
        // Everything the background thread touches is resolved HERE, on the UI thread: `Vm` reads DataContext
        // and TryGetPlatformHandle reads the window, both of which throw "a different thread owns it" off-thread.
        var shell = Vm.Shell;
        var owner = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        try { await Task.Run(() => shell.OpenWithRegisteredApp(target, owner)); }
        catch (Exception ex) { Vm.Status = $"Open in external app failed: {ex.Message}"; }
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

    // ── Double-click in Problems / NuGet tabs ──
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

    private void OnProblemsTreeAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        HookDoubleClick(sender, OnProblemsPointerPressed);

    private void OnNuGetPackagesAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        HookDoubleClick(sender, OnNuGetPackagesPointerPressed);

    private void OnNuGetVersionsAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        HookDoubleClick(sender, OnNuGetVersionsPointerPressed);

    // ClickCount counts presses of any button, so a double right-click would also read as 2 — only a left
    // double-click is an "open".
    private static bool IsLeftDoubleClick(object? sender, PointerPressedEventArgs e) =>
        e.ClickCount == 2 && e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;

    private void OnProblemsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftDoubleClick(sender, e)) return;
        if (sender is TreeView { SelectedItem: ProblemLeaf leaf, DataContext: ProblemsTabViewModel tab })
            tab.Activate(leaf);
    }

    private async void OnProblemsTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control) return;
        if (sender is not TreeView { SelectedItem: ProblemLeaf leaf }) return;
        e.Handled = true;
        await CopyProblemAsync(leaf);
    }

    private async void OnCtxCopyProblemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ProblemLeaf leaf) await CopyProblemAsync(leaf);
    }

    private Task CopyProblemAsync(ProblemLeaf leaf) =>
        CopyToClipboardAsync(leaf.Item.ToCompilerText(Vm.Solution.ToRelativePath));

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

    private async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) { await OpenSolutionDialogAsync(); return; }
        await Vm.OpenFileAsync(path);
    }

    // The TabControl reuses one realized TextEditor across all tabs of the same DataType and swaps its
    // DataContext, so binding must run on every DataContextChanged, not once on attach — and must be undone
    // on detach, since switching to a tab of a different DataType realizes a different control and discards
    // this one (see docs/avalonia.md). TabEditorBinder owns that lifecycle; both editor templates route here,
    // and each binder ignores editors it doesn't own.
    private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor)
            foreach (var binder in _binders) binder.Bind(editor);
    }

    private void OnEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor)
            foreach (var binder in _binders) binder.Bind(editor);
    }

    private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor)
            foreach (var binder in _binders) binder.Unbind(editor);
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

    // ── Code-editor context menu (Go to definition / Rename) ──

    // FindActiveEditor matched a code editor, so ActiveTab is an EditorTabViewModel and its FilePath is non-null.
    private async Task GoToDefinitionAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        await Vm.GoToDefinitionAsync(Vm.ActiveTab!.FilePath!, editor.CaretOffset);
    }

    private void OnCodeCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        var editor = FindActiveEditor();
        // A cheap synchronous gate: a resolvable identifier under the caret in a C# tab. Whether the symbol is
        // actually renameable (defined in this solution, not a framework type) is the authoritative in-source
        // check at invoke time — see RenameSymbolAsync.
        var eligible = CodeSymbolContext.IsSymbolEligible(editor, _codeEditors.StateFor(editor));
        foreach (var item in cm.Items)
            if (item is MenuItem mi) mi.IsEnabled = eligible;
    }

    // The context-menu caret already sits on the clicked token (TabEditorBinder), so this resolves against the
    // same offset as the F12 shortcut.
    private async void OnCtxGoToDefClick(object? sender, RoutedEventArgs e) => await GoToDefinitionAsync();

    private async void OnCtxRenameClick(object? sender, RoutedEventArgs e) => await RenameSymbolAsync();

    // Hands the VM the caret + the editor's current text (for the freshness gate) and a way to prompt for the
    // new name; the VM owns the refactor and all status reporting, the view owns only the modal dialog.
    private async Task RenameSymbolAsync()
    {
        var editor = FindActiveEditor();
        if (editor?.Document is null) return;
        await Vm.RenameSymbolAsync(
            Vm.ActiveTab!.FilePath!, editor.CaretOffset, editor.Document.Text, PromptForNewNameAsync);
    }

    // Shows the modal new-name dialog over this window. Returns the entered valid name, or null when the user
    // cancels (or the name was blank / unchanged / not a valid identifier — the dialog can't return those).
    private Task<string?> PromptForNewNameAsync(string currentName) =>
        new RenameDialog(currentName).ShowDialog<string?>(this);
}
