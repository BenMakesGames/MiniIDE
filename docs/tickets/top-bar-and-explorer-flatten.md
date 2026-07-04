# Top Bar Split + Explorer Flatten

## Context
**Current behavior**: Top toolbar row is a single DockPanel with a `Startup:` label, project ComboBox, Play, Stop — all left-docked. Solution explorer (leftmost `TreeView`) shows a single solution root node the user must expand to see projects.

**New behavior**: Top toolbar splits into two zones: solution name pinned left, project ComboBox + Play + Stop pinned right (no `Startup:` label). Explorer drops the solution root — projects appear as top-level `TreeView` items.

## Prerequisites
- None. Builds on `startup-dropdown-project-kinds.md` (already implemented in `SolutionService.Projects` + `ProjectEntry`).

## Scope
### In scope
- `Views/MainWindow.axaml` row 1 layout — split left/right zones, drop label.
- `MainWindowViewModel` — expose solution display name for left zone.
- `SolutionService.LoadAsync` / `MainWindowViewModel.OpenSolutionAsync` — return + bind project nodes without a wrapping solution root.

### Out of scope
- Changing `TreeNode` shape or removing `NodeKind.Solution` enum value (may still be used elsewhere; leave the type alone).
- Restyling the toolbar beyond the split (fonts, spacing niceties, icons).
- Showing solution status when none loaded — leave left zone empty (see Open Decisions).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — row 1 DockPanel (`Startup:` label + ComboBox + Play/Stop), row 2 `TreeView x:Name="SolutionTree"`.
  - `MainWindowViewModel.OpenSolutionAsync` (`Tree.Clear(); Tree.Add(root);`).
  - `SolutionService.LoadAsync` — currently builds a `TreeNode` solution root and returns it.
- **Related tickets**: `docs/tickets/startup-dropdown-project-kinds.md` — established `Projects` / `ProjectEntry` used by the ComboBox.

## Constraints & Gotchas
- `TreeView.ItemsSource="{Binding Tree}"` binds an `ObservableCollection<TreeNode>` — flattening means adding project nodes directly, not swapping the root's `Children` in.
- Double-tap handler `OnTreeDoubleTapped` already dispatches by `NodeKind`; unchanged. Project nodes stay `NodeKind.Project` at top level.
- `Solution.SolutionPath` is `string?`; SolutionName binding must handle null (empty display before first load).
- Row 1 `DockPanel` uses `DockPanel.Dock="Left"` throughout; a right-anchored group needs `DockPanel.Dock="Right"` (docks in declaration order — right-docked children come *before* left-docked ones in markup if you want the right group flush-right without a filler `TextBlock`). Alternative: a `Grid` with two columns.

## Open Decisions
1. **Solution name format** — filename with extension (`MiniIde.slnx`) vs. without (`MiniIde`). Default: without extension.
2. **`SolutionService.LoadAsync` return shape** — keep returning root `TreeNode` and have the VM flatten to `root.Children`, vs. change signature to `IReadOnlyList<TreeNode>`. Default: change signature — the wrapping root has no remaining consumer.
3. **Top-bar container** — `DockPanel` with left+right docks vs. two-column `Grid`. Default: `Grid ColumnDefinitions="Auto,*,Auto"` (left name, spacer, right controls); clearer intent than dock ordering.

## Acceptance Criteria
- [ ] Row 1 shows solution name flush-left and ComboBox + Play + Stop flush-right; no `Startup:` label anywhere.
- [ ] With no solution loaded, the left zone renders empty (no placeholder text).
- [ ] After `OpenSolutionAsync`, left zone shows solution filename (extension per Open Decision #1 default).
- [ ] `MainWindowViewModel` exposes an observable `SolutionName` (or equivalent) bound by the left zone; updates on `OpenSolutionAsync`.
- [ ] Explorer `TreeView` top-level items are project nodes (`NodeKind.Project`), one per entry in `SolutionService.Projects`; no solution wrapper node visible.
- [ ] Double-tapping a top-level project node still triggers `EnsureExpanded` (existing behavior preserved).
- [ ] Opening a second solution replaces prior project nodes; no leftover nodes from the previous solution.

## Implementation

### 1. Flatten solution load output
`SolutionService.LoadAsync`: drop the wrapping `root` `TreeNode`. Build project nodes as before (same `PopulateProjectFiles` call) and return `IReadOnlyList<TreeNode>` of project nodes. `Projects` (the `ProjectEntry` list) unchanged.

### 2. Bind projects directly in the VM
`MainWindowViewModel.OpenSolutionAsync`: replace `Tree.Clear(); Tree.Add(root);` with `Tree.Clear();` then add each project node from the new `LoadAsync` return. Preserve existing status + startup default logic.

### 3. Expose solution display name
Add `[ObservableProperty] private string? _solutionName;` on `MainWindowViewModel`. Set in `OpenSolutionAsync` from `Solution.SolutionPath` using `Path.GetFileNameWithoutExtension` (default per Open Decision #1). Null before first load — binding renders empty.

### 4. Rebuild row 1 layout
`Views/MainWindow.axaml` row 1: swap the current `DockPanel` for a `Grid ColumnDefinitions="Auto,*,Auto"` (default per Open Decision #3). Column 0: `TextBlock` bound to `SolutionName`, vertically centered, existing left margin preserved. Column 2: `StackPanel Orientation="Horizontal"` containing the existing ComboBox (bound to `Projects` / `StartupProject`), Play, Stop — same bindings, same margins between them. Drop the `Startup:` label and the trailing filler `TextBlock`. Keep the outer `Background="#1E1E22"` and `Margin="4"`.

### 5. Verify explorer template still works
No XAML change needed for the `TreeView` itself — top-level items are already rendered by the same `TreeDataTemplate`. Confirm project nodes render (name only, no icon) as before.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds.
- [ ] Launch (`scripts/run.ps1`) with no solution loaded — left zone empty, right-side controls (empty ComboBox, Play, Stop) flush-right.
- [ ] Open `MiniIde.slnx` — left zone shows `MiniIde` (no extension per default); ComboBox populates with `exe MiniIde` etc.
- [ ] Explorer shows project nodes as top-level items — no `MiniIde.slnx` wrapper above them.
- [ ] Double-tap a project node — its files appear (existing lazy-populate).
- [ ] Open a second, different solution — left-zone name updates, project list swaps, no stale project nodes remain in the tree.
- [ ] Play / Stop still work against the selected startup project.
