using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Microsoft.CodeAnalysis.Classification;
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

    public MainWindow()
    {
        InitializeComponent();
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

    // Output tabs now realize a TextEditor too, so match only editor tabs — otherwise F12/Shift+F12 on an
    // active output tab would resolve against a fileless tab. This keeps the invariant that a non-null result
    // implies ActiveTab is an EditorTabViewModel (so its FilePath is non-null).
    private TextEditor? FindActiveEditor()
    {
        var host = this.GetVisualDescendants().OfType<TextEditor>()
            .FirstOrDefault(e => e.DataContext is EditorTabViewModel && e.DataContext == Vm.ActiveTab);
        return host;
    }

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

    // Set once wt.exe fails to launch (absent execution alias); subsequent invocations skip straight to
    // PowerShell for the app's lifetime. Resetting between app runs is fine.
    private bool _wtUnavailable;

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

        if (!_wtUnavailable)
        {
            try
            {
                var wt = new ProcessStartInfo { FileName = "wt.exe", UseShellExecute = true };
                wt.ArgumentList.Add("-d");
                wt.ArgumentList.Add(dir);
                wt.ArgumentList.Add("claude");
                Process.Start(wt);
                return;
            }
            catch (Exception) { _wtUnavailable = true; } // wt not installed — fall through to PowerShell
        }

        try
        {
            var ps = new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = dir, UseShellExecute = true };
            ps.ArgumentList.Add("-NoExit");
            ps.ArgumentList.Add("-Command");
            ps.ArgumentList.Add("claude");
            Process.Start(ps);
        }
        catch (Exception ex) { Vm.Status = $"Open with Claude Code failed: {ex.Message}"; }
    }

    private void OnCtxOpenInExplorerClick(object? sender, RoutedEventArgs e)
    {
        var ctx = (sender as MenuItem)?.DataContext;
        var target = GetTargetPath(ctx);
        if (target is null) return;
        var isFolder = ctx is TreeNode { Kind: NodeKind.Folder };
        try
        {
            var psi = new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true };
            if (isFolder) psi.ArgumentList.Add(target);
            else { psi.ArgumentList.Add("/select,"); psi.ArgumentList.Add(target); }
            Process.Start(psi);
        }
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
        var slnPath = Vm.Solution.SolutionPath;
        var text = slnPath is null
            ? target
            : Path.GetRelativePath(Path.GetDirectoryName(slnPath)!, target);
        await CopyToClipboardAsync(text);
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

    private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) BindEditor(editor);
    }

    private void OnEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) BindEditor(editor);
    }

    private void OnOutputEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextEditor editor) BindOutputEditor(editor);
    }

    private void OnOutputEditorDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) BindOutputEditor(editor);
    }

    private readonly Dictionary<TextEditor, OutputBinding> _outputBindings = new();

    // Avalonia's TabControl swaps the content presenter's DataContext between two OutputTabViewModel tabs
    // rather than realizing a fresh TextEditor, so one editor is shared across all output tabs. Mirror
    // BindEditor: on every attach/DataContextChanged, re-point Document at the active tab's buffer and
    // re-wire tail-follow, detaching the previous document's handlers first.
    private void BindOutputEditor(TextEditor editor)
    {
        if (editor.DataContext is not OutputTabViewModel tab) return;

        if (!_outputBindings.TryGetValue(editor, out var b))
        {
            b = new OutputBinding();
            _outputBindings[editor] = b;
        }

        if (ReferenceEquals(b.CurrentTab, tab)) return;

        if (b.CurrentDoc is not null)
        {
            if (b.ChangingHandler is not null) b.CurrentDoc.Changing -= b.ChangingHandler;
            if (b.ChangedHandler is not null) b.CurrentDoc.Changed -= b.ChangedHandler;
        }

        var doc = tab.Output.Document;
        b.CurrentTab = tab;
        b.CurrentDoc = doc;
        editor.Document = doc;

        const double epsilon = 1.0;
        b.ChangingHandler = (_, _) =>
        {
            var scroll = (Avalonia.Controls.Primitives.ILogicalScrollable)editor.TextArea.TextView;
            b.WasAtBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - epsilon;
        };
        b.ChangedHandler = (_, _) =>
        {
            if (b.WasAtBottom) editor.ScrollToLine(editor.Document.LineCount);
        };
        doc.Changing += b.ChangingHandler;
        doc.Changed += b.ChangedHandler;
    }

    private class OutputBinding
    {
        public OutputTabViewModel? CurrentTab;
        public TextDocument? CurrentDoc;
        public EventHandler<DocumentChangeEventArgs>? ChangingHandler;
        public EventHandler<DocumentChangeEventArgs>? ChangedHandler;
        public bool WasAtBottom = true;
    }

    private readonly System.Collections.Generic.Dictionary<TextEditor, EditorBinding> _bindings = new();

    private void BindEditor(TextEditor editor)
    {
        if (editor.DataContext is not EditorTabViewModel tab) return;

        if (!_bindings.TryGetValue(editor, out var b))
        {
            b = new EditorBinding();
            _bindings[editor] = b;
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 4;
            b.Colorizer = new RoslynColorizer(async src => await Vm.Highlight.ClassifyAsync(src));
            editor.TextArea.TextView.LineTransformers.Add(b.Colorizer);
            editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                if (editor.DataContext is EditorTabViewModel t) t.CaretOffset = editor.CaretOffset;
            };
            // Tunnel so we place the caret before AvaloniaEdit's own pointer logic and before the menu opens.
            editor.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
        }

        if (ReferenceEquals(b.CurrentTab, tab)) return;

        if (b.CurrentDoc is not null && b.TextChangedHandler is not null)
            b.CurrentDoc.TextChanged -= b.TextChangedHandler;

        // Drop any reclassify still pending from the tab we're leaving.
        b.DebounceCts?.Cancel();

        b.CurrentTab = tab;
        b.CurrentDoc = tab.Document;
        editor.Document = tab.Document;

        // Debounce: a burst of keystrokes reclassifies the whole document (a full Roslyn snapshot)
        // only once typing pauses, instead of on every character.
        b.TextChangedHandler = (_, _) =>
        {
            b.DebounceCts?.Cancel();
            b.DebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            b.DebounceCts = cts;
            _ = DebouncedRefreshAsync(b.Colorizer!, editor, tab.Mode, cts.Token);
        };
        tab.Document.TextChanged += b.TextChangedHandler;

        // Initial highlight of the newly-shown document is immediate, not debounced.
        _ = RefreshAndRedraw(b.Colorizer!, editor, tab.Mode);
    }

    private const int HighlightDebounceMs = 200;

    private static async Task DebouncedRefreshAsync(RoslynColorizer colorizer, TextEditor editor, HighlightMode mode, CancellationToken ct)
    {
        try { await Task.Delay(HighlightDebounceMs, ct); }
        catch (OperationCanceledException) { return; }
        await RefreshAndRedraw(colorizer, editor, mode);
    }

    private static async Task RefreshAndRedraw(RoslynColorizer colorizer, TextEditor editor, HighlightMode mode)
    {
        switch (mode)
        {
            case HighlightMode.CSharp:
                editor.SyntaxHighlighting = null;
                await colorizer.RefreshAsync(editor.Document.Text);
                break;
            case HighlightMode.Xml:
                colorizer.Clear();
                editor.SyntaxHighlighting = XshdDarkPalette.Tune("XML");
                break;
            case HighlightMode.Json:
                colorizer.Clear();
                editor.SyntaxHighlighting = XshdDarkPalette.Tune("Json");
                break;
            default:
                colorizer.Clear();
                editor.SyntaxHighlighting = null;
                break;
        }
        editor.TextArea.TextView.Redraw();
    }

    private class EditorBinding
    {
        public EditorTabViewModel? CurrentTab;
        public AvaloniaEdit.Document.TextDocument? CurrentDoc;
        public RoslynColorizer? Colorizer;
        public EventHandler? TextChangedHandler;
        public CancellationTokenSource? DebounceCts;
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

    private RoslynColorizer? ColorizerFor(TextEditor editor)
        => _bindings.TryGetValue(editor, out var b) ? b.Colorizer : null;

    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextEditor editor || editor.Document is null) return;
        var point = e.GetCurrentPoint(editor);
        if (!point.Properties.IsRightButtonPressed) return;
        var pos = editor.GetPositionFromPoint(point.Position);
        if (pos is null) return;
        var offset = editor.Document.GetOffset(pos.Value.Line, pos.Value.Column);
        // Preserve a selection the user right-clicked inside (so "Search selection" keeps the full phrase);
        // otherwise move the caret to the clicked token and collapse any stale selection.
        if (editor.SelectionLength > 0 &&
            offset >= editor.SelectionStart && offset <= editor.SelectionStart + editor.SelectionLength)
            return;
        editor.TextArea.ClearSelection();
        editor.CaretOffset = offset;
    }

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

internal class RoslynColorizer : DocumentColorizingTransformer
{
    private readonly Func<string, Task<IReadOnlyList<ClassifiedSpan>>> _classify;
    private IReadOnlyList<ClassifiedSpan> _spans = System.Array.Empty<ClassifiedSpan>();

    public RoslynColorizer(Func<string, Task<IReadOnlyList<ClassifiedSpan>>> classify) { _classify = classify; }

    public async Task RefreshAsync(string source)
    {
        try { _spans = await _classify(source); }
        catch { _spans = System.Array.Empty<ClassifiedSpan>(); }
    }

    public void Clear() => _spans = System.Array.Empty<ClassifiedSpan>();

    /// <summary>The classification of the cached span covering <paramref name="offset"/>, or null if none.
    /// Reads the existing span cache synchronously — no reclassification.</summary>
    public string? ClassificationAt(int offset)
    {
        foreach (var span in _spans)
            if (offset >= span.TextSpan.Start && offset < span.TextSpan.End)
                return span.ClassificationType;
        return null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        int lineEnd = line.EndOffset;
        foreach (var span in _spans)
        {
            if (span.TextSpan.End <= lineStart) continue;
            if (span.TextSpan.Start >= lineEnd) break;
            var start = Math.Max(span.TextSpan.Start, lineStart);
            var end = Math.Min(span.TextSpan.End, lineEnd);
            var brush = BrushFor(span.ClassificationType);
            if (brush is null) continue;
            ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    // Shared, immutable brushes: ColorizeLine runs per visible span per redraw, so a fresh
    // SolidColorBrush per call would allocate dozens of throwaway brushes every frame.
    private static readonly IBrush KeywordBrush = new ImmutableSolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly IBrush StringBrush = new ImmutableSolidColorBrush(Color.FromRgb(206, 145, 120));
    private static readonly IBrush NumberBrush = new ImmutableSolidColorBrush(Color.FromRgb(181, 206, 168));
    private static readonly IBrush CommentBrush = new ImmutableSolidColorBrush(Color.FromRgb(106, 153, 85));
    private static readonly IBrush TypeBrush = new ImmutableSolidColorBrush(Color.FromRgb(78, 201, 176));
    private static readonly IBrush MethodBrush = new ImmutableSolidColorBrush(Color.FromRgb(220, 220, 170));
    private static readonly IBrush MemberBrush = new ImmutableSolidColorBrush(Color.FromRgb(156, 220, 254));
    private static readonly IBrush NamespaceBrush = new ImmutableSolidColorBrush(Color.FromRgb(200, 200, 200));

    private static IBrush? BrushFor(string kind) => kind switch
    {
        ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword or ClassificationTypeNames.PreprocessorKeyword
            => KeywordBrush,
        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral or ClassificationTypeNames.StringEscapeCharacter
            => StringBrush,
        ClassificationTypeNames.NumericLiteral
            => NumberBrush,
        ClassificationTypeNames.Comment or ClassificationTypeNames.XmlDocCommentText
            => CommentBrush,
        ClassificationTypeNames.ClassName or ClassificationTypeNames.StructName or ClassificationTypeNames.InterfaceName
            or ClassificationTypeNames.EnumName or ClassificationTypeNames.DelegateName or ClassificationTypeNames.RecordClassName
            => TypeBrush,
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName
            => MethodBrush,
        ClassificationTypeNames.PropertyName or ClassificationTypeNames.FieldName or ClassificationTypeNames.ConstantName
        or ClassificationTypeNames.LocalName or ClassificationTypeNames.ParameterName
            => MemberBrush,
        ClassificationTypeNames.NamespaceName => NamespaceBrush,
        _ => null
    };
}
