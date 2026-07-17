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
using Avalonia.VisualTree;
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
        // The safety-net refresh whenever the window regains focus: the OS change feed (SolutionWatcher) drives
        // the steady state, but it is best-effort, and re-entering MiniIde is exactly when a dropped event
        // would show. External tools (the agent, CLI git) own the writes under the read-only law.
        Activated += OnWindowActivated;
        // The watcher holds an OS handle and a debounce timer; a closing window wants neither, and a signal
        // arriving mid-teardown would post to a dispatcher that is going away.
        Closed += (_, _) => (DataContext as MainWindowViewModel)?.Watcher.Dispose();
        // The panel starts expanded, so the toggle's band starts at the full strip height (see TabStripHeight).
        BottomPanelToggleBand.Height = TabStripHeight;
        SolutionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        SolutionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        BottomTabs.AddHandler(PointerPressedEvent, OnBottomTabsPointerPressed, RoutingStrategies.Tunnel);
        BottomTabs.SelectionChanged += OnBottomTabsSelectionChanged;
    }

    /// <summary>Scopes the Disk panel's counter pull to the Disk tab being selected — a 1 s repaint for a panel
    /// nobody is looking at is pure background cost. Its signal log is unaffected: that is event-driven and
    /// stays live, so tabbing back after a burst still shows what happened.
    ///
    /// <para>Distinct from <see cref="OnBottomTabsPointerPressed"/>, which is a tunnel handler for
    /// expand-on-click and is not a selection hook. The source guard is load-bearing: SelectionChanged bubbles,
    /// so the Find results list and the three NuGet lists all raise it through this TabControl, and without the
    /// guard picking a NuGet package would stop the Disk panel's timer.</para></summary>
    private void OnBottomTabsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, BottomTabs)) return;
        if (DataContext is not MainWindowViewModel vm) return;

        if (ReferenceEquals(BottomTabs.SelectedItem, DiskTab)) vm.DiskInsight.StartPolling();
        else vm.DiskInsight.StopPolling();
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
        await Vm.OpenSolutionCommand.ExecuteAsync(path); // refreshes the tree/project list...
        await Vm.ReloadWorkspaceAsync();                 // ...and the semantic snapshot + open tabs
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

    // Same rationale as OnProblemsTreeAttached: the NuGet TabItem is not realized at ctor time, so its three
    // ListBoxes aren't in the visual tree yet. All three realize together when the tab first shows — one
    // AttachedToVisualTree fire is enough to hook them as a set. Tunnel PointerPressed + ClickCount==2 (never
    // DoubleTapped) so presses at row edges / right of text still register (see docs/avalonia.md).
    private bool _nugetListsHooked;
    private void OnNuGetListsAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_nugetListsHooked) return;
        _nugetListsHooked = true;
        NuGetProjectsList.AddHandler(PointerPressedEvent, OnNuGetProjectsPointerPressed, RoutingStrategies.Tunnel);
        NuGetPackagesList.AddHandler(PointerPressedEvent, OnNuGetPackagesPointerPressed, RoutingStrategies.Tunnel);
        NuGetVersionsList.AddHandler(PointerPressedEvent, OnNuGetVersionsPointerPressed, RoutingStrategies.Tunnel);
    }

    private async void OnNuGetProjectsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is ListBox lb && lb.SelectedItem is ProjectEntry entry)
            await Vm.OpenFileAsync(entry.Path);
    }

    private async void OnNuGetPackagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is ListBox lb && lb.SelectedItem is PackageEntry entry)
            await Vm.NuGetVm.OpenMetadataAsync(entry);
    }

    // The first press already committed SelectedVersion via the two-way binding — do not re-assign here. ApplyAsync
    // no-ops if SelectedPackage or SelectedVersion is null, so guarding those is not this handler's job.
    private async void OnNuGetVersionsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        await Vm.NuGetVm.ApplyCommand.ExecuteAsync(null);
    }

    private async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        var path = Vm.Solution.SolutionPath;
        if (path is null) { await OpenSolutionDialogAsync(); return; }
        await Vm.OpenFileAsync(path);
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TreeView tv) return;
        switch (e.Key)
        {
            case Key.Enter:
                if (tv.SelectedItem is TreeNode { Kind: NodeKind.File, Path: not null } node)
                {
                    e.Handled = true;
                    await Vm.OpenFileAsync(node.Path);
                }
                break;
            case Key.PageDown: MovePageSelection(tv, direction: +1, e); break;
            case Key.PageUp:   MovePageSelection(tv, direction: -1, e); break;
            case Key.Home:     JumpTreeSelection(tv, toFirst: true,  e); break;
            case Key.End:      JumpTreeSelection(tv, toFirst: false, e); break;
        }
    }

    // TreeView is un-virtualized, so the realized TreeViewItem descendants — filtered to visible ones — are the
    // full flat, in-order list of rendered rows honoring collapsed subtrees. If virtualization is ever turned
    // on, off-screen items won't be here and these helpers need revisiting.
    private static IReadOnlyList<TreeViewItem> GetVisibleTreeItems(TreeView tv) =>
        tv.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible).ToList();

    private static int IndexOfSelected(IReadOnlyList<TreeViewItem> items, object? selected)
    {
        for (int i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i].DataContext, selected)) return i;
        return -1;
    }

    // Bails without setting Handled when there's nowhere to go — empty tree, tree not yet laid out, or already
    // at the target end. The default ScrollViewer paging may still fire in that case, but the tree has no more
    // rows to page through anyway.
    private static void MovePageSelection(TreeView tv, int direction, KeyEventArgs e)
    {
        var items = GetVisibleTreeItems(tv);
        if (items.Count == 0) return;

        var viewport = tv.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Viewport.Height ?? 0;
        if (viewport <= 0) return;

        var actualIndex = IndexOfSelected(items, tv.SelectedItem);
        // Nothing selected → treat as index 0. PageDown then advances by a page; PageUp clamps back to 0 and
        // effectively selects the first row.
        var current = Math.Max(0, actualIndex);
        var itemHeight = items[current].Bounds.Height > 0 ? items[current].Bounds.Height : items[0].Bounds.Height;
        if (itemHeight <= 0) return;

        var page = Math.Max(1, (int)(viewport / itemHeight));
        var newIndex = Math.Clamp(current + direction * page, 0, items.Count - 1);
        if (newIndex == actualIndex) return;

        SelectAndFocus(tv, items[newIndex]);
        e.Handled = true;
    }

    private static void JumpTreeSelection(TreeView tv, bool toFirst, KeyEventArgs e)
    {
        var items = GetVisibleTreeItems(tv);
        if (items.Count == 0) return;

        var target = toFirst ? 0 : items.Count - 1;
        if (IndexOfSelected(items, tv.SelectedItem) == target) return;

        SelectAndFocus(tv, items[target]);
        e.Handled = true;
    }

    // TreeView tracks keyboard focus separately from selection: the container carrying focus is where arrow-key
    // navigation resumes, not the one carrying the blue selection highlight. Setting `SelectedItem` alone leaves
    // the focus (white outline) on the previous container, so a subsequent arrow key jumps from *there*, not
    // from where the user just paged to. Focusing the target container syncs both.
    private static void SelectAndFocus(TreeView tv, TreeViewItem target)
    {
        tv.SelectedItem = target.DataContext;
        target.Focus(NavigationMethod.Directional);
        target.BringIntoView();
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

    // ── Bottom panel collapse / expand ──
    //
    // Layout state, so it lives here and not in the VM (which has never held any). A bool and a stashed
    // GridLength are the whole of it; nothing persists across restarts and nothing ever collapses implicitly.

    /// <summary>The bottom row's height while collapsed — a bar showing just the tab headers. Deliberately far
    /// below <see cref="ExpandedMinPanelHeight"/>: the floor exists to stop the splitter stranding the panel at
    /// an unusable height, and collapsing is not that.</summary>
    private const double CollapsedPanelHeight = 22;

    /// <summary>The shortest the splitter may leave the panel while it's open. Also the floor on a restore,
    /// since the height we stash was itself subject to it.</summary>
    private const double ExpandedMinPanelHeight = 200;

    /// <summary>Height of the tab-header strip — Fluent's <c>TabItem</c> MinHeight. The toggle is centred inside a
    /// band of this height so it lines up with the tab labels; while collapsed the strip is squeezed down to the
    /// row, and the band follows it to <see cref="CollapsedPanelHeight"/>.</summary>
    private const double TabStripHeight = 48;

    private bool _bottomCollapsed;
    private GridLength _expandedPanelHeight;

    private RowDefinition BottomRow => Workspace.RowDefinitions[2];

    private void OnToggleBottomPanelClick(object? sender, RoutedEventArgs e)
    {
        if (_bottomCollapsed) ExpandBottomPanel();
        else CollapseBottomPanel();
    }

    private void CollapseBottomPanel()
    {
        if (_bottomCollapsed) return;

        // Read the height live rather than assuming the 220 in the markup: that's only the initial value, and
        // the GridSplitter mutates the row's Height in place, so this is whatever the user last dragged it to.
        _expandedPanelHeight = BottomRow.Height;

        // The 200px floor only applies while the panel is open — drop it first, or it would clamp the bar back
        // up to 200 and the collapse would do nothing.
        BottomRow.MinHeight = 0;
        BottomRow.Height = new GridLength(CollapsedPanelHeight);

        // The header strip is squeezed to the row; the toggle's band has to shrink with it or the button
        // centres against a 48px strip that isn't there any more and lands below the bar's bottom edge.
        BottomPanelToggleBand.Height = CollapsedPanelHeight;

        // Load-bearing: left draggable, the splitter would let the user pull a "collapsed" panel back open
        // behind the toggle's back, desyncing the glyph from reality.
        BottomSplitter.IsEnabled = false;

        _bottomCollapsed = true;
        BottomPanelToggle.Content = "▴";
        ToolTip.SetTip(BottomPanelToggle, "Restore panel");
    }

    /// <summary>Restores the height the panel had immediately before it was collapsed. A no-op when already
    /// expanded — that's what lets every reveal path call it unconditionally.</summary>
    private void ExpandBottomPanel()
    {
        if (!_bottomCollapsed) return;

        BottomRow.Height = _expandedPanelHeight;
        BottomRow.MinHeight = ExpandedMinPanelHeight;
        BottomPanelToggleBand.Height = TabStripHeight;
        BottomSplitter.IsEnabled = true;

        _bottomCollapsed = false;
        BottomPanelToggle.Content = "▾";
        ToolTip.SetTip(BottomPanelToggle, "Collapse panel");
    }

    /// <summary>Surfaces a bottom tab: expands the panel if it's collapsed, then selects the tab. Every path
    /// that reveals a bottom tab goes through here, so none of them can forget the expand half.</summary>
    private void ShowBottomTab(TabItem tab)
    {
        ExpandBottomPanel();
        BottomTabs.SelectedItem = tab;
    }

    // Restoring on a tab click can't hang off SelectionChanged: clicking the *already-selected* tab doesn't
    // raise it, so collapsing with Find active and then clicking "Find" would silently do nothing. Hang it off
    // the press instead — tunnel-registered so it runs before the TabItem consumes the press, and we must NOT
    // set e.Handled, or the press would no longer commit the selection. The collapsed guard keeps this inert
    // during normal use; while collapsed the only things under the pointer are the 22px tab headers.
    private void OnBottomTabsPointerPressed(object? sender, PointerPressedEventArgs e) => ExpandBottomPanel();

    private void FocusFind()
    {
        ShowBottomTab(FindTab);
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

    // ── Code-editor context menu (Search / Find usages / Go to definition) ──

    // FindActiveEditor matched a code editor, so ActiveTab is an EditorTabViewModel and its FilePath is non-null.
    private async Task GoToDefinitionAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        await Vm.GoToDefinitionAsync(Vm.ActiveTab!.FilePath!, editor.CaretOffset);
    }

    // Surface the Find tab afterwards — the results are useless behind a collapsed panel or another tab. Not
    // FocusFind: the user wants the results, and focusing + select-alling the query box would be noise here.
    private async Task FindRefsAsync()
    {
        var editor = FindActiveEditor();
        if (editor is null) return;
        await Vm.FindReferencesAsync(Vm.ActiveTab!.FilePath!, editor.CaretOffset);
        ShowBottomTab(FindTab);
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
                    case "CtxRenameItem":
                        // Same cheap synchronous gate as Find usages: a resolvable identifier under the caret in
                        // a C# tab. Whether the symbol is actually renameable (defined in this solution, not a
                        // framework type) is the authoritative in-source check at invoke time — see RenameSymbolAsync.
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
