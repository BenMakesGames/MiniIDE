# Fix Explorer Double-Click Dead Zones at Row Edges

## Context
**Current behavior**: In the solution `TreeView` (left explorer), single-click selection works anywhere on a row, but *double-click to open* fails when the click lands near the very top or bottom edge of an item — and, less obviously, anywhere to the right of the filename text. It feels random because selection succeeds while "open" silently does nothing.

**New behavior**: Double-clicking anywhere on a tree row — top edge, bottom edge, right of the text, or on the text — reliably opens files (and expands projects), matching the current branch logic. The whole row becomes a uniform interaction target.

Root cause: "open" is wired to Avalonia's `DoubleTapped` gesture (`SolutionTree` in `MainWindow.axaml`, handler `OnTreeDoubleTapped` in `MainWindow.axaml.cs`). Avalonia only raises `DoubleTapped` when **both** pointer presses resolve to the *same* source element ([AvaloniaUI/Avalonia #8733](https://github.com/AvaloniaUI/Avalonia/issues/8733)). The row template's content (a horizontal `StackPanel` with both `TextBlock`s `VerticalAlignment="Center"`, no `Background`) does not fill the `TreeViewItem`, whose FluentTheme default adds vertical padding. Near an edge the two clicks straddle the boundary between the content and the container padding → two different source elements → no `DoubleTapped`. Selection still works because `TreeViewItem` selects on press anywhere in its bounds — hence the inconsistency.

The fix has two layers (belt-and-suspenders, both requested):
- **Primary (source-agnostic):** replace the `DoubleTapped` wiring with a `PointerPressed` handler acting on `ClickCount == 2`. This fires wherever the press registers — i.e. everywhere selection already works — so it does not depend on both presses hitting the same element.
- **Supporting (uniform hit target):** make the whole row hit-testable and stretch the item content, eliminating the right-of-text and edge dead zones for selection/hover consistency and guaranteeing a single source element if `ClickCount` turns out to be source-sensitive.

## Prerequisites
- None. Related work already merged: `2026-07-05 open-solution-file-on-name-doubleclick.md` (the `Background="Transparent"` hit-testing gotcha) and `2026-07-05 treeview-horizontal-scroll-jitter.md` (the static-ctor class handler on `TreeViewItem` — must remain).

## Scope
### In scope
- `Views/MainWindow.axaml` — row `ItemTemplate` + a `TreeView.ItemContainerTheme` for `TreeViewItem`; remove the `DoubleTapped` attribute.
- `Views/MainWindow.axaml.cs` — register a tunneling `PointerPressed` handler on `SolutionTree`; repurpose the existing `OnTreeDoubleTapped` body.

### Out of scope
- `OnSolutionNameDoubleTapped` (top-bar solution label — a separate element, works fine, leave untouched).
- Any change to selection logic, the `TreeNode`/`SolutionNode` model, or `SolutionServiceExtensions.EnsureExpanded`.
- New folder-expand-on-double-click behavior. Folders (`NodeKind.Folder`) are **not** special-cased by the current handler and must stay that way — this is a bug fix, not a behavior addition.
- The static `RequestBringIntoView` class handler in the `MainWindow` static ctor — do not touch (see Constraints).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — `TreeView` named `SolutionTree` with `DoubleTapped="OnTreeDoubleTapped"`; its `TreeDataTemplate` (horizontal `StackPanel` + two `TextBlock`s).
  - `Views/MainWindow.axaml.cs` — `OnTreeDoubleTapped` (current branch logic to preserve); `OnTreeKeyDown` + its registration `SolutionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel)` in the `MainWindow` constructor (mirror this registration pattern).
  - `Models/SolutionNode.cs` — `enum NodeKind { Solution, Project, Folder, File }`; `TreeNode.Kind`, `TreeNode.Path`.
  - `SolutionServiceExtensions.EnsureExpanded` — called for `Project` nodes.
  - `MainWindowViewModel.OpenFileAsync` — de-dups by path; opens/reactivates a tab.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 open-solution-file-on-name-doubleclick.md` — Learnings confirm Avalonia `TextBlock`/content needs `Background="Transparent"` to be hit-testable across whitespace, not just over glyph ink.
  - `docs/tickets/complete/2026-07-05 treeview-horizontal-scroll-jitter.md` — the static-ctor `TreeViewItem` class handler and why it must not be removed.
- **Upstream**: [AvaloniaUI/Avalonia #8733](https://github.com/AvaloniaUI/Avalonia/issues/8733) (DoubleTapped drops when source changes); community workaround is `PointerPressed` + `ClickCount`.
- **Local docs**: `docs/avalonia.md` (Avalonia gotchas — extend if a new one is confirmed).

## Constraints & Gotchas
- **Tunneling registration required.** `TreeViewItem` handles `PointerPressed` for selection and may mark it handled. A `PointerPressed="..."` attribute in XAML (bubble) can therefore miss. Register in the constructor with `RoutingStrategies.Tunnel` (mirror the existing `OnTreeKeyDown` registration) so the handler runs before the item consumes the press. Alternatively `AddHandler(..., handledEventsToo: true)` — implementer's call (see Open Decisions).
- **Remove the old `DoubleTapped` wiring** when adding `PointerPressed`. Leaving both risks opening a file twice on a successful double-click.
- **Do not `e.Handled = true`** in the `PointerPressed` handler — selection must still occur on the same press. Only act (open/expand) when `ClickCount == 2`; otherwise fall through untouched.
- **Preserve the static ctor.** The `RequestBringIntoViewEvent.AddClassHandler<TreeViewItem>` block in `MainWindow`'s static constructor looks unreferenced but is load-bearing (horizontal-jitter fix). Do not remove or merge it away.
- **FluentTheme visual regression risk.** The `ItemContainerTheme` must `BasedOn` the Fluent default `TreeViewItem` theme, not replace its template — otherwise the chevron/twistie, indentation, and selection highlight break. Verify row height and indent look unchanged after the padding/alignment change.
- **`ClickCount` source-sensitivity is unconfirmed for Avalonia 12.** It is the standard workaround and expected to be more forgiving than `DoubleTapped`, but has not been verified in this codebase. The supporting layer (uniform hit target) is what makes it robust if `ClickCount` also proves source-sensitive — implement both, then confirm during testing.

## Open Decisions
1. **Node resolution in the handler** — read `SolutionTree.SelectedItem` (matches current `OnTreeDoubleTapped` and `OnTreeKeyDown`) vs. hit-test `e.Source` up to the `TreeViewItem`'s `DataContext`. Default: `SelectedItem` — the first press commits selection before the second press's `ClickCount == 2`, so it resolves to the right node.
2. **Tunnel vs. `handledEventsToo`** — both ensure the handler sees the press despite item selection handling. Default: `RoutingStrategies.Tunnel`, mirroring `OnTreeKeyDown`.
3. **Container `Padding` normalization** — whether to also zero/reduce `TreeViewItem` `Padding` in the container theme. Default: leave Fluent padding as-is; `HorizontalContentAlignment`/`VerticalContentAlignment="Stretch"` + transparent row background plus the source-agnostic `PointerPressed` layer should cover the dead strip. Only adjust padding if manual edge-testing still misses.

## Acceptance Criteria
- [ ] Double-clicking a file node within ~1–2px of its top or bottom edge opens the file in an editor tab.
- [ ] Double-clicking a file node in the empty area to the right of its filename opens the file.
- [ ] Double-clicking a project node (any part of the row, including edges) expands it (`EnsureExpanded`).
- [ ] Double-clicking a folder node does nothing new (unchanged from current behavior) and does not throw.
- [ ] A successful double-click opens the file exactly once (no duplicate tab, no double-open) — the old `DoubleTapped` path no longer coexists.
- [ ] Single-click selection continues to work anywhere on a row, and `Enter` on a selected file still opens it (`OnTreeKeyDown` intact).
- [ ] The `TreeView` markup contains no `DoubleTapped="OnTreeDoubleTapped"` attribute; the open/expand logic runs from the `PointerPressed` path.

## Implementation

### 1. Make the row content a uniform hit target
`Views/MainWindow.axaml`, the `TreeDataTemplate` root `StackPanel`: add `Background="Transparent"` so its empty pixels (right of the text, and any gaps) are hit-testable — same gotcha resolved for the solution-name label. Leave the two `TextBlock`s as-is.

### 2. Stretch the item container
`Views/MainWindow.axaml`, on the `TreeView`: add a `TreeView.ItemContainerTheme` containing a `ControlTheme TargetType="TreeViewItem"` that `BasedOn` the Fluent default `TreeViewItem` theme (e.g. `{StaticResource {x:Type TreeViewItem}}`) and sets `HorizontalContentAlignment` and `VerticalContentAlignment` to `Stretch`. Intent: the content presenter fills the row so the transparent `StackPanel` (step 1) covers the full item, removing edge/right dead zones. Do **not** override the template. See Open Decision 3 re: padding.

### 3. Remove the `DoubleTapped` wiring
`Views/MainWindow.axaml`: delete the `DoubleTapped="OnTreeDoubleTapped"` attribute from the `SolutionTree` `TreeView`. The open/expand action moves to `PointerPressed` (steps 4–5); keeping both double-fires.

### 4. Register a tunneling `PointerPressed` handler
`Views/MainWindow.axaml.cs`, in the `MainWindow` constructor next to the existing `SolutionTree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel)`: add `SolutionTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel)`. Tunnel so the handler runs before `TreeViewItem` consumes the press for selection.

### 5. Repurpose the handler body
`Views/MainWindow.axaml.cs`: rename/replace `OnTreeDoubleTapped` with `async void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)`. Guard: return early unless `e.ClickCount == 2`. Then reuse the existing branch logic verbatim — resolve the node from `SolutionTree.SelectedItem` as a `TreeNode` (Open Decision 1), and: `Project` → `SolutionServiceExtensions.EnsureExpanded(node)`; `File` with non-null `Path` → `await Vm.OpenFileAsync(node.Path)`. Do not set `e.Handled` (selection must still happen on this press). No branch for `Folder`/`Solution` — preserve current behavior.

### 6. Note the confirmed gotcha
If testing confirms `PointerPressed` + `ClickCount == 2` is the reliable pattern here (and whether `ClickCount` was source-sensitive), add a one-line entry to `docs/avalonia.md` so the next person reaches for it directly instead of `DoubleTapped` on templated item rows.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` — 0 errors.
- [ ] Launch (`scripts/run.ps1`), open a solution. Double-click a file **at the very top edge** of its row → opens. Repeat **at the very bottom edge** → opens. Repeat **far right of the filename** → opens. (This is the reported bug; confirm all three.)
- [ ] Double-click a project node at an edge → expands. Double-click a folder node → nothing happens, no exception.
- [ ] Double-click a file once → one tab. Double-click the same file again → same tab reactivates, no duplicate, no double-open flash.
- [ ] Single-click various rows (including edges) → selection highlight tracks correctly. Arrow-key up/down → selection moves; `Enter` on a file → opens (regression on `OnTreeKeyDown`).
- [ ] Regression: horizontal scroll does not jitter on selection (static-ctor class handler still present and effective).
- [ ] Visual check: row height, indentation, chevron, and selection highlight look unchanged vs. before the container-theme change.
