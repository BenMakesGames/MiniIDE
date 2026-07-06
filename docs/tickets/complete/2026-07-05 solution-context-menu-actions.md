# Solution Context Menu: Open New Solution, Open with Claude Code; Remove File Menu

## Context
**Current behavior**: The solution-name `TextBlock` (row 1, top bar) has a three-item context menu — Open in Explorer / Copy absolute path / Copy relative path — all scoped to the loaded `.slnx`/`.sln`. Opening a solution and Save/Exit live only on the top `_File` menu. Before a solution loads, `SolutionName` is `null`, so the label renders empty (near-zero width, effectively un-right-clickable).

**New behavior**: The solution context menu gains two solution-scoped actions — **Open new solution...** (the existing file-picker flow) and **Open with Claude Code** (spawns a terminal running `claude` in the solution's directory). When no solution is loaded the label shows `<no solution>` instead of empty, making the menu reachable at startup so a solution can be opened from it. The top `_File` menu is removed; its Open-Solution action now lives on this context menu (and Ctrl+O), Save remains on Ctrl+S, and Exit is dropped in favor of the window close button. The `_Navigate` menu is untouched (its own pending ticket removes it).

## Prerequisites
None. Builds on `docs/tickets/complete/2026-07-05 solution-name-context-menu.md` (the solution-name context menu + `GetTargetPath` VM arm).

## Scope
### In scope
- `Views/MainWindow.axaml`: add two `MenuItem`s to the solution-name `TextBlock`'s existing `ContextMenu`; remove the `_File` `MenuItem` from the top `Menu`; change the solution-name label to show `<no solution>` when unset.
- `Views/MainWindow.axaml.cs`: new `OnCtxOpenWithClaudeClick` handler (terminal launch with wt→PowerShell fallback); reuse the existing `OnOpenSolutionClick` for the new menu item; delete the now-dead `OnSaveClick` and `OnExitClick` handlers.
- `ViewModels/MainWindowViewModel.cs` **or** the XAML binding: default `SolutionName` presentation to `<no solution>` (see Open Decisions).

### Out of scope
- The `_Navigate` menu and its handlers (`OnGoToDefClick` / `OnFindRefsClick`) — owned by `docs/tickets/remove-navigate-menu.md`.
- Adding the two new actions to the tree-node or tab-header context menus — they stay three-item, solution-scoped actions belong only on the solution-name menu.
- Any "close solution" / reset-to-`<no solution>` flow — once loaded, the label never returns to the placeholder in this ticket.
- Removing the entire `<Menu>` element — the menu bar still holds `_Navigate` until its own ticket lands (see Constraints).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — the top `<Menu Grid.Row="0">` with the `_File` `MenuItem` (children: `_Open Solution...` → `OnOpenSolutionClick`, `_Save` → `OnSaveClick`, `E_xit` → `OnExitClick`); the solution-name `TextBlock Text="{Binding SolutionName}"` and its inline `ContextMenu` (three `OnCtx*` items).
  - `Views/MainWindow.axaml.cs` — `OnOpenSolutionClick` (reuse; keep), `OnSaveClick` / `OnExitClick` (delete), `OnCtxOpenInExplorerClick` (mirror its `ProcessStartInfo` + try/catch + `Vm.Status` error idiom), `OpenSolutionDialogAsync`, `Vm.Solution.SolutionPath`.
  - `Services/RunService.cs` / `Services/NuGetService.cs` — `ProcessStartInfo` with `WorkingDirectory = Path.GetDirectoryName(...)` idiom.
  - `ViewModels/MainWindowViewModel.cs` — `[ObservableProperty] private string? _solutionName;` (decl) and its only assignment in `OpenSolutionAsync`.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 solution-name-context-menu.md` — the three-item menu, `GetTargetPath` VM arm, clipboard/status plumbing, and the DataContext-inheritance gotcha (the menu's `DataContext` is `MainWindowViewModel`).
  - `docs/tickets/remove-navigate-menu.md` — pending; removes the `_Navigate` menu. **Its text currently states "The menu bar retains only `_File`" — that assertion goes stale once this ticket lands.** Coordinate: after both tickets, the `<Menu>` is empty and should be removed by whichever lands second.

## Constraints & Gotchas
- **Menu bar not emptied here.** This ticket removes only `_File`; `_Navigate` remains, so the `<Menu>` still has a child and must not be deleted. Removing the empty `<Menu>` element is the job of whichever of this / `remove-navigate-menu` lands last.
- **Context-menu DataContext is the VM.** The solution-name menu's items inherit `MainWindowViewModel` as `DataContext` (per the prior ticket). `OnCtxOpenWithClaudeClick` can read `Vm.Solution.SolutionPath` directly; it does not need `GetTargetPath`.
- **Terminal launch needs a visible window.** Use `UseShellExecute = true` so the spawned terminal appears (unlike `RunService`, which pipes output with `UseShellExecute = false`). `wt.exe` is a Windows app-execution alias and may be absent; a failed `Process.Start` throws `Win32Exception`.
- **Remember wt absence for the process lifetime.** Per decision: attempt `wt.exe` first; on launch failure, set a flag so subsequent invocations skip straight to PowerShell. The flag lives on the `MainWindow` instance (app-lifetime) — it is fine for it to reset between app runs.
- **No-solution safety.** With `SolutionPath == null`, Open with Claude Code and the three path items must no-op (no throw, no window). Open new solution... must still work (it is the intended startup entry point).
- **Keep `OnOpenSolutionClick`.** It moves from the File menu to the context menu — do not delete it. Only `OnSaveClick` / `OnExitClick` become dead; grep to confirm no other references before deleting.

## Open Decisions
1. **Placeholder mechanism** — field default (`private string? _solutionName = "<no solution>";`) vs. XAML `TargetNullValue`/`FallbackValue` on the binding (needs `&lt;`/`&gt;` escaping). Default: field default — the VM owns the display string, no XAML escaping, and `GetTargetPath` still reads the null `SolutionPath` so path actions stay guarded.
2. **New-item placement** — where the two new items sit relative to the three path items, and whether a `Separator` divides them. Default: two new solution actions at the top, then a `Separator`, then the three existing path items (solution-scoped actions read as primary). Implementer's call.
3. **Claude menu label & mnemonic** — e.g. `Open with _Claude Code`. Default as written; pick a non-colliding mnemonic within the menu.
4. **wt-unavailable flag scope** — instance field on `MainWindow` vs. `static`. Default: instance field (MainWindow is effectively a singleton). Either satisfies "remember for the app's lifetime."

## Acceptance Criteria
- [ ] The top menu bar has no `_File` menu; the `_Navigate` menu is still present and unchanged.
- [ ] `OnSaveClick` and `OnExitClick` no longer exist in `Views/MainWindow.axaml.cs`; `OnOpenSolutionClick` still exists and is wired to the new **Open new solution...** context-menu item. The project builds.
- [ ] Ctrl+O still opens the solution file picker and Ctrl+S still saves the active tab (both via `OnGlobalKeyDown`, unchanged).
- [ ] With no solution loaded, the solution-name label reads `<no solution>` and its context menu is openable; choosing **Open new solution...** launches the file picker.
- [ ] The solution context menu contains, in addition to the three existing path items: **Open new solution...** and **Open with Claude Code**.
- [ ] **Open with Claude Code** with a solution loaded spawns a terminal whose working directory is the solution's folder (`Path.GetDirectoryName(SolutionPath)`) running `claude`; with no solution loaded it does nothing (no window, no exception).
- [ ] When `wt.exe` cannot be launched, **Open with Claude Code** falls back to PowerShell; after the first failure it does not re-attempt `wt.exe` for the remainder of the app session.

## Implementation

### 1. Show `<no solution>` when unset
Give the solution-name presentation a placeholder so the label (and thus its context menu) is visible at startup. Per Open Decision 1, default to a field initializer on `MainWindowViewModel._solutionName` (`= "<no solution>"`); `OpenSolutionAsync` continues to overwrite it with the real name on load. Confirm `SolutionName`'s only writer is `OpenSolutionAsync` so the placeholder is never clobbered except by a real load.

### 2. Remove the File menu
In `Views/MainWindow.axaml`, delete the entire `<MenuItem Header="_File">…</MenuItem>` block from the top `<Menu>`. Leave the `_Navigate` `MenuItem` and the `<Menu>` element itself in place (see Constraints).

### 3. Delete the dead click handlers
In `Views/MainWindow.axaml.cs`, remove `OnSaveClick` and `OnExitClick` (their only callers were the File menu items). Keep `OnOpenSolutionClick`, `OpenSolutionDialogAsync`, and `SaveActiveAsync` — Ctrl+O/Ctrl+S and the new menu item still use them. Grep first to confirm no stray references.

### 4. Add the two new context-menu items
In `Views/MainWindow.axaml`, extend the solution-name `TextBlock`'s inline `ContextMenu` (the one whose three items call `OnCtx*`). Add an **Open new solution...** `MenuItem` wired to the existing `OnOpenSolutionClick`, and an **Open with Claude Code** `MenuItem` wired to a new `OnCtxOpenWithClaudeClick`. Arrange per Open Decision 2 (default: new items first, then a `Separator`, then the three path items).

### 5. Implement `OnCtxOpenWithClaudeClick` with wt→PowerShell fallback
Add the handler to `Views/MainWindow.axaml.cs`, mirroring `OnCtxOpenInExplorerClick`'s `ProcessStartInfo` + try/catch + `Vm.Status` error reporting. Resolve the solution directory from `Vm.Solution.SolutionPath` via `Path.GetDirectoryName`; if `SolutionPath` is `null`, no-op (optionally set `Vm.Status = "No solution open"`). Otherwise:
- Unless the wt-unavailable flag is set, attempt to launch **Windows Terminal**: `FileName = "wt.exe"`, `UseShellExecute = true`, arguments `-d <dir> claude` (via `ArgumentList`: `-d`, dir, `claude`). If `Process.Start` throws (e.g. `Win32Exception` — wt not installed), set the wt-unavailable flag (Open Decision 4) and fall through to PowerShell.
- **PowerShell fallback**: `FileName = "powershell.exe"`, `WorkingDirectory = <dir>`, `UseShellExecute = true`, arguments `-NoExit -Command claude` (via `ArgumentList`). Wrap in its own try/catch → `Vm.Status` on failure.

Only a launch failure of `wt.exe` itself triggers the fallback and flag — do not treat `claude` failing inside a successfully-spawned terminal as a wt problem.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1` with no solution — the label reads `<no solution>`. Right-click it → the context menu opens; choose **Open new solution...** → the file picker appears. Open `MiniIde.slnx`.
- [ ] The menu bar shows no `File` menu; `Navigate` is still present (until its own ticket). Press Ctrl+O → picker opens; Ctrl+S on a dirty tab → it saves.
- [ ] With `MiniIde.slnx` loaded, right-click the solution name → the menu lists Open new solution..., Open with Claude Code, and the three path items (Open in Explorer / Copy absolute / Copy relative), all still functional.
- [ ] Choose **Open with Claude Code** → a Windows Terminal window opens in the solution folder running `claude` (verify the CWD, e.g. `pwd`). On a machine without `wt.exe`, a PowerShell window opens in the solution folder running `claude` instead, and a second invocation goes straight to PowerShell.
- [ ] Regression: double-clicking the solution name still opens the `.slnx` in an editor tab; the tree and tab context menus still show their three path items.
- [ ] No exceptions in the Output pane throughout; **Open with Claude Code** while no solution is loaded does nothing and does not throw.

## Learnings

### Architectural decisions
- **Open Decision 1 (placeholder mechanism)** — used a field initializer `private string? _solutionName = "<no solution>";` on `MainWindowViewModel`. Confirmed `SolutionName`'s only writer is `OpenSolutionAsync` (`SolutionName = Path.GetFileNameWithoutExtension(path)`), so the placeholder is only ever clobbered by a real load. `SolutionPath` stays `null` until load, so path/Claude actions remain guarded independently of the display string — no XAML `TargetNullValue`/escaping needed.
- **Open Decision 2 (item placement)** — two new solution-scoped actions at the top (**Open new solution...**, **Open with _Claude Code**), a `Separator`, then the three existing path items. Solution-scoped actions read as primary.
- **Open Decision 3 (Claude label/mnemonic)** — `Open with _Claude Code` (mnemonic `C`). Within-menu mnemonics are all distinct: n / C / O / a / r. The Open-Solution item was reworded to `Open _new solution...` (mnemonic `n`) to avoid colliding with `_Open in Explorer`'s `O`.
- **Open Decision 4 (wt flag scope)** — instance field `_wtUnavailable` on `MainWindow` (effectively a singleton). Resets between app runs, which the ticket accepts.

### Implementation notes
- `OnCtxOpenWithClaudeClick` reads `Vm.Solution.SolutionPath` **directly** (not via `GetTargetPath`) — the menu item's `DataContext` is the `MainWindowViewModel`, and the ticket only needs the solution path, not the polymorphic tree/tab resolution `GetTargetPath` provides.
- **wt→PowerShell fallback**: `wt.exe` launched with `UseShellExecute = true` and `ArgumentList` `-d <dir> claude`. A `Process.Start` throw (wt is an app-execution alias that may be absent → `Win32Exception`) sets `_wtUnavailable` and falls through to `powershell.exe -NoExit -Command claude` with `WorkingDirectory = <dir>`. Only a *launch* failure of wt trips the flag — `claude` failing inside a spawned terminal is not a wt problem and is never observed by this handler.
- `UseShellExecute = true` is required here (unlike `RunService`, which uses `false` to pipe output) so the terminal window is actually visible.
- The `_File` menu removal left the top `<Menu>` holding only `_Navigate`, so the `<Menu>` element stays. Removing the empty `<Menu>` is now owned by `remove-navigate-menu.md` (updated in-place: its stale "retains only `_File`" assertions were corrected to "remove the empty `<Menu>`").

### Verification
- Isolated `dotnet build` (`-o` to a temp dir) **succeeded** — only the pre-existing `CS0618` (`Workspace.WorkspaceFailed` obsolete) warning; no new warnings, no compile/XAML errors. A normal in-place build fails only at the file-copy step because a running MiniIde instance locks the output DLL — not a compilation error.
- Acceptance criteria confirmed by code inspection. The manual GUI Test Plan items (menu contents, terminal launch + CWD, wt-absent fallback, no-solution safety, regressions) require driving the running app and are left for manual verification.

### Related areas affected
- `docs/tickets/remove-navigate-menu.md` — updated to reflect that `_File` is gone and that removing the empty `<Menu>` element is now its responsibility (whichever of the two tickets landed last owns it; this one landed first).

### Post-feedback
- **Disable, don't silently no-op.** Original design had the four solution-scoped items no-op when `SolutionPath == null`. User feedback: they should be *visibly disabled* — only **Open new solution...** stays live at startup. Added `OnSolutionCtxOpening` (`ContextMenu.Opening`, synchronous — cheap `SolutionPath` null check only) that sets `IsEnabled = SolutionPath is not null` on every `MenuItem` except the one named `SolutionCtxOpenNew`. Mirrors the `OnCodeCtxOpening` idiom (resolve items from `sender`'s `Items`, key off `mi.Name`). The `Separator` is not a `MenuItem`, so it's skipped naturally. The per-handler `SolutionPath == null` guards stay as defense-in-depth.
