using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Microsoft.CodeAnalysis.Classification;
using MiniIde.Models;
using MiniIde.ViewModels;

namespace MiniIde.Views;

/// <summary>The non-generic face of a binder, so the window can hold a list of them and route the three
/// editor lifecycle events to all of them without knowing their tab or state types. Each binder is a no-op
/// for an editor showing another kind of tab, so "offer it to every binder" is correct by construction —
/// this is a merged <em>dispatch</em>, not a merged registry (see <see cref="TabEditorBinder{TTab,TState}"/>,
/// which explains why the registries must stay separate).</summary>
internal interface ITabEditorBinder
{
    void Bind(TextEditor editor);
    void Unbind(TextEditor editor);
}

/// <summary>Owns the imperative <see cref="TextEditor"/> ↔ <see cref="DocumentTabViewModel"/> binding for
/// one kind of tab. <c>TextEditor.Document</c> is a CLR property, not an <c>AvaloniaProperty</c>, so it
/// cannot be bound in XAML (see docs/avaloniaedit.md) — the view must assign it, and a <c>TabControl</c>
/// reuses one realized control across every tab of the same <c>DataType</c>, swapping its
/// <c>DataContext</c>. Hence: per-control state, rebind on every attach/<c>DataContextChanged</c>, and
/// unbind on <c>DetachedFromVisualTree</c> (switching to a tab of a *different* <c>DataType</c> realizes a
/// different control and discards this one).
///
/// <para>Instantiate one binder <b>per editor kind</b>, never a merged registry: because the two
/// <c>DataTemplate</c>s realize disjoint controls, a per-kind registry structurally contains only that
/// kind's editors — which is what lets <see cref="EditorFor"/> answer "the active code editor" without a
/// visual-tree scan and without a view-model type guard.</para>
///
/// <para>The per-kind difference is expressed as two delegates that each return their own undo, so setup
/// and teardown are written adjacently and cannot drift apart.</para></summary>
/// <param name="setUpControl">One-time setup for a freshly-realized control; returns the state it created
/// plus the action that undoes the setup exactly.</param>
/// <param name="attachTab">Wires a tab's document into the control; returns the action that unwires it.</param>
internal sealed class TabEditorBinder<TTab, TState>(
    Func<TextEditor, (TState State, Action TearDown)> setUpControl,
    Func<TextEditor, TState, TTab, Action> attachTab) : ITabEditorBinder
    where TTab : DocumentTabViewModel
    where TState : class
{
    private readonly Dictionary<TextEditor, ControlBinding> _bindings = new();

    private sealed class ControlBinding(TState state, Action tearDownControl)
    {
        public TState State { get; } = state;
        public Action TearDownControl { get; } = tearDownControl;
        public TTab? CurrentTab { get; set; }
        public Action? DetachTab { get; set; }
    }

    /// <summary>Points <paramref name="editor"/> at the document of the tab it now shows. Call from both
    /// <c>AttachedToVisualTree</c> and <c>DataContextChanged</c>; a no-op for a control showing another
    /// kind of tab, and for a redundant rebind onto the tab it already shows.</summary>
    public void Bind(TextEditor editor)
    {
        if (editor.DataContext is not TTab tab) return;

        if (!_bindings.TryGetValue(editor, out var binding))
        {
            var (state, tearDown) = setUpControl(editor);
            binding = new ControlBinding(state, tearDown);
            _bindings[editor] = binding;
        }

        if (ReferenceEquals(binding.CurrentTab, tab)) return;

        binding.DetachTab?.Invoke();
        binding.CurrentTab = tab;
        editor.Document = tab.Document;
        binding.DetachTab = attachTab(editor, binding.State, tab);
    }

    /// <summary>The exact inverse of <see cref="Bind"/>. Call from <c>DetachedFromVisualTree</c>: unwires
    /// the current tab, undoes the one-time control setup, and forgets the control — so nothing it owns
    /// outlives it, and a re-attach of the same instance re-initializes cleanly rather than double-adding
    /// transformers or handlers.</summary>
    public void Unbind(TextEditor editor)
    {
        if (!_bindings.Remove(editor, out var binding)) return;
        binding.DetachTab?.Invoke();
        binding.TearDownControl();
    }

    /// <summary>The editor currently showing <paramref name="tab"/>, or null when <paramref name="tab"/>
    /// isn't this binder's kind of tab (or isn't realized).</summary>
    public TextEditor? EditorFor(TabViewModelBase? tab)
    {
        if (tab is not TTab typed) return null;
        foreach (var (editor, binding) in _bindings)
            if (ReferenceEquals(binding.CurrentTab, typed)) return editor;
        return null;
    }

    /// <summary>The per-control state <paramref name="editor"/> was set up with, or null if unbound.</summary>
    public TState? StateFor(TextEditor? editor) =>
        editor is not null && _bindings.TryGetValue(editor, out var b) ? b.State : null;
}

/// <summary>Per-control state of an output editor. <see cref="WasAtBottom"/> captures the follow state at the
/// instant a document change is about to be applied so <c>Changed</c> can decide whether to scroll — and is
/// stored here, not derived at <c>Changed</c> time, because inserting a line grows the extent before
/// AvaloniaEdit lays the view out again, which would wrongly answer "no" to "are we at the bottom?" in the
/// gap.</summary>
internal sealed class OutputEditorState
{
    public bool WasAtBottom = true;
}

/// <summary>The two per-kind wirings, and the factories that pair each with the shared binder.</summary>
internal static class TabEditorBinder
{
    private const int HighlightDebounceMs = 200;

    /// <summary>A code editor's per-control state is just its colorizer — kept reachable so the context menu
    /// can read cached classifications without re-running the classifier.</summary>
    public static TabEditorBinder<EditorTabViewModel, RoslynColorizer> ForCodeTabs(
        Func<string, Task<IReadOnlyList<ClassifiedSpan>>> classify)
        => new(editor => SetUpCodeEditor(editor, classify), AttachCodeTab);

    public static TabEditorBinder<OutputTabViewModel, OutputEditorState> ForOutputTabs()
        => new(SetUpOutputEditor, AttachOutputTab);

    // ── Code tabs: colorizer + debounced reclassify + right-click caret placement ──

    private static (RoslynColorizer State, Action TearDown) SetUpCodeEditor(
        TextEditor editor, Func<string, Task<IReadOnlyList<ClassifiedSpan>>> classify)
    {
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        // Navigating from a find hit / problem / go-to-def scrolls the view; without a highlighted caret line
        // there's nothing to tell you *which* line it landed on.
        editor.Options.HighlightCurrentLine = true;

        var colorizer = new RoslynColorizer(classify);
        editor.TextArea.TextView.LineTransformers.Add(colorizer);

        // Tunnel so we place the caret before AvaloniaEdit's own pointer logic and before the menu opens.
        EventHandler<PointerPressedEventArgs> onPointerPressed = OnEditorPointerPressed;
        editor.AddHandler(InputElement.PointerPressedEvent, onPointerPressed, RoutingStrategies.Tunnel);

        return (colorizer, () =>
        {
            editor.TextArea.TextView.LineTransformers.Remove(colorizer);
            editor.RemoveHandler(InputElement.PointerPressedEvent, onPointerPressed);
        });
    }

    private static Action AttachCodeTab(TextEditor editor, RoslynColorizer colorizer, EditorTabViewModel tab)
    {
        var document = tab.Document;
        CancellationTokenSource? debounceCts = null;

        // Debounce: a burst of keystrokes reclassifies the whole document (a full Roslyn snapshot)
        // only once typing pauses, instead of on every character.
        EventHandler onTextChanged = (_, _) =>
        {
            debounceCts?.Cancel();
            debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            debounceCts = cts;
            _ = DebouncedRefreshAsync(colorizer, editor, tab.Mode, cts.Token);
        };
        document.TextChanged += onTextChanged;

        // Initial highlight of the newly-shown document is immediate, not debounced.
        _ = RefreshAndRedraw(colorizer, editor, tab.Mode);

        return () =>
        {
            document.TextChanged -= onTextChanged;
            // Drop any reclassify still pending from the tab we're leaving.
            debounceCts?.Cancel();
            debounceCts?.Dispose();
            debounceCts = null;
        };
    }

    private static async Task DebouncedRefreshAsync(RoslynColorizer colorizer, TextEditor editor, HighlightMode mode, CancellationToken ct)
    {
        try { await Task.Delay(HighlightDebounceMs, ct); }
        catch (OperationCanceledException) { return; }
        await RefreshAndRedraw(colorizer, editor, mode);
    }

    // Exactly one highlight path may be live at a time: editor.SyntaxHighlighting and the colorizer both
    // write foreground brushes, so leaving the other armed bleeds the prior mode's colors through.
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

    private static void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
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

    // ── Output tabs: tail-follow only. Read-only, no colorizer. ──

    private static (OutputEditorState State, Action TearDown) SetUpOutputEditor(TextEditor editor)
    {
        // AvaloniaEdit otherwise lets the view scroll a full viewport past the final line, which on an output
        // tab reads as roughly half the pane blank below the last log line and makes "pin to tail" look like
        // it stopped halfway. Code editors sometimes want that extra room; a readonly log never does.
        editor.Options.AllowScrollBelowDocument = false;
        return (new OutputEditorState(), () => { });
    }

    private static Action AttachOutputTab(TextEditor editor, OutputEditorState state, OutputTabViewModel tab)
    {
        var document = tab.Document;
        var scroll = (ILogicalScrollable)editor.TextArea.TextView;

        // AvaloniaEdit's own ScrollToLine centres the target line in the viewport (VisualYPosition.LineMiddle
        // plus a 30% minimum-scroll fraction), which for the tail of the document leaves roughly half the
        // viewport blank below the final line. Pinning the offset directly to Extent - Viewport puts the last
        // line flush against the bottom edge. Posted at Background priority so the TextView has run the
        // measure pass triggered by the just-applied document change; otherwise Extent still reflects the
        // pre-append height and we land one line short.
        void PinToTail() => Dispatcher.UIThread.Post(() =>
        {
            var targetY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, targetY);
        }, DispatcherPriority.Background);

        // Don't overwrite tab.StickToEnd from the current offset: a rebind resets the view's scroll but
        // the view model has already remembered the user's intent, and scrolling to the bottom no longer
        // re-engages follow (only the overlay button does), so the current offset alone isn't a signal for
        // it. Instead, if the tab is pinned, snap the newly-bound view to the tail.
        if (tab.StickToEnd) PinToTail();

        // Changing fires pre-mutation, Changed post — so "was the view at the bottom before this append?"
        // has to be captured on Changing and consumed on Changed. Reading tab.StickToEnd here (rather than
        // recomputing) carries the user's intent across the gap in which the extent grows but the offset has
        // not caught up yet.
        EventHandler<DocumentChangeEventArgs> onChanging = (_, _) => state.WasAtBottom = tab.StickToEnd;
        EventHandler<DocumentChangeEventArgs> onChanged = (_, _) =>
        {
            if (state.WasAtBottom && editor.Document.LineCount > 0) PinToTail();
        };

        // Follow mode releases only on explicit user interaction with the editor — never on a scroll
        // invalidation, because AvaloniaEdit emits those for extent growth from an append, for the pin
        // itself, and for transient offset clamps on rebind or external triggers (a git button click
        // routing focus / layout through the view). Observing those as "the user scrolled away" wrongly
        // unpins while the user has done nothing. The three gestures that actually mean "I'm reading, not
        // following" are:
        //   • Pressing into the editor — placing a caret, starting a selection, or dragging the scrollbar
        //     thumb. Attached to the editor itself (not the TextArea) so a scrollbar press counts.
        //   • Mousewheel / trackpad scrolling the viewport.
        //   • Keyboard navigation (arrows, PageUp/Down, Home/End) that moves the caret or page.
        // All three tunnel so we release before AvaloniaEdit's own handlers run.
        EventHandler<PointerPressedEventArgs> onPointerPressed = (_, _) => tab.StickToEnd = false;
        EventHandler<PointerWheelEventArgs> onPointerWheel = (_, _) => tab.StickToEnd = false;
        EventHandler<KeyEventArgs> onKeyDown = (_, e) =>
        {
            // Filter to navigation keys so Ctrl+C on an unselected tail (or a modifier release) doesn't
            // silently drop follow mode. These are the keys AvaloniaEdit would act on to move the caret
            // or scroll the view.
            switch (e.Key)
            {
                case Key.Up: case Key.Down: case Key.Left: case Key.Right:
                case Key.PageUp: case Key.PageDown: case Key.Home: case Key.End:
                    tab.StickToEnd = false;
                    break;
            }
        };
        editor.AddHandler(InputElement.PointerPressedEvent, onPointerPressed, RoutingStrategies.Tunnel);
        editor.AddHandler(InputElement.PointerWheelChangedEvent, onPointerWheel, RoutingStrategies.Tunnel);
        editor.TextArea.AddHandler(InputElement.KeyDownEvent, onKeyDown, RoutingStrategies.Tunnel);

        Action onScrollRequested = () =>
        {
            if (editor.Document.LineCount > 0) PinToTail();
        };

        document.Changing += onChanging;
        document.Changed += onChanged;
        tab.ScrollToEndRequested += onScrollRequested;

        return () =>
        {
            document.Changing -= onChanging;
            document.Changed -= onChanged;
            tab.ScrollToEndRequested -= onScrollRequested;
            editor.RemoveHandler(InputElement.PointerPressedEvent, onPointerPressed);
            editor.RemoveHandler(InputElement.PointerWheelChangedEvent, onPointerWheel);
            editor.TextArea.RemoveHandler(InputElement.KeyDownEvent, onKeyDown);
        };
    }
}
