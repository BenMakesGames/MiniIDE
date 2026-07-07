# Activate Output Panel on Run

## Context
**Current behavior**: Clicking the "▶ Run" button (or pressing F5) invokes `PlayCommand`, which clears the Output panel and streams build/run output into it. But the bottom `TabControl` keeps whatever tab was already selected — if the user was on the Find or NuGet tab, output streams into a tab they can't see, with no cue to switch.

**New behavior**: Triggering a run selects the Output tab in the bottom `TabControl` so streamed output is immediately visible. The bottom panel is already visible (fixed 220px row); this only changes which tab is active, bringing Output to the foreground.

## Scope
### In scope
- Name the Output `TabItem` in `MainWindow.axaml` so code-behind can select it.
- A view-side helper that selects the Output tab, invoked when a run is triggered from the Run button.

### Out of scope
- Expanding/showing the bottom panel if it were collapsed — it isn't collapsible today (fixed row height).
- Changing `PlayCommand` / `PlayAsync` behavior or the Output streaming/tail-follow logic.
- Any Stop-button behavior.

## Relevant Docs & Anchors
- `src/MiniIde/Views/MainWindow.axaml.cs` — `FocusFind()` is the exemplar: it resolves the bottom `TabControl` by name (`this.FindControl<TabControl>("BottomTabs")`), finds a named `TabItem`, and sets `tabs.SelectedItem`. The new helper mirrors this, minus the focus/select-all dispatch (Output has no input box to focus).
- `src/MiniIde/Views/MainWindow.axaml` — the Run `Button` (`Content="▶ Run" Command="{Binding PlayCommand}"`) in the top-bar `StackPanel`; the bottom `TabControl x:Name="BottomTabs"` with its `Find`, `Output`, and `NuGet` `TabItem`s. The `Find` tab already carries `x:Name="FindTab"`; the Output tab is currently unnamed.
- `src/MiniIde/ViewModels/MainWindowViewModel.cs` — `PlayCommand` / `PlayAsync` (the run entry point) and `OnGlobalKeyDown`'s F5 branch in the code-behind, which also invokes `PlayCommand`.

## Constraints & Gotchas
- The Run `Button` uses `Command="{Binding PlayCommand}"`. Adding a `Click` handler alongside the existing `Command` binding is fine in Avalonia — both fire on click, and `Click` won't fire while the button is disabled (`CanPlay` false), so the tab won't switch when there's nothing to run.
- Tab selection is a view concern — keep it in the code-behind (as `FocusFind` does). Do not push tab-selection state into the view model.

## Open Decisions
1. **Cover the F5 shortcut too** — F5 routes through the same `PlayCommand` via `OnGlobalKeyDown`, so a user pressing F5 has the same "where did my output go" problem as clicking Run. Default: activate the Output tab from both the Run-button click and the F5 branch, ideally by having both call the shared helper so the behavior can't drift. If the implementer finds the click-only path cleaner, click-only still satisfies the ticket's literal ask.
2. **Helper naming** — e.g. `ShowOutput()` / `SelectOutputTab()`, mirroring `FocusFind`. Implementer's call.

## Acceptance Criteria
- [ ] With the bottom panel showing a non-Output tab (Find or NuGet), clicking "▶ Run" on a runnable startup project switches the bottom `TabControl` to the Output tab.
- [ ] The Output `TabItem` in `MainWindow.axaml` carries an `x:Name` referenced by the code-behind tab-selection logic.
- [ ] When the Run button is disabled (no runnable startup project), clicking it neither runs nor changes the selected bottom tab.
- [ ] Streamed build/run output appears in the now-active Output tab exactly as before (no regression to `PlayAsync` / tail-follow).

## Implementation

### 1. Name the Output tab
In `MainWindow.axaml`, add an `x:Name` (e.g. `OutputTab`) to the `<TabItem Header="Output">` inside the `BottomTabs` `TabControl`, matching how `FindTab` is named.

### 2. Add a select-Output helper in code-behind
In `MainWindow.axaml.cs`, add a helper mirroring `FocusFind` but simpler: resolve `BottomTabs` and the named Output `TabItem` via `this.FindControl<...>(...)`, and set `tabs.SelectedItem = outputTab` when both resolve. No `Dispatcher.UIThread.Post` / focus / select-all block — Output has no input to focus.

### 3. Invoke on run from the Run button
Add a `Click` handler to the Run `Button` (keeping the existing `Command="{Binding PlayCommand}"` binding) whose handler calls the step-2 helper. Because `Click` doesn't fire on a disabled button, this naturally no-ops when there's nothing to run.

### 4. (Per Open Decision #1) Invoke on F5
In `OnGlobalKeyDown`'s F5 branch, after confirming `PlayCommand.CanExecute`, also call the step-2 helper so keyboard-triggered runs surface Output too. Prefer routing both entry points through the one helper.

## Test Plan
- [ ] Build passes: `dotnet build src/MiniIde/MiniIde.csproj`.
- [ ] Open a solution with a runnable startup project. Click the NuGet tab in the bottom panel, then click "▶ Run" — the bottom panel switches to Output and shows streaming build/run lines.
- [ ] Repeat with the Find tab selected before clicking Run — confirm it switches to Output.
- [ ] Select a non-runnable startup project (Run button disabled). Click where the Run button is — nothing runs and the selected bottom tab does not change.
- [ ] (If F5 covered) Select the NuGet tab, press F5 on a runnable project — the bottom panel switches to Output.
- [ ] Regression: with Output already the active tab, click Run — output streams normally, tail-follow still pins to bottom.

## Learnings

- **Open Decision #1 (F5) — covered.** Both entry points route through one shared helper (`ShowOutput()`): the Run button's `Click="OnRunClick"` and the F5 branch in `OnGlobalKeyDown`. This keeps the two paths from drifting, as the ticket preferred.
- **Open Decision #2 (naming) — `ShowOutput()`.** Chosen over `SelectOutputTab()` to describe intent (surface the panel) rather than mechanism.
- **F5 helper placement.** `ShowOutput()` is called *inside* the existing `CanExecute` guard (`if (Vm.PlayCommand.CanExecute(null)) { ShowOutput(); await ... }`), matching the button's disabled-no-op behavior so a non-runnable project never switches tabs from either path.
- **`Click` + `Command` coexist.** As the ticket noted, Avalonia fires both `Click` and the bound `Command` on a click, and raises neither on a disabled button — so `OnRunClick` needs no `CanPlay` re-check; the disabled state gates it for free.
- **Helper mirrors `FocusFind` minus the dispatch.** `ShowOutput()` resolves `BottomTabs` + `OutputTab` via `FindControl` and sets `SelectedItem`, dropping `FocusFind`'s `Dispatcher.UIThread.Post` focus/select-all block — the Output editor is read-only with no input to focus.
- **Verification limit.** Build compiles the XAML (so the `x:Name` and `Click` handler resolve at compile time), and the tab-switch logic is confirmed by inspection. The interactive Test Plan items (observing the switch on a live run) require a human at the UI and were not automated.
