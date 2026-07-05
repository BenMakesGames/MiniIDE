# Solution-Name Context Menu (Path Actions)

## Context
**Current behavior**: Right-clicking the solution-name `TextBlock` in the top bar (row 1, directly beneath the main menu) does nothing. The same three path actions available on every tree node — **Open in Explorer**, **Copy absolute path**, **Copy relative path** — have no equivalent for the solution file itself; the label only responds to double-click (opens the `.slnx`/`.sln` in an editor tab).

**New behavior**: Right-clicking the solution name opens the same three-item context menu that tree nodes use, targeting the loaded solution file (`Solution.SolutionPath`). Open in Explorer reveals-and-selects the `.slnx`/`.sln`; Copy absolute yields its full path; Copy relative yields the bare filename (relative to the solution's own directory).

## Prerequisites
None. Builds on `2026-07-05 file-path-context-menu.md` (the shared three-item menu + handlers) and `2026-07-04 top-bar-and-explorer-flatten.md` (the solution-name `TextBlock`).

## Scope
### In scope
- `Views/MainWindow.axaml`: attach a `ContextMenu` to the solution-name `TextBlock` with the same three `MenuItem`s and `Click` handlers the tree template already uses.
- `Views/MainWindow.axaml.cs`: extend the `GetTargetPath` resolver to handle the solution-name case (its `DataContext` is the `MainWindowViewModel`, not a `TreeNode`).

### Out of scope
- Any solution-scoped actions beyond the three path items (build all, reload/close solution, set startup) — those are surfaced elsewhere (top-bar ComboBox + Play/Stop) and are not part of "same options as a project."
- New handlers or new commands — reuse the three existing `OnCtx*` handlers verbatim.
- Changing the tree or tab context menus, or the existing double-tap-to-open behavior on the solution name.
- Extracting the now-triplicated inline `ContextMenu` into a shared resource (see Open Decisions).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — the shared `ContextMenu` inside `TreeDataTemplate DataType="m:TreeNode"` (three `MenuItem`s wired to `OnCtxOpenInExplorerClick` / `OnCtxCopyAbsolutePathClick` / `OnCtxCopyRelativePathClick`); the solution-name `TextBlock Text="{Binding SolutionName}"` in the row 1 `Grid` (column 0), which already has `Background="Transparent"` and `DoubleTapped="OnSolutionNameDoubleTapped"`.
  - `Views/MainWindow.axaml.cs` — `GetTargetPath(object?)` resolver (the `switch` over `TreeNode` / `TabViewModelBase`); the three `OnCtx*` handlers that consume it; the `OnCtxOpenInExplorerClick` folder-vs-file branch (`ctx is TreeNode { Kind: NodeKind.Folder }`).
  - `Services/SolutionService.cs` — `SolutionPath` (absolute path to the loaded `.slnx`/`.sln`).
  - `ViewModels/MainWindowViewModel.cs` — `Solution` accessor (used by handlers as `Vm.Solution.SolutionPath`).
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 file-path-context-menu.md` — established the three-item menu, the `GetTargetPath` resolver, the Explorer `/select,` launch, and the clipboard/status plumbing. Read its Learnings (DataContext inheritance in embedded menus; `IClipboard.SetTextAsync` is an extension method).
  - `docs/tickets/complete/2026-07-04 top-bar-and-explorer-flatten.md` — moved the solution root out of the tree into the top-bar label; documents why the solution name is a bare `TextBlock`, not a `TreeNode`.

## Constraints & Gotchas
- **Silent-no-op trap.** The solution-name `TextBlock` inherits the window's `DataContext` (`MainWindowViewModel`). If the menu is attached without extending `GetTargetPath`, every `MenuItem.DataContext` is the VM, `GetTargetPath` falls through to `null`, and all three actions bail silently. The `GetTargetPath` arm is the load-bearing change — do not skip it.
- **Reveal-and-select, not folder.** The solution file is a file, so `OnCtxOpenInExplorerClick` must take its `/select,` branch, not the folder branch. That branch keys off `ctx is TreeNode { Kind: NodeKind.Folder }`; since the solution-name `ctx` is the VM (not a `TreeNode`), `isFolder` is naturally `false` — no change needed there once `GetTargetPath` returns the path.
- **No-solution state is safe by construction.** Before a solution loads, `SolutionName` is empty (near-zero-width label, effectively un-right-clickable) and `SolutionPath` is `null`, so `GetTargetPath` returns `null` and handlers no-op. No explicit guard required.
- **Relative path = bare filename.** `OnCtxCopyRelativePathClick` anchors to `Path.GetDirectoryName(SolutionPath)`, which for the solution file is its own directory — so the relative result is just `MiniIde.slnx`. Expected and consistent with the existing math; do not special-case it.
- **Third inline copy.** This makes a third literal copy of the same `<ContextMenu>` block (tree, tabs, now solution name). Consistent with the codebase's existing choice (file-path ticket rejected a shared resource). See Open Decisions if the duplication is to be addressed instead.

## Open Decisions
1. **Inline copy vs. shared resource** — a third inline `ContextMenu` (consistent with tree + tab templates) vs. finally hoisting it to a shared `ContextMenu` `StaticResource`. Default: third inline copy — matches the established pattern and keeps per-instance `DataContext` reasoning simple. Extract only if it reads as clearly cleaner in place.
2. **`GetTargetPath` VM arm vs. a dedicated handler set** — add a `MainWindowViewModel vm => vm.Solution.SolutionPath` arm to the shared resolver (reuses all three handlers) vs. writing solution-specific handlers. Default: the single resolver arm — smallest change, keeps one code path.

## Acceptance Criteria
- [ ] With a solution loaded, right-clicking the solution-name `TextBlock` opens a `ContextMenu` with exactly three items in order: Open in Explorer, Copy absolute path, Copy relative path.
- [ ] **Open in Explorer** launches `explorer.exe` with `/select,<SolutionPath>` so Explorer opens the solution's folder with the `.slnx`/`.sln` pre-selected (reveal-and-select, not folder-open).
- [ ] **Copy absolute path** places `Solution.SolutionPath` (full absolute path) on the clipboard and reports it via the status bar.
- [ ] **Copy relative path** places the solution filename (e.g. `MiniIde.slnx`) on the clipboard — the result of `Path.GetRelativePath` against the solution's own directory.
- [ ] All three actions route through the existing `OnCtx*` handlers with no new handler methods added.
- [ ] With no solution loaded (`SolutionPath == null`), none of the three actions throw and none produce an Explorer launch or clipboard write.

## Implementation

### 1. Resolve the solution path for the VM `DataContext`
In `Views/MainWindow.axaml.cs`, extend `GetTargetPath(object?)` with an arm for the solution-name case: when the `DataContext` is a `MainWindowViewModel`, return its `Solution.SolutionPath` (nullable — a `null` solution path stays `null`, preserving the existing bail-on-null behavior in every handler). Place it alongside the existing `TreeNode` / `TabViewModelBase` arms. No other handler logic changes — the folder-vs-file branch in `OnCtxOpenInExplorerClick` already resolves to file (select) for a non-`TreeNode` context.

### 2. Attach the context menu to the solution-name label
In `Views/MainWindow.axaml`, add a `TextBlock.ContextMenu` to the row 1 solution-name `TextBlock` (the one bound to `SolutionName` with `DoubleTapped="OnSolutionNameDoubleTapped"`). Mirror the exact `ContextMenu` block from the `TreeDataTemplate` — same three `MenuItem` headers (`_Open in Explorer`, `Copy _absolute path`, `Copy _relative path`) wired to the same `OnCtx*` `Click` handlers. The `TextBlock` already carries `Background="Transparent"`, so it is fully hit-testable; no layout change needed.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1` with no solution loaded — the top-left zone is empty; nothing is right-clickable there and no exception occurs.
- [ ] Open `MiniIde.slnx`. Right-click the solution name (`MiniIde`) — a three-item menu appears: Open in Explorer, Copy absolute path, Copy relative path.
- [ ] Choose **Open in Explorer** — Explorer opens the solution folder with `MiniIde.slnx` pre-selected.
- [ ] Choose **Copy absolute path** — clipboard holds the full path ending in `MiniIde.slnx`; status bar shows `Copied <path>`.
- [ ] Choose **Copy relative path** — clipboard holds `MiniIde.slnx` (bare filename, no separator, no `.\`).
- [ ] Regression: right-click a project node and a file node in the tree — the same three actions still behave as before (project reveals its `.csproj`; file reveals the file).
- [ ] Regression: double-clicking the solution name still opens the solution file in an editor tab (`OnSolutionNameDoubleTapped` untouched).
- [ ] No exceptions appear in the Output pane throughout.
