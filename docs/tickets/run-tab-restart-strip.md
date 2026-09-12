# Run Tab Restart & Button Strip

## Context
**Current behavior**: A run tab's strip holds exactly one control — `■ Stop` — plus a `RunState` word ("Running" / "Exited"). The strip is hidden outright on tabs with no run (`IsVisible="{Binding HasRun}"`), which is how NuGet output and per-package metadata tabs avoid it. Stop is always enabled, including after the run has exited, where it is a silent no-op. To run the same thing again you go back to the toolbar's Run split button, which by design opens a *fresh* tab — so a session of iterating on one project accumulates tabs. `OutputTabViewModel` holds a `RunSession` but nothing describing what was run, so the tab has no way to relaunch itself.

**New behavior**: A run tab can relaunch itself in place. The strip becomes a persistent row of controls — `↻ Restart`, `■ Stop`, and the state indicator — where **every control is always present, and a control that cannot act is disabled with a tooltip naming the reason** rather than hidden. Restart stops whatever is live in that tab, then starts the same project under the same run selection again into the same tab, appending a visible separator so the transcript reads as successive runs rather than one muddled stream. Stop is unchanged in what it does, but is now greyed with "Not running" once the run has exited. Tabs with no run at all (NuGet) keep no strip.

## Prerequisites
None.

## Scope
### In scope
- `OutputTabViewModel` learns *what its run was* — the project and the run selection that produced it — so it can start that same thing again, plus the command and enablement state the strip binds to.
- A relaunch path that reuses the tab rather than creating one: stop what's live, mark a separator, start again, re-attach the new session.
- `MainWindowViewModel.PlayAsync`: extract the "start a run into *this* tab" half so first-run and restart share one code path and cannot drift.
- Guarding the live-flag against a superseded run's completion (see Constraints).
- `MainWindow.axaml` — the `OutputTabViewModel` content `DataTemplate`'s strip: a Restart button, a tooltip on Stop, and the indicator.
- `ActionIcon` — a stop glyph, and the font subset regenerated.

### Out of scope
- **Hot reload / `dotnet watch` in any form.** That is `hot-reload-live-run-mode.md`, which depends on this ticket and adds a fourth control to the strip this ticket builds.
- **An "Apply changes" button.** Deliberately dropped after investigation, and worth recording so it isn't re-proposed: applying changes to a *running* process without restarting it requires Roslyn's Edit-and-Continue delta producer, whose public wrapper (`Microsoft.CodeAnalysis.ExternalAccess.HotReload`) ships only inside the SDK's `dotnet-watch` tool folder, is `InternalsVisibleTo`-gated to `Microsoft.DotNet.HotReload.Watch`, and is not on NuGet — as is the applier half (`Microsoft.Extensions.DotNetDeltaApplier`, 404 on nuget.org). The one host we *can* drive, `dotnet watch`, has no manual-apply mode at all. So there is no state in which such a button could be enabled, and a permanently-dead button teaches the wrong rule.
- **Changing the toolbar Run split button.** It still opens a fresh tab on every click; that is the decision from `per-run-output-tabs` and this ticket does not revisit it.
- **A confirmation prompt when restarting a live run.** The codebase still has no yes/no dialog infrastructure; restart stops the old process the same way closing the tab does.
- **Rebuilding only when source changed.** Restart always rebuilds-and-relaunches (`dotnet run` builds implicitly). No staleness detection — that idea was considered and set aside as redundant with Restart itself.
- **Giving NuGet output or metadata tabs any of these controls.**
- **Queuing or persisting restarts**, and any cap on how many times a tab may be restarted.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/complete/2026-08-18 per-run-output-tabs.md` — established per-tab process ownership, the strip itself, and the rule that the strip is *realized once and reused* across output tabs. Its reasoning for why a run's lifetime belongs to the tab and not to a service is the foundation this ticket builds on.
  - `docs/tickets/complete/2026-08-18 run-configuration-dropdown.md` — `RunSelection`, the `Describe` helper, and the project+configuration+profile tab-numbering key. Its Learnings also carry the XAML `--` build failure noted in Constraints.
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — the greyed-but-present decision for inapplicable actions, which this ticket generalizes from context menus to controls.
  - `docs/tickets/complete/2026-08-20 tab-context-menu-first-open-disabled.md` — why enablement should be a declarative `IsEnabled` binding rather than an `Opening`/code-behind handler.
- **Design docs**: `docs/avaloniaedit.md` (`TextEditor.Document` is a CLR property, assigned imperatively); `docs/avalonia.md` (flyout/binding gotchas from the run-configuration work).
- **Code anchors**:
  - `OutputTabViewModel` — `AttachRun`, `StopRun`, `MarkExitedAsync`, `HasRun`, `IsRunLive`, `RunState`, `Append`, `Clear`.
  - `MainWindowViewModel.PlayAsync` — the whole body is the thing being split; also `NextRunTab`, `Describe`, `CloseTabAsync`, `StopAllRuns`.
  - `RunService.Start` and `RunSession` — the argument phrasing and the per-run handle stay exactly as they are; only who calls `Start` changes.
  - `MainWindow.axaml` — the `<DataTemplate DataType="vm:OutputTabViewModel">` strip `Border` and its `StackPanel`.
  - `TabEditorBinder.ForOutputTabs` / `AttachOutputTab` — the tail-follow wiring, so the separator append behaves like any other append.
  - `ActionIcon` and `tools/README.md` — the glyph constants and the mandatory re-subset step.

## Constraints & Gotchas
- **The strip is realized once and reused.** A `TabControl` reuses one realized control per `DataType`, swapping its `DataContext` — the existing XAML comment says so explicitly. Every piece of strip state must therefore come from the *bound tab*; nothing may live on the window or the main view model, or it will bleed between tabs.
- **A superseded run can clear the live flag.** `MarkExitedAsync` awaits a session's completion and then posts `IsRunLive = false`. On restart, the old session's completion lands *after* the new session has already set the flag true, silently greying Stop on a run that is very much alive. The exit handler must only clear the flag if the session it was watching is still the tab's current one.
- **XAML comments cannot contain a double hyphen.** Writing `--project` or `--non-interactive` inside an XAML comment fails the build as `AVLN1001: File doesn't contain valid XAML` with a raw `XmlException` stack that reads like a structural error. Spell such flags without the leading hyphens in comments, or don't mention them.
- **A new icon glyph needs the font re-subset**: `cd tools && npm run subset`. The shipped TTF contains only glyphs referenced in source (the full MDI font stalls 12–40 s on load via its ligature table). `IconFontCoverageTests` fails `dotnet test` if you forget, so this is caught — but catch it deliberately. Codepoints are SPUA-A (`\U000FXXXX`); verify any new one against the pinned MDI 6.8.96 cheatsheet rather than guessing.
- **`Append` is called from the stdout/stderr reader threads** and marshals to the UI thread itself. The separator write goes through the same path, so it needs no special handling — but it must not bypass `Append` and touch `Document` directly from a background thread.
- **`HasRun` still gates the strip's visibility.** NuGet output and per-package metadata tabs are `OutputTabViewModel`s with no run; they must continue to show no strip at all. "Disabled, not hidden" applies to controls *within* a run's strip, not to giving non-run tabs a strip they have no business having.
- **Run-tab identity does not change.** `NextRunTab` allocates a `run:` id and header once, at creation. A restart reuses the tab, so it keeps its existing id and header — it must **not** allocate a new number, or the header would stop naming the run it has named all along.

## Open Decisions
1. **Stop and Restart glyphs** — `ActionIcon.Reload` (`\U000F0450`, refresh) already exists and is the obvious Restart glyph; Stop needs a new codepoint (MDI `stop`). Default: reuse `Reload`, add one stop constant. Alternatively keep the current `■`/`▶` text characters and add no glyphs at all, which avoids the re-subset entirely.
2. **Separator between runs** — a text marker appended into the document (e.g. a rule line naming the restart) versus clearing the document on restart. Default: append a marker; the scrollback of the previous run is usually what you want to compare against. Note `Clear()` already exists but is currently unused.
3. **Tooltip wording** — "Not running" for a disabled Stop is the phrasing agreed with the user; the Restart tooltip (if any, since it is always enabled) is the implementer's call.
4. **Whether Restart is enabled while a run is still live** — it is defensible either way (restart-in-place vs. stop-first). Default: enabled always, since stopping is part of what Restart does.

## Acceptance Criteria
- [ ] An `OutputTabViewModel` created for a run exposes the project and `RunSelection` it was started with, and can start that same combination again without the caller re-supplying them.
- [ ] Restarting a tab whose run has exited produces a new live run in **the same tab**, with the same `TabId` and the same `Header` as before the restart; no new tab is added to `Tabs`, and `NextRunTab` is not consulted.
- [ ] Restarting a tab whose run is still live terminates that run's process tree before the replacement run is started.
- [ ] After a restart, `IsRunLive` is true and remains true until the *replacement* run exits — the superseded run's completion does not clear it.
- [ ] `Stop` is disabled exactly when the tab has no live run, and carries a tooltip stating the reason when disabled.
- [ ] The strip is absent (not merely disabled) on `OutputTabViewModel`s that have never had a run attached — the NuGet output and per-package metadata tabs.
- [ ] Restarting a test project re-invokes it under `dotnet test` with its positional project path, and a non-test project under `dotnet run --project` with its launch profile — i.e. the verb/argument asymmetry survives the restart path unchanged.
- [ ] Closing a tab, and closing the window, still stop that tab's run — including a run that arrived via Restart rather than via the toolbar.
- [ ] Every glyph referenced from `ActionIcon` is present in the shipped font subset (`IconFontCoverageTests` passes).

## Implementation

### 1. Give a run tab the identity of its run
A tab that cannot say what it ran cannot run it again. Extend `OutputTabViewModel` so that attaching a run also records the `ProjectEntry` and `RunSelection` behind it. `AttachRun` is the natural place — it is already the single "this tab now owns a run" moment, called once on the UI thread right after starting, and it already sets `HasRun`/`IsRunLive`. Prefer widening that call over adding a second initialisation step a caller could forget.

Note the layering question this raises: `OutputTabViewModel` currently depends on `MiniIde.Services` only for the `RunSession` type. Recording a `ProjectEntry`/`RunSelection` adds a `MiniIde.Models` dependency, which is fine, but *starting* a run needs `RunService`. Decide whether the tab holds a start-delegate handed to it by `MainWindowViewModel` or a reference to the service itself — the delegate keeps the tab ignorant of the service and mirrors how `Append` is already passed as an `Action<string>` rather than the tab knowing about process plumbing.

### 2. Extract "start a run into this tab" from `PlayAsync`
`PlayAsync` currently does two separable things: allocate a fresh tab (snapshot the startup project, build the selection, number the header, add the tab, activate it) and then run into it (status message, `Run.Start`, `AttachRun`, await, final status). Split at that seam so the second half is callable on an existing tab.

The second half is the shared path — it must keep the status-bar behaviour intact, including the reasoning already recorded there: the status bar is last-write-wins and concurrent runs share it, so every message names its project and selection so whichever wins is attributable. A restart's messages should read the same way.

Keep the test-project rule where it is: `PlayAsync` drops the launch profile for a `ProjectKind.Tst` project *independently* of the flyout having offered none, so the verb-to-argument rule lives in one place. A restart reuses the already-sanitised `RunSelection` recorded in step 1, so it inherits that for free — do not re-derive it.

### 3. Add the restart command
A `[RelayCommand]` on `OutputTabViewModel` that stops the current run if one is live, appends the separator (see Open Decision 2), and starts the recorded project/selection again, re-attaching the resulting session. It must not touch `Tabs`, `TabId`, or `Header`.

Failure surfaces the way a first run's failure does — `RunService.Start` throws if the process can't be started, and the existing `PlayAsync` catch turns that into a status message naming the run. Route restart failures through the same path rather than inventing a second error story.

### 4. Make the exit handler ignore a superseded run
`MarkExitedAsync` posts `IsRunLive = false` when the session it was given completes. Once restart exists, two of these can be in flight, and the older one must not clear the newer one's flag. Gate the post on the completed session still being the tab's current one — the tab already holds the current `RunSession` in a field, so a reference comparison at post time is enough. This is the bug most likely to be missed, because it only shows up when a restart happens *before* the previous process has finished dying.

### 5. Build the strip
In `MainWindow.axaml`'s `OutputTabViewModel` `DataTemplate`, extend the existing strip `StackPanel`: Restart to the left of Stop, then the indicator, matching the current `Padding="6,4"` / `MinHeight="0"` sizing so the strip does not grow taller. Keep `IsVisible="{Binding HasRun}"` on the wrapping `Border`.

Enablement is a declarative `IsEnabled` binding plus `ToolTip.Tip` on each control — not an `Opening`-style code-behind handler. The `tab-context-menu-first-open-disabled` ticket is the cautionary tale: an imperative handler that read `DataContext` too early got the enabled state wrong on first open, and the fix was exactly this declarative move.

Stop's disabled tooltip is a constant string; if the codebase gains more of these, a bindable reason property on the tab is the natural next step, but one literal does not justify it yet.

### 6. Add the icon and re-subset
If step 5 introduces a new glyph, add the constant to `ActionIcon` (with the trailing `// name` comment the existing constants carry), then run `cd tools && npm run subset`. Verify the codepoint against the pinned MDI 6.8.96 cheatsheet — `\uXXXX` cannot encode SPUA-A, so the `\U000FXXXX` form is required.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes, including `IconFontCoverageTests`.
- [ ] Open a solution, pick a console project, click Run. When it exits, confirm Stop greys out and hovering it shows the reason tooltip.
- [ ] Click Restart on the exited tab. Confirm: the app runs again, the tab count is unchanged, the tab header still reads exactly as it did (same number, if it had one), and the separator makes the run boundary obvious in the scrollback.
- [ ] Click Restart while the app is still running. Confirm the old process dies, the new one starts, Stop stays enabled, and the indicator does not flicker to "Exited" a second or two later — this is the superseded-run race from Constraints.
- [ ] Click Restart several times in quick succession on a long-running app. Confirm exactly one process survives (check Task Manager or `Get-Process`) and the indicator agrees.
- [ ] Restart a **test** project's tab and confirm it re-runs under `dotnet test` — a `--project`/MSB1001 error means the verb asymmetry was lost.
- [ ] With a project that has launch profiles, pick a non-default profile, Run, then Restart; confirm the restarted run uses the same profile (a profile-dependent environment variable or console banner is the easiest tell).
- [ ] Restart a tab, then close it while live — confirm the process dies. Then restart another, leave it live, and close the window — confirm no orphaned `dotnet` process survives.
- [ ] Trigger a NuGet restore and confirm its output tab still shows **no** strip at all.
- [ ] Restart a project that fails to launch (e.g. temporarily rename its `.csproj` output or point at a broken project) and confirm the failure lands in the status bar rather than throwing.
