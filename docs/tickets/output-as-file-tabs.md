# Output as File-Area Tabs

## Context
**Current behavior**: Build/run output lives in a single shared `OutputViewModel` rendered in one fixed "Output" `TabItem` in the bottom tool panel (`BottomTabs`). That one buffer is written to by two unrelated features: project runs (`MainWindowViewModel.PlayAsync` → `Run.RunAsync(StartupProject, Output.Append)`) and NuGet restores (`NuGetViewModel.ApplyAsync` → `_output.Clear()`/`_output.Append`). A recently-added `ShowOutput()` view helper selects that bottom tab whenever a run starts.

**New behavior**: Output is rendered as tabs in the **main file-area `TabControl`** (the same one that hosts editor and image tabs), not in the bottom panel. Running a project opens — or reuses — a tab titled `<project name> - Output` and activates it; NuGet restore streams into a `NuGet - Output` tab created/reused the same way. The old bottom-panel Output tab and its plumbing are removed; the bottom panel keeps Find and NuGet. Single-run behavior is unchanged (starting any run still kills the prior process), so at most one output tab is "live" at a time while earlier ones remain as readable, finished output. Closing an output tab whose process is still running silently stops that process (no confirmation dialog yet).

## Prerequisites
None.

## Scope
### In scope
- Generalize `TabViewModelBase` off its file-backed assumption: add a namespaced identity key used for dedup/reuse; make `FilePath` optional.
- New `OutputTabViewModel : TabViewModelBase` (an output-buffer tab; `SaveAsync` no-op).
- `MainWindowViewModel`: a get-or-create-output-tab helper keyed by identity; route project-run output and NuGet output to tabs; track which output tab owns the live run; stop that process when its tab is closed; remove the singleton `Output` property.
- `NuGetViewModel`: route restore output to its own output tab instead of the shared singleton.
- `MainWindow.axaml`: add an `OutputTabViewModel` content `DataTemplate`; remove the bottom Output `TabItem`.
- `MainWindow.axaml.cs`: per-instance output-editor binding (multiple output tabs now share one realized `TextEditor`); remove the obsolete `ShowOutput()`/`OnRunClick`/singleton output binding; disable the tab-header context menu on output tabs.

### Out of scope
- **Concurrent multi-run.** The single-process engine in `RunService` stays exactly as is — starting a run still calls `Stop()` first. Per-project concurrent processes are a deliberate later decision.
- **Close-confirmation dialog.** Closing a running output tab stops the process with no prompt. The "do you want to stop the project?" dialog is a separate follow-up ticket (the codebase has no yes/no dialog infrastructure today).
- **Making NuGet restore stoppable/cancellable.** NuGet restore is not a `RunService` process; closing its tab mid-restore just removes the tab (see Gotchas).
- **Context-menu actions on output tabs.** The header context menu (Open in Explorer / Copy path) is disabled on output tabs, not repurposed.
- Persisting output across sessions; word-wrap/search/theming controls on output (unchanged from the current plain-text output).

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 image-preview-tabs.md` — established the polymorphic tab system (`TabViewModelBase` + concrete VMs + `DataType`-keyed `TabControl.DataTemplates`). The exemplar for adding a new tab kind, including the abstract-base + factory decisions.
  - `docs/tickets/complete/2026-07-05 output-panel-plain-text.md` — the current output rendering: read-only `ae:TextEditor` backed by a `TextDocument`, 5000-line cap, tail-follow (`Document.Changing`/`Changed`, at-bottom epsilon). This logic moves into the per-tab output editor.
  - `docs/tickets/complete/2026-07-07 activate-output-panel-on-run.md` — added `ShowOutput()`/`OnRunClick`/`OutputTab` x:Name; those become obsolete here (activation now happens by setting `ActiveTab` in the VM).
- **Design docs**: `docs/avaloniaedit.md` — `TextEditor.Document` is a CLR property, so it must be assigned in code-behind, not bound in XAML.
- **Code anchors**:
  - `ViewModels/TabViewModelBase.cs` — `FilePath`, `Header`, `RequestClose`, `SaveCommand`/`SaveAsync`, `CloseAsync`, static `CreateForFile`.
  - `ViewModels/EditorTabViewModel.cs`, `ViewModels/ImageTabViewModel.cs` — concrete subclasses to mirror.
  - `ViewModels/MainWindowViewModel.cs` — `Tabs`, `ActiveTab`, `OpenFileAsync` (dedup loop), `CloseTabAsync`, `PlayAsync`, `Stop`, the `Output` property, `NuGetVm` construction.
  - `ViewModels/OutputViewModel.cs` — the reusable output buffer (document + `Append`/`Clear` + 5000-line cap).
  - `Services/RunService.cs` — single `_current` process, `IsRunning` (currently unused), `RunAsync` (calls `Stop()` first), `Stop`.
  - `ViewModels/NuGetViewModel.cs` — `ApplyAsync` (the `_output.Clear()`/`Append`/`RestoreAsync(..., _output.Append)` site) and the ctor taking `OutputViewModel`.
  - `Views/MainWindow.axaml` — file-area `TabControl` (`ItemsSource="{Binding Tabs}"`), its `ItemTemplate` (header + ✕ + context menu) and `TabControl.DataTemplates`; the bottom `BottomTabs` with the Output `TabItem`.
  - `Views/MainWindow.axaml.cs` — `BindEditor` (the per-instance rebind-on-`DataContextChanged` pattern to mirror for output), `BindOutputEditor`/`OnOutputEditorAttached` (the singleton binding to replace), `OnCloseTabClick`, `GetTargetPath`, `ShowOutput`/`OnRunClick`, the F5 branch in `OnGlobalKeyDown`.

## Constraints & Gotchas
- **One realized `TextEditor` is shared across same-type tabs.** Avalonia's `TabControl` swaps the content presenter's `DataContext` when moving between two `OutputTabViewModel` tabs rather than realizing a fresh control. The current singleton `BindOutputEditor` (one-shot `_outputBound` `HashSet`, binds `vm.Output.Document` once) is insufficient — it must become a rebind-on-`DataContextChanged` binding that re-points `editor.Document` to the newly-active output tab's document and re-wires tail-follow, detaching the previous document's `Changing`/`Changed` handlers. This is exactly what `BindEditor` already does for code tabs (per-editor state, `ReferenceEquals(currentTab, tab)` guard, handler swap) — mirror it.
- **Tab identity must not key on title or path.** Two situations demand a namespaced identity key (e.g. `file:`/`run:`/`nuget:` prefixes over a normalized path): a project's `.csproj` opened as an editor must not collide with that same project's run output tab (both would otherwise share the project path), and a project literally named "NuGet" must not collide with the package-restore tab. Dedup/reuse everywhere keys on this identity, not `FilePath` or `Header`. (Two tabs may legitimately share a display title — e.g. a project named "NuGet" — as long as their identities differ.)
- **`FilePath` becomes optional.** Output tabs have no backing file. Existing `ActiveTab.FilePath` consumers are already safe against this: `GoToDefinitionAsync`/`FindReferencesAsync` early-return because `FindActiveEditor()` returns null for non-editor tabs; `GetTargetPath` must tolerate a null `FilePath` (the header context menu is disabled on output tabs anyway).
- **Live-run-tab tracking has a race under single-run.** Because starting run B calls `Stop()` and kills run A, A's awaited `RunAsync` returns and A's `PlayAsync` continuation runs *after* B has become the live tab (F5/button can trigger a second `PlayAsync` while the first is still awaiting). Whatever tracks "which output tab owns the live process" must only clear/act when the tracked tab still equals the one being finalized — never blindly clear on any run's completion.
- **NuGet restore is not a `RunService` process.** It streams via `NuGetService.RestoreAsync`, awaited inside `ApplyAsync`. There is no process to stop when its tab closes; closing the `NuGet - Output` tab mid-restore simply removes the tab, and the in-flight restore harmlessly finishes appending to a buffer no longer shown. No stop-on-close logic applies to the NuGet tab.
- **`TextEditor.Document` is CLR, not an `AvaloniaProperty`** — assign in code-behind (per `docs/avaloniaedit.md`); XAML binding silently no-ops.
- **`Append` runs on background threads** (the stdout/stderr readers in `RunService`). The existing `OutputViewModel` already marshals via `Dispatcher.UIThread.Post`; preserve that when reusing/relocating it.

## Open Decisions
1. **`OutputTabViewModel` buffer** — compose the existing `OutputViewModel` (reuse its document + `Append`/`Clear` + 5000-line cap) vs. absorb that logic directly into the new VM. Default: compose `OutputViewModel`; it already encapsulates the exact buffer semantics.
2. **Stop-on-close mechanism** — VM-side field tracking the live-run output tab (checked in `CloseTabAsync`) vs. a stop callback carried on the run tab. Default: VM-side tracking, keeping the `RunService` coupling inside `MainWindowViewModel`.
3. **NuGet output wiring** — how `NuGetViewModel` reaches its output tab now that the shared singleton is gone: inject a `Func<OutputViewModel>` (or similar delegate) that gets-or-creates + activates the `NuGet - Output` tab and returns its buffer, vs. another shape. Default: inject a resolve-and-activate delegate; keep `NuGetViewModel` ignorant of tab mechanics.
4. **New output tab placement** — where a freshly created output tab is inserted in `Tabs`. Default: append at the end (mirrors `OpenFileAsync`).

## Acceptance Criteria
- [ ] Running a project opens or reuses a tab in the main file-area `TabControl` titled `<project name> - Output`, makes it the active tab, and streams build/run output into it.
- [ ] Re-running the same project reuses that same tab (cleared at run start) rather than creating a duplicate.
- [ ] Running a *different* project produces a separate output tab; the previously-run project's output tab remains open showing its finished output.
- [ ] NuGet "Apply" streams restore output into a tab titled `NuGet - Output` (created or reused), and into no bottom-panel surface.
- [ ] The bottom `BottomTabs` panel no longer contains an Output tab; Find and NuGet remain and behave as before.
- [ ] Closing an output tab whose project's process is still running stops that process; closing a finished output tab (or any non-live one) removes it with no process side effect.
- [ ] Opening a project's `.csproj` as an editor tab and running that same project yield two distinct tabs (no dedup collision between the file tab and the run output tab).
- [ ] A project named "NuGet" and a package restore produce two separate output tabs (distinct identities), even though both display as `NuGet - Output`.
- [ ] Ctrl+S while an output tab is active performs no file write and raises no error.
- [ ] The tab-header context menu (Open in Explorer / Copy absolute path / Copy relative path) is disabled on output tabs.
- [ ] An output tab is read-only and preserves tail-follow parity with the former Output panel: appends pin the view to the bottom when it was at the bottom, and do not yank it down when the user has scrolled up.
- [ ] With two output tabs open, switching between them shows each tab's own buffer with no cross-contamination.

## Implementation

### 1. Add namespaced identity to `TabViewModelBase`; make `FilePath` optional
Give `TabViewModelBase` an identity key (e.g. `TabId`) that all dedup/reuse compares on, and relax the file-backed assumption so a tab need not have a `FilePath`. File-backed tabs derive their identity from a `file:`-namespaced normalized full path; the `Header` default stays filename-based for them. Update the static `CreateForFile` factory so file tabs set both `FilePath` and the `file:` identity. Keep `SaveCommand`/`CloseAsync`/`RequestClose` unchanged. Ensure `EditorTabViewModel`/`ImageTabViewModel` still construct correctly through the widened base.

### 2. Add `OutputTabViewModel`
New `ViewModels/OutputTabViewModel.cs` deriving `TabViewModelBase`. It carries an output buffer (see Open Decision #1), a run/nuget-namespaced identity, and a fixed `Header` (`"<project name> - Output"` or `"NuGet - Output"`) supplied at construction. `SaveAsync` is a no-op returning a completed task; `IsDirty` is never set (mirror `ImageTabViewModel`). Expose whatever the view needs to bind the document and whatever the VM needs to `Append`/`Clear`.

### 3. Route run output through an output tab in `MainWindowViewModel`
Add a helper that gets-or-creates an `OutputTabViewModel` by identity (scanning `Tabs`, wiring `RequestClose += CloseTabAsync`, appending if new — Open Decision #4) and returns it. Rework `PlayAsync`: instead of `Output.Clear()` + `Run.RunAsync(StartupProject, Output.Append)`, resolve the `run:`-identity tab for the startup project, clear it, set it as `ActiveTab`, record it as the live-run tab (Open Decision #2), then `Run.RunAsync(StartupProject, <tab>.Append)`. In the completion path, clear the live-run tracking only if it still refers to this tab (see Gotchas — the single-run kill race). Delete the singleton `public OutputViewModel Output { get; }` property once no consumer remains.

### 4. Stop the process when its live output tab closes
In `CloseTabAsync`, before/instead of the existing dirty-save path, detect when the tab being closed is the live-run output tab and its process is still running (`Run.IsRunning`), and call `Run.Stop()` (clearing the tracking). Non-live output tabs and file tabs keep their current close behavior. Keep the existing `ActiveTab` fallback logic.

### 5. Route NuGet restore output to a `NuGet - Output` tab
Change `NuGetViewModel`'s dependency from the shared `OutputViewModel` to a resolve-and-activate delegate (Open Decision #3). In `ApplyAsync`, replace the `_output.Clear()`/`_output.Append(...)`/`RestoreAsync(..., _output.Append)` calls with the resolved `nuget:`-identity tab's buffer. Construct the delegate in `MainWindowViewModel` where `NuGetVm` is built, using the same get-or-create helper from step 3.

### 6. Dedup `OpenFileAsync` on identity
Change the reuse scan in `OpenFileAsync` to compare the new identity key (the `file:` id for the path) instead of `FilePath.Equals(...)`, so file tabs and output tabs can never collide even on the same underlying path.

### 7. Add the output content template; remove the bottom Output tab
In `MainWindow.axaml`, add a `DataTemplate DataType="vm:OutputTabViewModel"` to the file-area `TabControl.DataTemplates`, styled like the current bottom Output editor (read-only `ae:TextEditor`, no line numbers, no word-wrap, Consolas, `#000000`/`#DCDCDC`) with attach/`DataContextChanged` handlers for binding. Remove the entire `<TabItem Header="Output" x:Name="OutputTab">` block (and its `OutputEditor`) from `BottomTabs`.

### 8. Per-instance output-editor binding in code-behind
Replace the singleton `BindOutputEditor`/`_outputBound` approach with a per-instance binding that mirrors `BindEditor`: on attach and on `DataContextChanged`, if the editor's `DataContext` is an `OutputTabViewModel`, point `editor.Document` at that tab's document and (re)wire tail-follow (`Document.Changing` captures at-bottom with the existing epsilon; `Document.Changed` scrolls to the last line when it was at bottom), detaching handlers from any previously-bound document. See Gotchas for why the one-shot binding fails once multiple output tabs exist.

### 9. Remove obsolete run-activation view plumbing
Delete `ShowOutput()` and `OnRunClick`, drop the `Click="OnRunClick"` from the Run button, and remove the `ShowOutput()` call from the F5 branch in `OnGlobalKeyDown` — activation now happens in `PlayAsync` by setting `ActiveTab`. Remove the now-unused `OutputTab` x:Name reference and the old `OnOutputEditorAttached`/`OnOutputEditorDataContextChanged`/singleton `BindOutputEditor` members superseded by step 8.

### 10. Disable the header context menu on output tabs; harden `GetTargetPath`
The shared tab-header `ItemTemplate` context menu (Open in Explorer / Copy absolute path / Copy relative path) should be inert for output tabs. Gate the menu items (e.g. an `Opening` handler, mirroring `OnSolutionCtxOpening`'s enable/disable pattern, or per-item enable bound to "has a file path"), and make `GetTargetPath` return null for a tab with no `FilePath` so the copy/open handlers no-op.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings (kill any running `MiniIde.exe` first — it locks its own output DLL).
- [ ] Launch via `scripts/run.ps1`. Open a solution with a runnable startup project. Press F5 / click Run — a `<project> - Output` tab appears in the main tab strip, becomes active, and streams build/run lines in the same order as the old panel.
- [ ] Run the same project again — the same tab is reused and cleared (no duplicate tab).
- [ ] Switch the startup project to a second runnable project and run it — a second output tab appears; the first project's output tab is still present and shows its previous (now finished) output. Switch between the two tabs — each shows its own buffer.
- [ ] With a project actively running (e.g. a console app that keeps printing), close its output tab — the process stops (verify output ceases / the app's port or console is released / it disappears from Task Manager). Close a finished output tab — no side effects.
- [ ] Go to the NuGet tab, apply a package version change — a `NuGet - Output` tab appears in the main tab strip with the restore log; the bottom panel has no Output tab.
- [ ] Confirm `BottomTabs` shows only Find and NuGet (no Output).
- [ ] Open a runnable project's `.csproj` as an editor tab, then run that project — two separate tabs exist (editor + `<project> - Output`).
- [ ] With an output tab active, press Ctrl+S — nothing is written, no error dialog/status error.
- [ ] Right-click an output tab's header — the Open in Explorer / Copy path items are disabled.
- [ ] Trigger verbose output, scroll up mid-stream — appends do not yank the view to the bottom; scroll back to the bottom — appends re-pin to the bottom. Force >5000 lines — the top lines drop and the total holds at the cap.
- [ ] Regression: open a `.cs` / `.xml` / `.json` file — colorization, editing, and save behave as before; opening a Find result still navigates correctly; the Find and NuGet bottom tabs work.
