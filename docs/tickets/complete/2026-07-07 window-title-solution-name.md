# Window Title Reflects Open Solution

## Context
**Current behavior**: The OS window title is the static string `MiniIde`, hard-coded as `Title="MiniIde"` in `MainWindow.axaml`. It never changes when a solution is opened, and the casing (`MiniIde`) doesn't match the product name (`MiniIDE`).

**New behavior**: With no solution open, the window title reads `MiniIDE`. When a solution is open, it reads `<solution name> - MiniIDE` (e.g. `MiniIDE - MiniIDE`, `Contoso.Web - MiniIDE`), where `<solution name>` is the solution filename without extension — the same value already shown in the top-bar `SolutionName` label. The title updates live when a solution loads (and re-loads).

## Scope
### In scope
- A derived window-title value on `MainWindowViewModel`, computed from whether a solution is loaded.
- Binding the `Window.Title` to that value in `MainWindow.axaml`.

### Out of scope
- Any "close solution" flow that returns the app to the no-solution state at runtime — there is no such flow today (opening a second solution launches a new instance; see `LaunchNewInstance` / `docs/tickets/complete/2026-07-05 open-solution-from-command-line-arg.md`). The no-solution title only needs to be correct at startup before any solution loads.
- Changing the top-bar `SolutionName` label text or its `<no solution>` placeholder.
- Showing dirty/unsaved indicators, active file name, or project name in the title.

## Relevant Docs & Anchors
- `src/MiniIde/ViewModels/MainWindowViewModel.cs` — `_solutionName` is an `[ObservableProperty]` (initial value `"<no solution>"`), set to `Path.GetFileNameWithoutExtension(path)` inside `OpenSolutionAsync` right after `Solution` finishes loading. `Solution.SolutionPath` (on `SolutionService`) is non-null once a solution is loaded and is the reliable "is a solution open?" signal.
- `src/MiniIde/Views/MainWindow.axaml` — the `<Window ... Title="MiniIde" ...>` root element. The window already has `x:DataType="vm:MainWindowViewModel"`, so a `{Binding}` on `Title` resolves against the view model.
- CommunityToolkit.Mvvm is already in use (`[ObservableProperty]`); `[NotifyPropertyChangedFor(...)]` is the standard way to raise change notification for a computed property when its backing source property changes — see how other `[ObservableProperty]` fields are declared in this file.

## Constraints & Gotchas
- Prefer deriving "no solution open" from `Solution.SolutionPath is null` rather than string-comparing `SolutionName` against the `"<no solution>"` sentinel — the sentinel is presentation-only and fragile. Within `OpenSolutionAsync`, `Solution.LoadAsync` runs (setting `SolutionPath`) *before* `SolutionName` is assigned, so when the title recomputes off a `SolutionName` change the path is already populated.
- Keep the product name spelled `MiniIDE` (capital I-D-E) in the title, correcting the current `MiniIde`.

## Open Decisions
1. **Computed-property name** — e.g. `WindowTitle` vs `Title`. Default: `WindowTitle` (avoids shadowing the control's own `Title`). Implementer's call.
2. **Where the title constant lives** — inline in the getter vs a `const`. Default: inline `$"{SolutionName} - MiniIDE"` / `"MiniIDE"`; a `const` for the bare product name is fine if it reads cleaner.

## Acceptance Criteria
- [ ] On launch with no solution loaded, the OS window title is exactly `MiniIDE`.
- [ ] After a solution loads, the OS window title is exactly `<solution name> - MiniIDE`, where `<solution name>` equals the top-bar `SolutionName` label.
- [ ] The title updates without restarting the app when a solution is loaded (and when reloaded via the context menu).
- [ ] `MainWindow.axaml` no longer hard-codes `Title="MiniIde"`; the title comes from the view model.

## Implementation

### 1. Add a derived window-title property to the view model
In `MainWindowViewModel`, add a read-only computed property (e.g. `WindowTitle`) returning `"MiniIDE"` when `Solution.SolutionPath is null`, otherwise `$"{SolutionName} - MiniIDE"`. Annotate the existing `_solutionName` `[ObservableProperty]` field with `[NotifyPropertyChangedFor(nameof(WindowTitle))]` so the title re-raises whenever `SolutionName` changes (which happens once per load, after the path is set).

### 2. Bind the window Title
In `MainWindow.axaml`, replace `Title="MiniIde"` on the root `<Window>` with `Title="{Binding WindowTitle}"`.

## Test Plan
- [ ] Build passes: `dotnet build src/MiniIde/MiniIde.csproj`.
- [ ] Launch MiniIDE with no command-line argument — the title bar reads `MiniIDE`.
- [ ] Open a solution (Ctrl+O or double-click the solution-name label) — the title bar updates to `<solution name> - MiniIDE`, matching the top-bar label.
- [ ] Reload the solution via the solution-name context menu (`_Reload solution`) — the title stays `<solution name> - MiniIDE`.
- [ ] Launch with a solution path as a command-line argument — the title shows the solution name at startup.

## Learnings

- **Open Decision 1 (property name)** — went with the default `WindowTitle` to avoid shadowing the control's own `Title`.
- **Open Decision 2 (constant location)** — went with the default inline literals (`"MiniIDE"` / `$"{SolutionName} - MiniIDE"`); the string appears twice but reads clearly, and a `const` would add ceremony without payoff (KISS).
- **Change-notification wiring** — a computed getter (`WindowTitle`) plus `[NotifyPropertyChangedFor(nameof(WindowTitle))]` on the source `[ObservableProperty]` is the idiomatic CommunityToolkit.Mvvm pattern for derived properties. Multiple attributes stack on one field: `[ObservableProperty][NotifyPropertyChangedFor(...)] private ...;`.
- **Ordering gotcha confirmed** — `Solution.LoadAsync` sets `SolutionPath` *before* `SolutionName` is assigned in `OpenSolutionAsync`, so when the `SolutionName` change fires the `WindowTitle` re-raise, `SolutionPath` is already non-null and the title resolves to the loaded-solution branch. Deriving "no solution" from `SolutionPath is null` (not the `"<no solution>"` sentinel) keeps presentation and state concerns separate.
- **Reload** — re-opening the same solution assigns `SolutionName` the same string, so `SetProperty` skips the change notification; harmless because the title is already correct.
- **Verification** — build passed; user ran the app and confirmed the title updates on load. Manual UI test rather than an automated one (no runtime harness for the Avalonia window title, and the logic is a trivial getter).
