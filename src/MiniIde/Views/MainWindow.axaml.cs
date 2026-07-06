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
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CommunityToolkit.Mvvm.Input;
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

    public IRelayCommand OpenSolutionDialogCommand { get; }
    public IRelayCommand SaveActiveCommand { get; }
    public IRelayCommand GoToDefinitionCommand { get; }
    public IRelayCommand FindRefsCommand { get; }

    public MainWindow()
    {
        InitializeComponent();
        OpenSolutionDialogCommand = new RelayCommand(async () => await OpenSolutionDialogAsync());
        SaveActiveCommand = new RelayCommand(async () => await SaveActiveAsync());
        GoToDefinitionCommand = new RelayCommand(async () => await GoToDefinitionAsync());
        FindRefsCommand = new RelayCommand(async () => await FindRefsAsync());
        DataContextChanged += (_, _) =>
        {
            if (Vm is not null) Vm.RequestOpen += OpenHit;
        };
        KeyDown += OnGlobalKeyDown;
        SolutionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (ctrl && e.Key == Key.O) { e.Handled = true; await OpenSolutionDialogAsync(); }
        else if (ctrl && e.Key == Key.S) { e.Handled = true; await SaveActiveAsync(); }
        else if (ctrl && shift && e.Key == Key.F) { e.Handled = true; FocusFind(); }
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

    private TextEditor? FindActiveEditor()
    {
        var host = this.GetVisualDescendants().OfType<TextEditor>()
            .FirstOrDefault(e => e.DataContext == Vm.ActiveTab);
        return host;
    }

    private async void OnOpenSolutionClick(object? sender, RoutedEventArgs e) => await OpenSolutionDialogAsync();
    private async void OnSaveClick(object? sender, RoutedEventArgs e) => await SaveActiveAsync();
    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();
    private async void OnGoToDefClick(object? sender, RoutedEventArgs e) => await GoToDefinitionAsync();
    private async void OnFindRefsClick(object? sender, RoutedEventArgs e) => await FindRefsAsync();

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

    private async void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView tv && tv.SelectedItem is TreeNode node)
        {
            if (node.Kind == NodeKind.Project) Services.SolutionServiceExtensions.EnsureExpanded(node);
            else if (node.Kind == NodeKind.File && node.Path is not null)
                await Vm.OpenFileAsync(node.Path);
        }
    }

    private async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) return;
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

    private readonly HashSet<TextEditor> _outputBound = new();

    private void BindOutputEditor(TextEditor editor)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!_outputBound.Add(editor)) return;

        var doc = vm.Output.Document;
        editor.Document = doc;

        const double epsilon = 1.0;
        bool wasAtBottom = true;

        doc.Changing += (_, _) =>
        {
            var scroll = (Avalonia.Controls.Primitives.ILogicalScrollable)editor.TextArea.TextView;
            wasAtBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - epsilon;
        };
        doc.Changed += (_, _) =>
        {
            if (wasAtBottom) editor.ScrollToLine(editor.Document.LineCount);
        };
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

        b.CurrentTab = tab;
        b.CurrentDoc = tab.Document;
        editor.Document = tab.Document;
        b.Failed.Clear(); // this editor is recycled across tabs; failed-token cache is per-document

        b.TextChangedHandler = async (_, _) =>
        {
            b.Failed.Clear(); // an edit may fix a typo / add a using, re-enabling retroactively-disabled items
            await RefreshAndRedraw(b.Colorizer!, editor, tab.Mode);
        };
        tab.Document.TextChanged += b.TextChangedHandler;

        _ = RefreshAndRedraw(b.Colorizer!, editor, tab.Mode);
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
        // Tokens whose symbol action was attempted and proved impossible (keyed by identifier run + action).
        // Consulted during menu enablement; cleared on document edit / tab switch.
        public readonly HashSet<(int Start, int End, string Text, CtxAction Action)> Failed = new();
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
        await Vm.OpenSolutionCommand.ExecuteAsync(path);
    }

    private async Task SaveActiveAsync()
    {
        if (Vm.ActiveTab is not null) await Vm.ActiveTab.SaveCommand.ExecuteAsync(null);
    }

    private async Task GoToDefinitionAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        var result = await Vm.GoToDefinitionAsync(Vm.ActiveTab.FilePath, editor.CaretOffset);
        if (result is null) return;
        await OpenHit(result.Value.Item1, result.Value.Item2, result.Value.Item3);
    }

    private async Task FindRefsAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        var refs = await Vm.FindReferencesAsync(Vm.ActiveTab.FilePath, editor.CaretOffset);
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

    private enum CtxAction { FindUsages, GoToDef }

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

    private void RecordFailed(TextEditor editor, int offset, CtxAction action)
    {
        if (!_bindings.TryGetValue(editor, out var b)) return;
        var text = editor.Document.Text;
        var (start, end) = IdentifierRunAt(text, offset);
        if (end <= start) return;
        b.Failed.Add((start, end, text.Substring(start, end - start), action));
    }

    private bool IsFailed(TextEditor editor, int offset, CtxAction action)
    {
        if (!_bindings.TryGetValue(editor, out var b)) return false;
        var text = editor.Document.Text;
        var (start, end) = IdentifierRunAt(text, offset);
        if (end <= start) return false;
        return b.Failed.Contains((start, end, text.Substring(start, end - start), action));
    }

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

        // Symbol actions: C# tab, identifier char under the caret, non-denied classification, not failed.
        var baseEligible = false;
        var offset = 0;
        if (editor is not null && tab is not null && tab.Mode == HighlightMode.CSharp)
        {
            offset = editor.CaretOffset;
            var text = editor.Document.Text;
            if (offset >= 0 && offset < text.Length && IsIdentifierChar(text[offset]))
                baseEligible = !IsDeniedClassification(ColorizerFor(editor)?.ClassificationAt(offset));
        }
        if (findUsages is not null)
            findUsages.IsEnabled = baseEligible && editor is not null && !IsFailed(editor, offset, CtxAction.FindUsages);
        if (goToDef is not null)
            goToDef.IsEnabled = baseEligible && editor is not null && !IsFailed(editor, offset, CtxAction.GoToDef);
    }

    private void OnCtxSearchClick(object? sender, RoutedEventArgs e)
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        var term = TermAt(editor);
        if (string.IsNullOrEmpty(term) || Vm.Solution.SolutionPath is null) return;
        Vm.Find.UseRegex = false; // clicked word is a literal query — avoid regex-metacharacter surprises
        Vm.Find.Query = term;
        Vm.Find.SearchCommand.Execute(null);
        FocusFind();
    }

    private async void OnCtxFindUsagesClick(object? sender, RoutedEventArgs e)
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        var offset = editor.CaretOffset;
        var refs = await Vm.FindReferencesAsync(Vm.ActiveTab.FilePath, offset);
        if (refs is null) // no symbol resolved — record so the item disables on reopen at this token
        {
            RecordFailed(editor, offset, CtxAction.FindUsages);
            Vm.Find.Results.Clear();
            Vm.Find.Status = "No symbol found";
            return;
        }
        PopulateRefs(refs); // a resolved symbol with zero references is a legitimate result, not a failure
    }

    private async void OnCtxGoToDefClick(object? sender, RoutedEventArgs e)
    {
        var editor = FindActiveEditor();
        if (editor is null || Vm.ActiveTab is null) return;
        var offset = editor.CaretOffset;
        var result = await Vm.GoToDefinitionAsync(Vm.ActiveTab.FilePath, offset);
        if (result is null) // no symbol, or no in-source definition (framework symbol) — the VM set the status
        {
            RecordFailed(editor, offset, CtxAction.GoToDef);
            return;
        }
        await OpenHit(result.Value.Item1, result.Value.Item2, result.Value.Item3);
    }
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

    private static IBrush? BrushFor(string kind) => kind switch
    {
        ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword or ClassificationTypeNames.PreprocessorKeyword
            => new SolidColorBrush(Color.FromRgb(86, 156, 214)),
        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral or ClassificationTypeNames.StringEscapeCharacter
            => new SolidColorBrush(Color.FromRgb(206, 145, 120)),
        ClassificationTypeNames.NumericLiteral
            => new SolidColorBrush(Color.FromRgb(181, 206, 168)),
        ClassificationTypeNames.Comment or ClassificationTypeNames.XmlDocCommentText
            => new SolidColorBrush(Color.FromRgb(106, 153, 85)),
        ClassificationTypeNames.ClassName or ClassificationTypeNames.StructName or ClassificationTypeNames.InterfaceName
            or ClassificationTypeNames.EnumName or ClassificationTypeNames.DelegateName or ClassificationTypeNames.RecordClassName
            => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName
            => new SolidColorBrush(Color.FromRgb(220, 220, 170)),
        ClassificationTypeNames.PropertyName or ClassificationTypeNames.FieldName or ClassificationTypeNames.ConstantName
        or ClassificationTypeNames.LocalName or ClassificationTypeNames.ParameterName
            => new SolidColorBrush(Color.FromRgb(156, 220, 254)),
        ClassificationTypeNames.NamespaceName => new SolidColorBrush(Color.FromRgb(200, 200, 200)),
        _ => null
    };
}
