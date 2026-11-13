using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using MiniIde.ViewModels;

namespace MiniIde.Views;

/// <summary>Owns the imperative <see cref="TextEditor"/> ↔ <see cref="OutputTabViewModel"/> binding.
/// <c>TextEditor.Document</c> is a CLR property, not an <c>AvaloniaProperty</c>, so it cannot be bound in XAML
/// (see docs/avaloniaedit.md) — the view must assign it, and a <c>TabControl</c> reuses one realized control
/// across every tab of the same <c>DataType</c>, swapping its <c>DataContext</c>. Hence: per-control state,
/// rebind on every attach/<c>DataContextChanged</c>, and unbind on <c>DetachedFromVisualTree</c> (switching to a
/// tab of a <em>different</em> <c>DataType</c> realizes a different control and discards this one).</summary>
internal sealed class OutputEditorBinder
{
    private readonly Dictionary<TextEditor, ControlBinding> _bindings = new();

    /// <summary>Per-control state. <see cref="WasAtBottom"/> captures the follow state at the instant a document
    /// change is about to be applied so <c>Changed</c> can decide whether to scroll — and is stored here, not
    /// derived at <c>Changed</c> time, because inserting a line grows the extent before AvaloniaEdit lays the view
    /// out again, which would wrongly answer "no" to "are we at the bottom?" in the gap.</summary>
    private sealed class ControlBinding
    {
        public bool WasAtBottom = true;
        public OutputTabViewModel? CurrentTab;
        public Action? DetachTab;
    }

    /// <summary>Points <paramref name="editor"/> at the document of the tab it now shows. Call from both
    /// <c>AttachedToVisualTree</c> and <c>DataContextChanged</c>; a no-op for a redundant rebind onto the tab it
    /// already shows.</summary>
    public void Bind(TextEditor editor)
    {
        if (editor.DataContext is not OutputTabViewModel tab) return;

        if (!_bindings.TryGetValue(editor, out var binding))
        {
            // AvaloniaEdit otherwise lets the view scroll a full viewport past the final line, which reads as
            // roughly half the pane blank below the last log line and makes "pin to tail" look like it stopped
            // halfway.
            editor.Options.AllowScrollBelowDocument = false;
            binding = new ControlBinding();
            _bindings[editor] = binding;
        }

        if (ReferenceEquals(binding.CurrentTab, tab)) return;

        binding.DetachTab?.Invoke();
        binding.CurrentTab = tab;
        editor.Document = tab.Document;
        binding.DetachTab = Attach(editor, binding, tab);
    }

    /// <summary>The exact inverse of <see cref="Bind"/>. Call from <c>DetachedFromVisualTree</c>: unwires the
    /// current tab and forgets the control, so nothing it owns outlives it and a re-attach of the same instance
    /// re-initializes cleanly rather than double-adding handlers.</summary>
    public void Unbind(TextEditor editor)
    {
        if (!_bindings.Remove(editor, out var binding)) return;
        binding.DetachTab?.Invoke();
    }

    /// <summary>Tail-follow wiring for one tab; returns the action that unwires it.</summary>
    private static Action Attach(TextEditor editor, ControlBinding state, OutputTabViewModel tab)
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
