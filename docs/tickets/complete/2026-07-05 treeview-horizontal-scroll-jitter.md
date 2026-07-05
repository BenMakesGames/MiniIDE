# TreeView Horizontal Auto-Scroll Jitter on Selection

## Context
**Current behavior**: Clicking or arrow-keying items in the solution `TreeView` causes the horizontal scroll offset to jump around per item — different depths / name lengths yield different X offsets. Distracting; user never asked for horizontal scroll on selection.

**New behavior**: Selection scrolls vertically only. Horizontal offset unchanged. Manual horizontal scroll (drag scrollbar) still works.

## Prerequisites
- None.

## Scope
### In scope
- `Views/MainWindow.axaml.cs` — add class handler that neutralises the horizontal component of `RequestBringIntoView` on `TreeViewItem`.

### Out of scope
- Any change to vertical auto-scroll (still wanted for arrow-key navigation past viewport).
- Disabling `TreeView.AutoScrollToSelectedItem` (kills vertical scroll too — wrong tool).
- Global fix for other `TreeView`s / `ListBox`es — only one `TreeView` in the app.

## Relevant Docs & Anchors
- **Code anchor**: `MainWindow` static constructor.
- **Upstream discussion**: [AvaloniaUI/Avalonia #17020](https://github.com/AvaloniaUI/Avalonia/discussions/17020) — recommended pattern from Avalonia maintainers.
- **Related upstream issue**: [AvaloniaUI/Avalonia #15335](https://github.com/AvaloniaUI/Avalonia/issues/15335) — root cause description.

## Constraints & Gotchas
- `RequestBringIntoView` is a **bubbling** routed event, not tunneling. Registering a `Tunnel` handler on `TreeView` fires never.
- `ScrollContentPresenter` handles the event during bubble, inside the `TreeView` template. Any instance handler attached on the `TreeView` ancestor runs **after** the presenter has already scrolled — too late to influence.
- `ScrollViewer.BringIntoViewOnFocusChange="False"` on the `TreeView` does **not** stop this — `TreeView` raises `BringIntoView` programmatically on selection, not only via focus change.
- Correct interception point: class handler on `TreeViewItem` type. Class handler on the event source fires before the event bubbles to the presenter, so the presenter reads the modified `TargetRect`.
- `e.TargetRect.WithWidth(0)` collapses the horizontal target to a zero-width point at the item's left edge → always already visible → presenter skips horizontal scroll. Vertical (Y + Height) untouched → vertical scroll preserved.

## Acceptance Criteria
- [x] Arrow-keying up/down the solution tree does not shift horizontal scroll offset.
- [x] Clicking a deeply-nested item does not shift horizontal scroll offset.
- [x] Vertical auto-scroll still occurs when arrowing past the viewport top/bottom.
- [x] Manual horizontal scrollbar drag still works.

## Implementation

### 1. Register class handler in `MainWindow` static ctor
`Views/MainWindow.axaml.cs`: add a static constructor that calls `Control.RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>` with `RoutingStrategies.Bubble`. Handler body: `e.TargetRect = e.TargetRect.WithWidth(0);`. Class handlers run once per type and cannot be accidentally attached twice on window re-open — right place for a global-to-this-window override.

## Test Plan
- [x] `dotnet build src/MiniIde/MiniIde.csproj` — 0 errors.
- [x] Launch, open a solution, click each tree node type (solution / project / folder / file). Horizontal scroll does not move.
- [x] Arrow-key top-to-bottom through tree. Horizontal scroll static. Vertical scroll follows selection past viewport edge.
- [x] Drag horizontal scrollbar manually — still scrolls.

## Learnings

### Why it took three tries
1. **`Tunnel` handler on `TreeView`** — wrong: event bubbles, tunnel handler never fires.
2. **`ScrollViewer.BringIntoViewOnFocusChange="False"` on `TreeView`** — wrong: `TreeView` triggers `BringIntoView` programmatically on selection, independent of focus-change plumbing.
3. **Class handler on `TreeViewItem`** — right: intercepts at event source, before `ScrollContentPresenter` reads `TargetRect` during bubble.

### Do not remove
The static ctor block in `MainWindow` looks like dead code (no reference to it anywhere in the file). It is not — `RoutedEvent.AddClassHandler` registers globally on the `TreeViewItem` type. Deleting it revives the jitter.

### Related pitfall to avoid
`TreeView.AutoScrollToSelectedItem = false` is the WPF-style fix and disables both axes. Do not reach for it — vertical scroll on arrow-key navigation is wanted.
