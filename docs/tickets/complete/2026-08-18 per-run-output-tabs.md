# Per-Run Output Tabs

## Context
**Current behavior**: Running is single-process by construction. `RunService` holds one `Process? _current` and `RunAsync` opens by calling `Stop()`, so starting a run kills the previous one. `MainWindowViewModel.PlayAsync` gets-or-creates **one** output tab per project (keyed `run:` + normalized path), clears it, and records it in `_liveRunTab` so the tab that owns the live process can stop it on close. A single toolbar `■ Stop` button kills whatever is currently live.

**New behavior**: Every click of Run opens a **fresh output tab that owns its own `dotnet` process**, and runs proceed concurrently — so an app can be launched several times over (MiniIDE itself being the motivating case). Repeat runs of the same project get a numbered header suffix. Stop moves out of the toolbar and into each output tab, stopping only that tab's run. Closing a tab stops its own run; closing the window stops all of them.

## Prerequisites
None.

## Scope
### In scope
- `RunService`: from one-process-at-a-time to a per-run handle — starting a run returns something that owns its own process, can stop just that process, and can be awaited for exit. The service-wide `_current` / `IsRunning` / `Stop` surface goes away.
- `OutputTabViewModel`: holds the run handle for its own tab and exposes a stop command plus whether a run is live there.
- `MainWindowViewModel.PlayAsync`: a fresh tab per invocation with a unique id and a disambiguated header; removal of `_liveRunTab` and of the now-dead `tab.Clear()` call.
- Terminating every live run when the main window closes.
- `MainWindow.axaml`: remove the toolbar Stop button; add a small stop/state strip above the output editor in the `OutputTabViewModel` content template.

### Out of scope
- **Run configurations and launch profiles** (Debug vs. Release, `-c` / `-lp`) — that is `run-configuration-dropdown.md`, which depends on this ticket.
- **Close-confirmation dialog.** Closing a tab with a live run still stops it with no prompt; the codebase still has no yes/no dialog infrastructure.
- **Making NuGet restore stoppable.** A restore is not a `RunService` process, so the new strip must be absent on the `NuGet - Output` tab rather than wired to the restore.
- **Any cap or auto-close on run-tab count.** Run tabs accumulate until the user closes them, exactly like file tabs.
- **Mitigating build contention** between concurrent runs of the same project — see Constraints & Gotchas; the failure is surfaced, not prevented.
- Persisting output across sessions.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — established output-as-tabs, the `run:`/`nuget:` identity scheme, the `_liveRunTab` tracking, and the close-stops-the-process rule. Its "Out of scope: concurrent multi-run — the single-process engine stays exactly as is; per-project concurrent processes are a deliberate later decision" is precisely the decision this ticket makes.
  - `docs/tickets/complete/2026-07-07 activate-output-panel-on-run.md` — why a run activates its output tab; that behavior is preserved per-invocation.
  - `docs/tickets/complete/2026-07-07 fix-dotnet-test-invocation.md` — the `dotnet test` (positional path) vs. `dotnet run` (`--project`) asymmetry that must survive the refactor.
- **Design docs**: `docs/avaloniaedit.md` — `TextEditor.Document` is a CLR property, not an `AvaloniaProperty`, so it is assigned imperatively rather than bound in XAML. Relevant because the output content template gains a wrapper around the editor.
- **Code anchors**:
  - `RunService.RunAsync` and `RunService.Stop` — the argument building (verb selection, test-vs-run invocation) is worth preserving verbatim; only the process ownership changes.
  - `MainWindowViewModel.PlayAsync`, `Stop`, `GetOrCreateOutputTab`, `CloseTabAsync`, the `_liveRunTab` field and its kill-race comment, and `OnStartupProjectChanged`.
  - `OutputTabViewModel` — `Append` / `Clear` and the UI-thread marshalling both mutators rely on.
  - `TabViewModelBase` — `TabId` (the identity contract these new ids must satisfy), `Header`, `CloseAsync` / `RequestClose`.
  - `TabEditorBinder.ForOutputTabs` and its output-tab attach path — how the one realized output editor is bound; read before restructuring the output `DataTemplate`.
  - `MainWindow.axaml` — the `OutputTabViewModel` content `DataTemplate`, and the top-bar `StackPanel` holding the startup `ComboBox` plus the Run and Stop buttons.
  - `MainWindow.axaml.cs` — the `Closed` handler (currently disposes only `Watcher`).

## Constraints & Gotchas
- **A latent bug this fixes; do not carry it forward.** `RunAsync` logs its exit line by reading the `_current` *field* after awaiting. Start a second run while the first is live and the first run's continuation reads the *second* run's process; since that one has not exited, `Process.ExitCode` throws `InvalidOperationException`, which `PlayAsync`'s catch turns into a status-bar message instead of an `[exit …]` line in the right tab. Per-run ownership must make this structurally impossible — the exit code has to come from the handle the run started with, never from shared state.
- **One realized output editor serves every output tab.** A `TabControl` realizes one control per `DataType` and swaps its `DataContext`, which is the whole reason `TabEditorBinder` exists. The new strip is therefore realized once and reused: everything it shows must come from the bound tab, never from control-local state.
- **NuGet output tabs are `OutputTabViewModel` too.** They will pick up the same content template, so the strip must hide itself when the bound tab has no run attached.
- **`OutputTabViewModel.Clear()` stays.** `NuGetViewModel` still uses the get-or-create-then-clear path for both the restore output and per-package metadata tabs. Only `PlayAsync`'s call becomes dead, because a freshly created tab is already empty.
- **Concurrent runs of the same project contend on the build output.** With one instance live and holding `bin/<cfg>/<app>.exe`, a second run that has to rebuild after a source edit emits ten `MSB3026` copy-retry warnings a second apart and then fails with `MSB3027` / `MSB3021`. This is accepted: the failure belongs in that run's own output tab, the same way Visual Studio surfaces it. With no source edit since the live instance was built, MSBuild's up-to-date check skips the copy and the second run is clean; different configurations never contend at all because they write to different `bin` subtrees.
- **Preserve the `dotnet test` invocation asymmetry.** `dotnet test` takes the project path positionally and rejects `--project` (MSB1001); `dotnet run` requires `--project`. The existing comment at that branch explains why — keep both the branch and the explanation.
- **The status bar is last-write-wins.** With concurrent runs, one run's completion message will overwrite another's "running" message. Run-related status messages should name their project so whatever wins is at least attributable.
- **Nothing terminates run children on exit today.** The window's `Closed` handler disposes only the `Watcher`, and these processes are started with `UseShellExecute = false` / `CreateNoWindow`, so Windows does not reap them with the parent. One orphan is already possible today; N concurrent runs make it N orphans, so shutdown cleanup is in scope here.

## Open Decisions
1. **Handle shape** — a dedicated session/handle type owning its process and exposing stop-plus-await, versus handing the `Process` back to the caller. Default: a small handle type, so no view model has to touch `System.Diagnostics`.
2. **Where the handle lives** — a nullable run handle on `OutputTabViewModel`, versus a dedicated run-output subclass. Default: the nullable handle; a subclass would need its own `DataTemplate` and binder registration for no behavioral gain, and would still have to answer the same "is a run attached" question.
3. **Repeat-run numbering** — a monotonic per-project counter, versus reusing the lowest number not currently open. Default: monotonic, so closing `(2)` does not make the next run reuse that label.
4. **Strip content beyond the Stop button** — whether to also show a running/exited indicator. Default: include a short state text; it is the cheapest way to tell a finished tab from a live one at a glance.
5. **Whether stopping is disabled or hidden once a run exits.** Default: keep the button present but disabled, so the strip does not reflow when a run ends.

## Acceptance Criteria
- [ ] Clicking Run twice on the same runnable startup project produces two separate output tabs, each with its own live process, and neither tab's text is cleared or overwritten by the other.
- [ ] The first output tab for a project carries the plain `<project name> - Output` header; the second and later ones carry a distinguishing numbered suffix.
- [ ] Each run's output tab offers a stop affordance that terminates that run's process tree only; other live runs continue.
- [ ] No stop affordance appears on the `NuGet - Output` tab or on per-package NuGet metadata tabs.
- [ ] Closing an output tab whose run is still live terminates that run's process tree and leaves every other live run running.
- [ ] Closing the main window terminates the process tree of every live run.
- [ ] Each run writes its own `[exit <code>]` line, carrying its own exit code, into its own tab — including when a later run was started before the earlier one finished.
- [ ] `RunService` exposes no service-wide "current process" / `IsRunning` / `Stop` members, and `MainWindowViewModel` retains neither `_liveRunTab` nor a window-level stop command.
- [ ] The `■ Stop` button is gone from the top bar in `MainWindow.axaml`.
- [ ] Test projects still run as `dotnet test <project path>` with the path positional, and non-test projects still as `dotnet run --project <project path>`.

## Implementation

### 1. Turn `RunService` into a per-run launcher
The point is that a run's lifetime belongs to the run, not to the service. Keep `RunService` as the single place that knows how to phrase a `dotnet` invocation for a `ProjectEntry` — the verb choice and the test-vs-run argument asymmetry, comment included — but have starting a run hand back a handle that owns its process and can be stopped and awaited independently. Delete the `_current` field along with the service-wide `IsRunning` and `Stop`, and the `Stop()` call at the top of the start path that currently kills the previous run. The exit line must be logged from the handle's own process so it can never read another run's state; keep the existing cancellation-token registration behavior, scoped to the one run.

### 2. Let an output tab own its run
Give `OutputTabViewModel` a way to be handed a run handle, a stop command that acts on it, and an observable notion of whether a run is currently live there — the strip and the close path both need to ask. Tabs with no handle (NuGet output, NuGet metadata) must answer "no run" so the strip stays hidden. Mind that `Append` already marshals to the UI thread from the reader threads; any run-state change raised from a background completion needs the same treatment so property-changed notifications reach bindings safely.

### 3. Create a fresh tab per Run click
Rework `PlayAsync` so each invocation builds a brand-new `OutputTabViewModel` instead of reusing one. Two things need generating: a `TabId` unique per invocation while staying in the `run:`-namespaced convention `TabId` documents, and a header that disambiguates repeats of the same project per Open Decision 3. Activate the new tab as today, attach the run handle to it, drop the `tab.Clear()` (a new tab is empty), and await the handle's completion so the status message still fires when that run ends — naming the project in the message, per Constraints. Leave `GetOrCreateOutputTab` in place for the NuGet callers that still want get-or-create semantics.

### 4. Drop the single-run bookkeeping
Remove `_liveRunTab` and its kill-race comment — with per-tab ownership the race it guarded against cannot arise. Rewrite `CloseTabAsync` to stop whatever run the closing tab itself owns rather than consulting shared state. Remove the window-level `Stop` command and the `StopCommand.NotifyCanExecuteChanged()` call in `OnStartupProjectChanged`; `PlayCommand`'s own `CanPlay` gating is unchanged.

### 5. Stop live runs on window close
Extend the `Closed` handler in `MainWindow.axaml.cs` — currently disposing only the `Watcher` — to also stop every live run. Walk the output tabs and stop the ones holding a live handle; a view-model-side helper that does the walk keeps the code-behind thin and matches how `Watcher.Dispose()` is reached there today.

### 6. Move Stop into the output tab
In `MainWindow.axaml`, wrap the `OutputTabViewModel` template's `ae:TextEditor` in a two-row container: a compact strip on top with the stop button (and state text, per Open Decision 4) bound to the tab, the editor filling the rest. The editor keeps all three lifecycle handlers (`AttachedToVisualTree`, `DataContextChanged`, `DetachedFromVisualTree`) exactly as it has them — `TabEditorBinder` depends on them and on the editor's document being assigned imperatively. Bind the strip's visibility to the tab's has-a-run state so NuGet output tabs render as they do today.

### 7. Remove the toolbar Stop button
Delete the `■ Stop` `Button` from the top-bar `StackPanel`, leaving the startup `ComboBox` and the Run button. Adjust the Run button's margin if removing its neighbour leaves the spacing lopsided.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes — nothing in `MiniIde.Tests` currently touches `RunService` or `PlayAsync`, so this is a regression check rather than new coverage.
- [ ] Open this repository's own solution, select MiniIde as the startup project, and click Run twice. Confirm two output tabs, both streaming, with distinct headers.
- [ ] With both live, click Stop in the first tab. Confirm only that instance dies, its tab logs an exit line, and the second keeps running.
- [ ] With one run still live, close its tab and confirm via Task Manager that the process is gone.
- [ ] Start a run, then close the MiniIDE window. Confirm no orphaned `dotnet` or app process survives.
- [ ] Run a test project (`MiniIde.Tests`) and confirm the invocation still works — the test output appears and no MSB1001 error is emitted.
- [ ] Start a run, edit a source file, and run again. Confirm the copy-lock failure lands in the *second* tab's output and leaves the first run running.
- [ ] Trigger a NuGet restore and confirm the `NuGet - Output` tab shows no stop affordance and behaves exactly as before.
- [ ] Let a run exit on its own, then start another; confirm each tab shows its own correct `[exit <code>]` and no `InvalidOperationException` text reaches the status bar.

## Learnings

### Architectural decisions
- **Open Decision 1 — handle shape: dedicated `RunSession`.** `RunService.Start` now returns a `RunSession` that owns exactly one `Process` and exposes `Stop()` plus a `Completion` task. `RunService` keeps only the knowledge of how to phrase a `dotnet` invocation; it holds no state at all, which is what makes "starting a run cannot disturb another run" structural rather than a rule someone has to remember. No view model touches `System.Diagnostics`.
- **`Completion` is a property, not a method.** The session starts waiting in its constructor and hands out the same task, so both `PlayAsync` (for the status message) and the owning tab (for the live/exited flag) can await the one run without either having to know the other exists. Awaiting a task twice is free; two `WaitForExitAsync` calls would not have been.
- **Open Decision 2 — nullable handle on `OutputTabViewModel`, no subclass.** Confirmed as the ticket predicted: a subclass would have needed its own `DataTemplate` and `TabEditorBinder` registration, and the template would still have had to answer "is a run attached" to decide whether to draw the strip. Two flags carry it: `HasRun` (was a run ever attached — the strip's visibility) and `IsRunLive` (is it still going — the stop button's `CanExecute` and the state text). They are deliberately distinct so the strip does not disappear and reflow the editor the moment a run exits.
- **Open Decision 3 — monotonic per-project counter.** A `Dictionary<string, int> _runsStarted` keyed by the normalized project path, incremented per invocation and never decremented. Headers read `MiniIde - Output`, `MiniIde - Output (2)`, …; the tab id is `run:{normalized path}#{n}`, which keeps the `run:` namespace `TabId` documents while being unique per invocation.
- **Open Decisions 4 & 5 — both defaults taken.** The strip carries the stop button plus a `Running`/`Exited` text, and the button stays present-but-disabled after exit (`[RelayCommand(CanExecute = nameof(IsRunLive))]`).
- **`StopRun` is one method serving three callers.** The strip's command, `CloseTabAsync`, and `StopAllRuns` all call the same `OutputTabViewModel.StopRun()`, and `RunSession.Stop()` is a no-op on an already-exited (or never-attached) run. So the close and shutdown paths call it unconditionally on every output tab — no "is there a run, is it live" interrogation at any call site, and NuGet tabs pass through harmlessly.

### Problems encountered
- **The latent exit-code bug is now structurally impossible, and it was real.** The old `RunAsync` logged `_current.ExitCode` after awaiting — a field read, so run A's continuation read run B's process. `RunSession.WaitForExitAsync` closes over its own `Process`, so there is no shared state left to read. Verified: a throwaway harness started two concurrent runs, stopped the first, and confirmed the first awaited cleanly and logged its own `[exit …]` while the second kept streaming.
- **Verification was blocked by a locked workstation.** The plan was to launch MiniIDE against a scratch solution and drive it with `SendKeys {F5}`; the keystrokes were silently dropped and a screenshot showed the Windows lock screen. `[System.Windows.Forms.SendKeys]::SendWait` surfaces this as a nonsense `Exception … "The operation completed successfully."`, not as anything naming the lock. Check `Get-Process LogonUI` before trusting a scripted UI drive.
- **Multiple `MiniIde.exe` instances make `Get-Process MiniIde` a footgun.** A developer instance was already running, so `$p.Id` was an array and every downstream `Where-Object { $_.ParentProcessId -eq $p.Id }` matched nothing. Always pin the pid returned by `Start-Process -PassThru`.

### Interesting tidbits
- `RunSession.Stop()` uses `Kill(entireProcessTree: true)`, which matters more than it looks: `dotnet run` is a *launcher* — the app is its child. Killing only the `dotnet` process would leave the app alive. Confirmed via process listing that a stopped run leaves no descendant behind.
- `[RelayCommand(CanExecute = nameof(SomeProperty))]` accepts a **property** name, not just a method — so an `[ObservableProperty] bool` doubles as the gate, and `[NotifyCanExecuteChangedFor(nameof(TheCommand))]` on its backing field keeps the button's enabled state live with no manual `NotifyCanExecuteChanged()` call. That is what makes the disabled-after-exit behavior free.
- Compiled bindings (`AvaloniaUseCompiledBindingsByDefault`) mean the new strip's `{Binding HasRun}` / `{Binding RunState}` / `{Binding StopRunCommand}` are type-checked against `OutputTabViewModel` at build time. A green build is genuine proof the template's bindings resolve — worth remembering when UI can't be exercised interactively.
- Wrapping the output template's `ae:TextEditor` in a `Grid` did **not** disturb `TabEditorBinder`: the three lifecycle handlers stay on the editor itself, and `DataContextChanged` still fires on it when the wrapper's inherited `DataContext` is swapped.

### Workarounds / limitations
- **Not verified interactively.** Everything in the Test Plan that needs a running window (two tabs streaming, the strip's appearance, stop-from-tab, close-tab-stops-run, close-window-stops-all, the NuGet tab having no strip) was verified by code inspection plus a headless harness over `RunService`/`RunSession`, not by clicking. The workstation was locked for the whole session.
- The status bar remains last-write-wins. Every run message now names its project (`Running MiniIde...`, `MiniIde finished`), so whichever concurrent run wins the race is at least attributable — but it is still one line for N runs. A per-tab status would be the real fix and is out of scope.

### Related areas affected
- `MainWindow.axaml.cs`'s `Closed` handler grew from a one-line expression to a block (`Watcher.Dispose()` + `StopAllRuns()`). Anything else needing shutdown cleanup now has an obvious home.
- `GetOrCreateOutputTab` survives untouched for the two NuGet callers, as does `OutputTabViewModel.Clear()` — only `PlayAsync`'s use of both went away.
- `run-configuration-dropdown.md` builds directly on this: its `-c` / `-lp` arguments belong in `RunService.Start`, and its per-run configuration should ride along into the tab header, which is now the only thing that disambiguates repeat runs.

### Rejected alternatives
- **Handing the `Process` back to the caller** (Open Decision 1's other branch) — would have put `System.Diagnostics` and `Kill(entireProcessTree: true)` into a view model, and left "who logs the exit line" ambiguous.
- **Committing the concurrency harness as a test.** It starts two real `dotnet run` processes against a scratch project on disk — slow and environment-dependent, for an invariant that a green build plus code inspection largely protects. The ticket's Test Plan is deliberately manual; the harness was run, its result recorded here, and the file deleted.
- **Reusing the lowest free number for repeat runs** — rejected per Open Decision 3: a header would silently start meaning a different run than it did a minute earlier.
- **Hiding the stop button once a run exits** — rejected per Open Decision 5; disabling it keeps the strip's layout stable.
