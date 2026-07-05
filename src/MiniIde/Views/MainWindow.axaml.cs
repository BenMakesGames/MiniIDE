using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        if (sender is Button b && b.Tag is EditorTabViewModel tab)
            await tab.CloseCommand.ExecuteAsync(null);
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
        }

        if (ReferenceEquals(b.CurrentTab, tab)) return;

        if (b.CurrentDoc is not null && b.TextChangedHandler is not null)
            b.CurrentDoc.TextChanged -= b.TextChangedHandler;

        b.CurrentTab = tab;
        b.CurrentDoc = tab.Document;
        editor.Document = tab.Document;

        b.TextChangedHandler = async (_, _) => await RefreshAndRedraw(b.Colorizer!, editor, tab.IsCSharp);
        tab.Document.TextChanged += b.TextChangedHandler;

        _ = RefreshAndRedraw(b.Colorizer!, editor, tab.IsCSharp);
    }

    private static async Task RefreshAndRedraw(RoslynColorizer colorizer, TextEditor editor, bool isCSharp)
    {
        if (!isCSharp) { colorizer.Clear(); editor.TextArea.TextView.Redraw(); return; }
        await colorizer.RefreshAsync(editor.Document.Text);
        editor.TextArea.TextView.Redraw();
    }

    private class EditorBinding
    {
        public EditorTabViewModel? CurrentTab;
        public AvaloniaEdit.Document.TextDocument? CurrentDoc;
        public RoslynColorizer? Colorizer;
        public EventHandler? TextChangedHandler;
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
        Vm.Find.Results.Clear();
        foreach (var r in refs) Vm.Find.Results.Add(new FindHit(r.Item1, r.Item2, r.Item3, r.Item4));
        Vm.Find.Status = $"{refs.Count} reference(s)";
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
