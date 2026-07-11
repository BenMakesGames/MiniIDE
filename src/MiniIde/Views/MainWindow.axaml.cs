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
    static MainWindow()
    {
        Control.RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>(
            (_, e) => e.TargetRect = e.TargetRect.WithWidth(0),
            RoutingStrategies.Bubble);
    }

    private MainWindowViewModel Vm => (MainWindowViewModel)DataContext!;

    // One binder per editor kind — never a merged registry (see TabEditorBinder). The code binder's
    // registry therefore holds only code-tab editors, which is what makes FindActiveEditor unambiguous.
    private readonly TabEditorBinder<EditorTabViewModel, CodeEditorState> _codeEditors;
    private readonly TabEditorBinder<OutputTabViewModel, OutputEditorState> _outputEditors;

    public MainWindow()
    {
        InitializeComponent();
        // Deferred: Vm isn't set at ctor time — the classify call resolves it when the colorizer first runs.
        _codeEditors = TabEditorBinder.ForCodeTabs(src => Vm.Highlight.ClassifyAsync(src));
        _outputEditors = TabEditorBinder.ForOutputTabs();
        DataContextChanged += (_, _) =>
        {
            if (Vm is not null) Vm.RequestOpen += OpenHit;
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

    private async Task OpenHit(string file, int line, int col)
    {
        await Vm.OpenFileAsync(file, line, col);
        var editor = FindActiveEditor();
        if (editor is null) return;
        var offset = editor.Document.GetOffset(line, col);
        editor.CaretOffset = offset;
        editor.ScrollTo(line, col);
        editor.Focus();
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
    // this one (see docs/avalonia.md). TabEditorBinder owns that lifecycle for both kinds.
    private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _codeEditors.Bind(editor);
    }

    private void OnEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) _codeEditors.Bind(editor);
    }

    private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _codeEditors.Unbind(editor);
    }

    private void OnOutputEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Bind(editor);
    }

    private void OnOutputEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Bind(editor);
    }

    private void OnOutputEditorDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) _outputEditors.Unbind(editor);
    }

    private void FocusFind()
    {
        var tabs = this.FindControl<TabControl>("BottomTabs");
        var findTab = this.FindControl<TabItem>("FindTab");
        if (tabs is not null && findTab is not null)
            tabs.SelectedItem = findTab;

        Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("FindBox");
            if (box is null) return;
            box.Focus();
            box.SelectAll();
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

    private async Task GoToDefinitionAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        // FindActiveEditor matched an editor tab, so FilePath is non-null.
        var result = await Vm.GoToDefinitionAsync(Vm.ActiveTab.FilePath!, editor.CaretOffset);
        if (result is null) return;
        await OpenHit(result.Value.Item1, result.Value.Item2, result.Value.Item3);
    }

    private async Task FindRefsAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        // FindActiveEditor matched an editor tab, so FilePath is non-null.
        var refs = await Vm.FindReferencesAsync(Vm.ActiveTab.FilePath!, editor.CaretOffset);
        if (refs is null) { Vm.Find.Results.Clear(); Vm.Find.Status = "No symbol found"; return; }
        PopulateRefs(refs);
    }

    private void PopulateRefs(System.Collections.Generic.IReadOnlyList<(string, int, int, string)> refs)
    {
        Vm.Find.Results.Clear();
        foreach (var r in refs) Vm.Find.Results.Add(new FindHit(r.Item1, r.Item2, r.Item3, r.Item4));
        Vm.Find.Status = $"{refs.Count} reference(s)";
    }

    // ── Code-editor context menu (Search / Find usages / Go to definition) ──

    private static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    /// <summary>The [start, end) of the identifier run covering <paramref name="offset"/>, or an empty
    /// range (start == end) when the offset is not on/adjacent to an identifier.</summary>
    private static (int Start, int End) IdentifierRunAt(string text, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;
        int start = offset, end = offset;
        while (start > 0 && IsIdentifierChar(text[start - 1])) start--;
        while (end < text.Length && IsIdentifierChar(text[end])) end++;
        return (start, end);
    }

    /// <summary>The query term for the Search action: the selection if non-empty, else the identifier run
    /// under the caret, else null.</summary>
    private static string? TermAt(TextEditor editor)
    {
        var sel = editor.SelectedText;
        if (!string.IsNullOrEmpty(sel)) return sel;
        var text = editor.Document.Text;
        var (start, end) = IdentifierRunAt(text, editor.CaretOffset);
        return end > start ? text.Substring(start, end - start) : null;
    }

    /// <summary>A classification is ineligible for symbol actions when it names a keyword, string, comment,
    /// number, operator, punctuation, excluded, or whitespace kind. Substring matching covers Roslyn's
    /// dotted variants ("keyword - control", "string - verbatim", "xml doc comment - text", …). A null
    /// classification (no covering span) is treated as eligible — the identifier-char gate still applies.</summary>
    private static bool IsDeniedClassification(string? cls)
    {
        if (cls is null) return false;
        return cls.Contains("keyword") || cls.Contains("string") || cls.Contains("comment")
            || cls.Contains("number") || cls.Contains("operator") || cls.Contains("punctuation")
            || cls.Contains("excluded") || cls.Contains("whitespace");
    }

    private static string Ellipsize(string s) => s.Length <= 30 ? s : s.Substring(0, 30) + "…";

    private RoslynColorizer? ColorizerFor(TextEditor editor) => _codeEditors.StateFor(editor)?.Colorizer;

    private void OnCodeCtxOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu cm) return;
        MenuItem? search = null, findUsages = null, goToDef = null;
        foreach (var item in cm.Items)
            if (item is MenuItem mi)
                switch (mi.Name)
                {
                    case "CtxSearchItem": search = mi; break;
                    case "CtxFindUsagesItem": findUsages = mi; break;
                    case "CtxGoToDefItem": goToDef = mi; break;
                }

        var editor = FindActiveEditor();
        var tab = editor?.DataContext as EditorTabViewModel;

        // Search: enabled when a solution is loaded and a term (selection or word) exists.
        var term = editor is null ? null : TermAt(editor);
        if (search is not null)
        {
            search.IsEnabled = Vm.Solution.SolutionPath is not null && !string.IsNullOrEmpty(term);
            search.Header = string.IsNullOrEmpty(term) ? "Search solution" : $"Search solution for \"{Ellipsize(term)}\"";
        }

        // Symbol actions: C# tab, identifier char under the caret, non-denied classification.
        var symbolEligible = false;
        if (editor is not null && tab is not null && tab.Mode == HighlightMode.CSharp)
        {
            var offset = editor.CaretOffset;
            var text = editor.Document.Text;
            if (offset >= 0 && offset < text.Length && IsIdentifierChar(text[offset]))
                symbolEligible = !IsDeniedClassification(ColorizerFor(editor)?.ClassificationAt(offset));
        }
        if (findUsages is not null) findUsages.IsEnabled = symbolEligible;
        if (goToDef is not null) goToDef.IsEnabled = symbolEligible;
    }

    private void OnCtxSearchClick(object? sender, RoutedEventArgs e) => TrySearchTermInEditor();

    /// <summary>Searches the solution for the selection (or identifier under the caret) in the active editor,
    /// then reveals the Find tab. Shared by the "Search solution" context-menu item and the Ctrl+Shift+F
    /// shortcut. Returns false without side effects when there's no active editor, no term, or no solution —
    /// letting the keyboard path fall back to simply focusing the Find box.</summary>
    private bool TrySearchTermInEditor()
    {
        var editor = FindActiveEditor();
        if (editor is null) return false;
        var term = TermAt(editor);
        if (string.IsNullOrEmpty(term) || Vm.Solution.SolutionPath is null) return false;
        Vm.Find.UseRegex = false; // clicked word is a literal query — avoid regex-metacharacter surprises
        Vm.Find.Query = term;
        Vm.Find.SearchCommand.Execute(null);
        FocusFind();
        return true;
    }

    // The context-menu caret already sits on the clicked token (OnEditorPointerPressed), so these
    // resolve against the same offset as the F12 / Shift+F12 shortcuts.
    private async void OnCtxFindUsagesClick(object? sender, RoutedEventArgs e) => await FindRefsAsync();

    private async void OnCtxGoToDefClick(object? sender, RoutedEventArgs e) => await GoToDefinitionAsync();
}
