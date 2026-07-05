# TreeView PageUp/PageDown/Home/End Move Selection

## Context
**Current behavior**: Pressing PageUp / PageDown while the solution `TreeView` has focus scrolls the container's viewport but leaves the selected item unchanged. Home / End do nothing useful — Avalonia's default sends them to the `ScrollViewer`, which pages to top/bottom of scroll offset without moving selection. Arrow up/down already move selection and auto-scroll, so this asymmetry is jarring for a text-editor-shaped app.

**New behavior**: With focus on the solution tree, PageDown / PageUp move the selection down / up by one viewport's worth of rendered items; Home / End move the selection to the first / last visible item in the tree. The tree scrolls the new selection into view (vertical only — horizontal-jitter class handler already handles that).

## Prerequisites
None.

## Scope
### In scope
- `Views/MainWindow.axaml.cs` — extend `OnTreeKeyDown` (already tunnel-registered on `SolutionTree`) to handle `Key.PageDown`, `Key.PageUp`, `Key.Home`, `Key.End`.

### Out of scope
- Ctrl+Home / Ctrl+End variants — Home / End already jump all the way per this ticket, so a modifier variant would be redundant.
- Shift-modified range selection — `TreeView` is single-select today; no multi-select infrastructure to hook.
- Any other list-like control (Find results `ListBox`, NuGet lists) — different UX, different tickets if wanted.
- Horizontal paging — no horizontal scroll semantics on this tree.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 treeview-horizontal-scroll-jitter.md` — the static-ctor class handler on `TreeViewItem.RequestBringIntoView` that zeros horizontal target. Any `BringIntoView()` call added here inherits that behavior — vertical scroll only.
- **Code anchors**:
  - `MainWindow.OnTreeKeyDown` — existing tunnel-strategy `KeyDown` handler on `SolutionTree`. Currently handles Enter to open a file. Extend here.
  - `MainWindow` static ctor — `RequestBringIntoView` class handler, do not touch, but relied on.
  - `MainWindow.axaml` `TreeView x:Name="SolutionTree"` — the only tree in the app.

## Constraints & Gotchas
- `TreeView` is not virtualized in Avalonia's default template. Enumerating `tv.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible)` yields the flat, in-order list of rendered rows, honoring collapsed subtrees. If virtualization is later switched on, off-screen items won't be in that list — revisit then.
- Expansion state lives on `TreeViewItem` containers, not on the `TreeNode` model. `TreeNode.IsExpanded` field exists but is unbound; ignore it. Use the visual-descendant enumeration above as the source of truth.
- Handler must set `e.Handled = true` on the four new keys or the default `ScrollViewer` paging still fires and fights the new selection.
- Home / End should target the first / last *rendered* `TreeViewItem` — i.e., visible in the currently-expanded tree — not the raw first/last of `Vm.Tree`. Matches how PageUp/Down and arrows already treat "the list."
- No-op cases: empty tree, or already at the target end — bail without changing selection or handling the event, so the user's key press doesn't feel swallowed with zero effect. (Bail = return without setting `e.Handled`; default scroll may still fire, but there's nowhere further to go anyway.)
- When nothing is selected, treat PageDown / End as "from before-first" (jump to first / last) and PageUp / Home as "from first" (select first). Consistent with arrow-key onboarding behavior.
- Page size = `floor(viewportHeight / itemHeight)`, clamped to a minimum of 1, so short viewports still advance by at least one row.
- Item height source: any realized `TreeViewItem.Bounds.Height` is fine (all rows are the same fixed template). Prefer the currently-selected container's height; fall back to the first visible item's height.
- Viewport source: the `ScrollViewer` inside the `TreeView`'s template — `tv.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Viewport.Height`. If null (tree not yet laid out), bail — the user can't have been paging a tree that isn't rendered.

## Open Decisions
1. **Extract a `GetVisibleItems()` helper on `MainWindow`** — or inline the LINQ at each call site. Four keys share the enumeration; a private helper reads cleaner. Default: extract, private static method taking the `TreeView`.
2. **PageUp/Down when selection is off-screen** (e.g., user scrolled manually). Options: (a) recentre from the current selection anyway; (b) jump into the current viewport first, then page. Default: (a) — arrow keys already do (a), consistency wins.

## Acceptance Criteria
- [ ] With focus on the solution tree, PageDown moves the selection down by approximately one viewport's worth of rows (page size = `floor(viewportHeight / rowHeight)`, min 1) and scrolls the target into view.
- [ ] PageUp does the same in the opposite direction.
- [ ] Home selects the first rendered tree item and scrolls it into view.
- [ ] End selects the last rendered tree item and scrolls it into view.
- [ ] All four keys respect current expansion state — collapsed subtrees are not stepped into.
- [ ] Horizontal scroll offset does not shift as a side effect of any of the four keys (piggy-backs on the existing `RequestBringIntoView` class handler).
- [ ] Arrow up / down navigation behavior is unchanged.
- [ ] Enter-to-open behavior on a file node is unchanged.
- [ ] Pressing any of the four keys with an empty tree does not throw.

## Implementation

### 1. Extend `OnTreeKeyDown` with a `Key` switch
`Views/MainWindow.axaml.cs`. Handler is already registered tunnel on `SolutionTree` in the ctor, so it runs before the default `ScrollViewer` paging. Turn the existing single-key check into a `switch` on `e.Key`:
- `Enter` — existing branch, unchanged.
- `PageDown` / `PageUp` — resolve page target (see step 3), set selection, `BringIntoView()`, mark handled.
- `Home` / `End` — resolve first / last visible item, set selection, `BringIntoView()`, mark handled.

Keep the handler `async void` shape it already has; new branches are sync but the file open branch still needs the await.

### 2. Add a `GetVisibleTreeItems` helper
Private static (or instance) helper on `MainWindow`. Takes the `TreeView`, returns `IReadOnlyList<TreeViewItem>` of `GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible).ToList()`. Shared by all four new branches. Empty-list early-return handled at each call site.

### 3. Page-size math and index shift
For PageUp / PageDown:
- Fetch visible items via helper. If empty, bail without handling.
- Find current index by matching `DataContext == tv.SelectedItem`; if no match, treat as index 0 for PageDown (moving forward) or index 0 for PageUp (moving backward — clamps to 0 anyway).
- Compute page size: locate the tree's `ScrollViewer` (`GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()`); pull `Viewport.Height`. Item height: current container's `Bounds.Height` if selected, else the first visible item's. `page = Math.Max(1, (int)(viewport / itemHeight))`. Bail (no handling) if viewport or item height is not positive.
- `newIndex = Math.Clamp(current ± page, 0, count - 1)`. If `newIndex == current`, don't handle — user is already at the end.
- Set `tv.SelectedItem = items[newIndex].DataContext`, call `items[newIndex].BringIntoView()`, `e.Handled = true`.

### 4. Home / End
- Fetch visible items; bail on empty.
- Home → target index 0. End → target index `count - 1`.
- If already selected on that target, don't handle (avoid swallowing key with zero effect).
- Same set-selection + BringIntoView + handled pattern as step 3.

### 5. Manual verification prep
No new using directives beyond what `MainWindow.axaml.cs` already imports (`Avalonia.VisualTree`, `Avalonia.Controls`, `Avalonia.Input`, `System.Linq`). Confirm on build.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` — 0 errors, no new warnings.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`.
- [ ] Click any middle node in the tree. Press PageDown — selection jumps ~one viewport down; target row visible.
- [ ] Press PageUp — selection returns roughly toward the start; target row visible.
- [ ] Press End — selection jumps to the last visible tree row.
- [ ] Press Home — selection jumps to the very first tree row (solution node).
- [ ] Expand a project, then collapse one of its folders. PageDown across the collapsed folder — selection skips the folder's hidden children (respects expansion).
- [ ] Repeat all four keys with the tree scrolled so the selected item is off-screen — selection still moves relative to itself (not to viewport contents).
- [ ] Arrow up / down still moves selection one row and scrolls as before.
- [ ] Enter on a file node still opens the file.
- [ ] Horizontal scroll offset does not budge on any PageUp / PageDown / Home / End press (regression check for `treeview-horizontal-scroll-jitter` fix).
- [ ] Open a solution, immediately press End before clicking anything — no crash; last item selected.
- [ ] Load an empty / not-yet-loaded tree state (no solution open); press each key — no crash, nothing selected.
