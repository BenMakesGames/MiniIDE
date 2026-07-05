# Open Solution File on Solution-Name Double-Click

## Context
**Current behavior**: The solution name (`SolutionName` bound `TextBlock` in row 1, column 0 of the top toolbar) is a static label. Double-clicking it does nothing. The only way to view the underlying `.sln` / `.slnx` file is to locate it on disk manually.

**New behavior**: Double-clicking the solution-name `TextBlock` opens the underlying solution file (`Solution.SolutionPath`) as a new editor tab — same pathway used by tree-node double-clicks. `.slnx` files render with XML highlighting via the existing `HighlightMode.Xml` mapping; `.sln` files render as plain text (no highlighter for that extension — fine).

## Prerequisites
- None. Builds on `2026-07-04 top-bar-and-explorer-flatten.md` (solution name binding already exists) and `2026-07-05 xml-json-syntax-highlighting.md` (`.slnx` → XML highlight already wired via `HighlightModeExtensions.FromExtension`).

## Scope
### In scope
- `Views/MainWindow.axaml`: attach a `DoubleTapped` handler on the solution-name `TextBlock`.
- `Views/MainWindow.axaml.cs`: handler that calls `Vm.OpenFileAsync(Vm.Solution.SolutionPath)` when the path is non-null.

### Out of scope
- Any hover/cursor affordance to signal the label is clickable (see Open Decisions).
- Read-only enforcement or special editing behavior for the solution file — it opens like any other file.
- Extending `HighlightModeExtensions` to add a `.sln`-specific highlighter.
- Menu/keyboard shortcut for the same action.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` row 1 grid, column 0 `TextBlock` bound to `SolutionName`.
  - `Views/MainWindow.axaml.cs` `OnTreeDoubleTapped` — mirror shape (guard, call `Vm.OpenFileAsync`).
  - `MainWindowViewModel.OpenFileAsync` — reuses existing tab if one already exists for the path.
  - `SolutionService.SolutionPath` (nullable `string?`, set in `LoadAsync`).
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 top-bar-and-explorer-flatten.md` — introduced `SolutionName` binding + Grid layout for row 1.
  - `docs/tickets/complete/2026-07-05 xml-json-syntax-highlighting.md` — `.slnx` extension already routes to `HighlightMode.Xml`.

## Constraints & Gotchas
- Avalonia hit-testing: a `TextBlock` with no `Background` set can pass pointer events through empty pixels around the glyphs. Set `Background="Transparent"` on the solution-name `TextBlock` so `DoubleTapped` fires reliably across the whole label bounds, not only over ink.
- `Solution.SolutionPath` is `string?`. Handler must no-op when null (pre-solution state) — otherwise `OpenFileAsync` will throw at `File.ReadAllText`.
- `OpenFileAsync` de-duplicates by path (case-insensitive) — clicking the name twice reactivates the existing tab rather than opening a duplicate. No extra guarding needed in the handler.

## Open Decisions
1. **Cursor / hover affordance** — no visual cue vs. `Cursor="Hand"` on the `TextBlock`. Default: no cursor change (keep the label visually flat; user knows the interaction). Add `Cursor="Hand"` only if manual testing feels ambiguous.
2. **`Tapped` with click-count check vs. `DoubleTapped` event** — Avalonia exposes both. Default: `DoubleTapped` — matches the tree-view handler already in `MainWindow.axaml.cs` and reads clearer.

## Acceptance Criteria
- [ ] With a solution loaded, double-clicking the solution-name `TextBlock` in the top toolbar opens a new editor tab whose file path equals `Solution.SolutionPath`.
- [ ] The opened tab's header shows the solution filename with extension (e.g., `MiniIde.slnx`).
- [ ] `.slnx` content in the opened tab renders with XML syntax highlighting (existing `HighlightMode.Xml` mapping).
- [ ] With no solution loaded (`SolutionPath == null`), double-clicking the empty label area is a no-op — no exception, no tab created.
- [ ] Double-clicking the solution name a second time reactivates the existing tab rather than creating a duplicate (existing `OpenFileAsync` de-dup behavior preserved).

## Implementation

### 1. Make the label hit-testable across its full bounds
`Views/MainWindow.axaml`, row 1 grid column 0 `TextBlock`: add `Background="Transparent"` alongside the existing `Text`, `VerticalAlignment`, `FontSize` attributes. Ensures Avalonia treats the whole `TextBlock` rectangle as a hit target for pointer events, not just the glyphs.

### 2. Wire the `DoubleTapped` handler in XAML
Same `TextBlock`: add `DoubleTapped="OnSolutionNameDoubleTapped"`. Mirrors the naming pattern used by `OnTreeDoubleTapped` on `SolutionTree`.

### 3. Implement the handler
`Views/MainWindow.axaml.cs`: add an `async void OnSolutionNameDoubleTapped(object? sender, TappedEventArgs e)` method. Body: read `Vm.Solution.SolutionPath` into a local; if null, return; otherwise `await Vm.OpenFileAsync(path)`. No `e.Handled = true` needed — no ancestor listens for this event on that TextBlock.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds.
- [ ] Launch (`scripts/run.ps1`) with no solution loaded — double-click the empty left zone of the top bar: nothing happens, no exception in the output panel or debugger.
- [ ] Open `MiniIde.slnx`. Solution name `MiniIde` shows in the top-left. Double-click it — a new tab titled `MiniIde.slnx` opens with the file contents; XML highlighting is visible on element tags.
- [ ] Double-click the name a second time — the same tab reactivates; no duplicate tab appears.
- [ ] Repeat with a `.sln` file (if available) — tab opens as plain text; no exception.
- [ ] Regression: tree-view double-click on a project node still expands; on a file node still opens the file (`OnTreeDoubleTapped` untouched).
- [ ] Regression: Play / Stop buttons in the top-right still work; the added handler does not interfere with sibling controls.
