using System;
using System.Collections.Generic;
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
    static MainWindow()
    {
        Control.RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>(
            (_, e) => e.TargetRect = e.TargetRect.WithWidth(0),
            RoutingStrategies.Bubble);
    }

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
        SolutionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        SolutionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.O) { e.Handled = true; await OpenSolutionDialogAsync(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveActiveAsync(); }
        else if (ctrl && shift && e.Key == Key.F) { e.Handled = true; if (!TrySearchTermInEditor()) FocusFind(); }
        else if (e.Key == Key.F5) { e.Handled = true; if (Vm.PlayCommand.CanExecute(null)) await Vm.PlayCommand.ExecuteAsync(null); }
        else if (e.Key == Key.F12 && !shift) { e.Handled = true; await GoToDefinitionAsync(); }
        else if (e.Key == Key.F12 && shift) { e.Handled = true; await FindRefsAsync(); }
    }

    /// <summary>Shows a location: opens its file, then puts the caret exactly on the hit, scrolls it into view,
    /// and focuses the editor. The view owns this because only it has the realized editor; every navigation
    /// source (find results, problems, go-to-definition) reaches it through
    /// <see cref="MainWindowViewModel.RequestOpen"/>.</summary>
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
    /// that no longer exists — the file may have changed on disk since the search ran, or a diagnostic may point
    /// one past the last column — and <c>TextDocument.GetOffset</c> throws on an out-of-range line.</summary>
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
        await Vm.OpenSolutionCommand.ExecuteAsync(path);
    }

    private async void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is TabViewModelBase tab)
            await tab.CloseCommand.ExecuteAsync(null);
    }

    private static string? GetTargetPath(object? dataContext) => dataContext switch
    {
        TreeNode { Path: not null } tn => tn.Path,
        TabViewModelBase tab => tab.FilePath,
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
        var ctx = (sender as MenuItem)?.DataContext;
        var target = GetTargetPath(ctx);
        if (target is null) return;
        var isFolder = ctx is TreeNode { Kind: NodeKind.Folder };
        try { Vm.Shell.RevealInExplorer(target, isFolder); }
        catch (Exception ex) { Vm.Status = $"Open in Explorer failed: {ex.Message}"; }
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

    // Open/expand on double-click via PointerPressed + ClickCount == 2 rather than DoubleTapped: the latter
    // only fires when both presses resolve to the same source element, so it drops near row edges and right
    // of the text (see docs/avalonia.md). PointerPressed fires wherever the press registers — everywhere
    // selection already works. Tunnel-registered so it runs before TreeViewItem consumes the press; we must
    // NOT set e.Handled, or selection would no longer commit on this press.
    private async void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is TreeView tv && tv.SelectedItem is TreeNode node)
        {
            if (node.Kind == NodeKind.Project) Services.SolutionServiceExtensions.EnsureExpanded(node);
            else if (node.Kind == NodeKind.File && node.Path is not null)
                await Vm.OpenFileAsync(node.Path);
        }
    }

    // Wire double-click navigation once the Problems tree is realized (it lives inside a bottom TabItem, so
    // it isn't in the visual tree at ctor time). Same tunnel PointerPressed + ClickCount==2 approach as the
    // solution tree — DoubleTapped drops presses near row edges (see docs/avalonia.md).
    private bool _problemsTreeHooked;
    private void OnProblemsTreeAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_problemsTreeHooked || sender is not TreeView tv) return;
        _problemsTreeHooked = true;
        tv.AddHandler(PointerPressedEvent, OnProblemsPointerPressed, RoutingStrategies.Tunnel);
    }

    private void OnProblemsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is TreeView tv && tv.SelectedItem is ProblemLeaf leaf)
            Vm.Problems.Activate(leaf);
    }

    private async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) { await OpenSolutionDialogAsync(); return; }
        await Vm.OpenFileAsync(path);
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender is not TreeView tv || tv.SelectedItem is not TreeNode node) return;
        if (node.Kind == NodeKind.File && node.Path is not null)
        {
            e.Handled = true;
            await Vm.OpenFileAsync(node.Path);
        }
    }

    private void OnFindKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm.Find.SearchCommand.Execute(null);
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

    private void FocusFind()
    {
        BottomTabs.SelectedItem = FindTab;
        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    // Output tabs have no backing file, so the path actions (Open in Explorer / Copy path) are inert on them.
    // Disable every item when the tab under the menu has no FilePath; GetTargetPath returns null in that case.
    private void OnTabHeaderCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        var hasPath = GetTargetPath(cm.DataContext) is not null;
        foreach (var item in cm.Items)
            if (item is MenuItem mi) mi.IsEnabled = hasPath;
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

    private async Task SaveActiveAsync()
    {
        if (Vm.ActiveTab is not null) await Vm.ActiveTab.SaveCommand.ExecuteAsync(null);
    }

    // ── Code-editor context menu (Search / Find usages / Go to definition) ──

    // FindActiveEditor matched a code editor, so ActiveTab is an EditorTabViewModel and its FilePath is non-null.
    private async Task GoToDefinitionAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        await Vm.GoToDefinitionAsync(Vm.ActiveTab!.FilePath!, editor.CaretOffset);
    }

    private async Task FindRefsAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        await Vm.FindReferencesAsync(Vm.ActiveTab!.FilePath!, editor.CaretOffset);
    }

    private void OnCodeCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu cm) return;

        var editor = FindActiveEditor();
        var context = CodeSymbolContext.At(editor, _codeEditors.StateFor(editor));
        var canSearch = Vm.Solution.SolutionPath is not null && !string.IsNullOrEmpty(context.Term);

        foreach (var item in cm.Items)
            if (item is MenuItem mi)
                switch (mi.Name)
                {
                    case "CtxSearchItem":
                        mi.IsEnabled = canSearch;
                        mi.Header = context.SearchHeader;
                        break;
                    case "CtxFindUsagesItem":
                    case "CtxGoToDefItem":
                        mi.IsEnabled = context.SymbolEligible;
                        break;
                }
    }

    private void OnCtxSearchClick(object? sender, RoutedEventArgs e) => TrySearchTermInEditor();

    /// <summary>Searches the solution for the selection (or identifier under the caret) in the active editor,
    /// then reveals the Find tab. Shared by the "Search solution" context-menu item and the Ctrl+Shift+F
    /// shortcut. Returns false without side effects when there's no active editor, no term, or no solution —
    /// letting the keyboard path fall back to simply focusing the Find box.</summary>
    private bool TrySearchTermInEditor()
    {
        var editor = FindActiveEditor();
        var term = CodeSymbolContext.At(editor, _codeEditors.StateFor(editor)).Term;
        if (string.IsNullOrEmpty(term) || Vm.Solution.SolutionPath is null) return false;
        Vm.Find.UseRegex = false; // clicked word is a literal query — avoid regex-metacharacter surprises
        Vm.Find.Query = term;
        Vm.Find.SearchCommand.Execute(null);
        FocusFind();
        return true;
    }

    // The context-menu caret already sits on the clicked token (TabEditorBinder), so these resolve against
    // the same offset as the F12 / Shift+F12 shortcuts.
    private async void OnCtxFindUsagesClick(object? sender, RoutedEventArgs e) => await FindRefsAsync();

    private async void OnCtxGoToDefClick(object? sender, RoutedEventArgs e) => await GoToDefinitionAsync();
}
