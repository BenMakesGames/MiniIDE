# Live Run Mode (Hot Reload)

## Context
**Current behavior**: Every run is a `dotnet run` (or `dotnet test`) child process started by `RunService.Start`, owned by one output tab. Once launched, the process is frozen against the code that built it: when the agent rewrites a source file, the running app keeps executing the old build until you Restart it. `RunService` has one invocation shape and knows nothing about watching.

**New behavior**: A run tab can run in **live mode**, hosting its app under `dotnet watch` so that source edits made by external tools — the agentic AI, git — are compiled to deltas and applied to the *running* process without restarting it, typically ~2.5–3 s after the write. Live is a per-tab toggle sitting in the strip built by `run-tab-restart-strip.md`; flipping it restarts that tab's run under the other engine. The strip's indicator stops being a bare "Running"/"Exited" word and reports what live mode actually did — reloaded, restarted because the edit was too big to patch, or nothing to apply. Live is offered only where it can work: it is disabled with a specific reason for test projects and for non-Debug configurations.

Everything here rests on `dotnet watch` being the only hot-reload host a third-party tool can drive. There is deliberately **no manual "apply changes" gate** — see Out of scope.

## Prerequisites
- `run-tab-restart-strip.md` — this ticket adds a fourth control to that strip and reuses its restart machinery (toggling live *is* a restart under a different engine) and its superseded-run guard.

## Scope
### In scope
- `RunService` gains a second invocation shape: the same project and `RunSelection`, hosted under `dotnet watch`. The existing non-live shape is unchanged.
- `OutputTabViewModel` gains live state: whether this tab is live, whether live is even available for what it runs, the reason when it is not, and the reload/restart status the indicator shows.
- Classifying `dotnet watch`'s own status lines out of the run's stderr, so they neither read as errors nor go unnoticed.
- The strip's fourth control (a Live toggle) and the richer indicator.
- Eligibility rules: Debug-only, non-test-project-only, with the reason surfaced as a tooltip.
- `docs/` note recording the `dotnet watch` findings below, so the next person doesn't re-derive them.

### Out of scope
- **A manual "apply changes" gate — holding edits until you click.** This was investigated at length and is not achievable; the ticket should not be reopened on a hunch. Two independent findings, both verified empirically against SDK 10.0.204:
  1. `dotnet watch` has no apply-on-demand trigger. Its only console control is Ctrl+R, which is *rebuild and restart*, not apply.
  2. Its hot-reload watcher watches the **directory tree**, not the MSBuild `Watch` item group. A probe that marked source files `Watch="false"` — confirmed excluded via `dotnet watch --list` — still saw them change at runtime and hot-reloaded them anyway. So there is no lever, supported or otherwise, to withhold changes from a live session.

  Visual Studio *can* offer that button (🔥 / Alt+F10, with "apply on file save" as the opt-in) because it owns the Roslyn Edit-and-Continue delta producer. Both halves of that machinery are unshipped for third parties: the producer's public wrapper `Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll` lives only inside the SDK's `dotnet-watch` folder and is `InternalsVisibleTo`-gated to `Microsoft.DotNet.HotReload.Watch`, and the applier `Microsoft.Extensions.DotNetDeltaApplier` is 404 on nuget.org. The EnC engine itself *is* inside `Microsoft.CodeAnalysis.Features` 5.6.0, which this project already references (`IEditAndContinueService`, `DebuggingSession`, `EmitSolutionUpdateAsync` are all in that assembly) — but every one of those types is `internal`, reachable only by private reflection into a package this project bumps deliberately for `.slnx` support. Rejected as a load-bearing hack.
- **Predicting whether a pending edit is hot-reloadable before applying it.** That is the rude-edit analyzer, i.e. the same internal API. Live mode reports what happened; it cannot forecast.
- **Live mode for test projects.** `dotnet watch test` exists, but `--non-interactive` is documented as hot-reload-only and the test path uses positional `dotnet test`. Excluded by eligibility, not merely untested.
- **Starting a run live directly from the toolbar.** The Run split button keeps its two axes (configuration, profile) and always launches non-live; going live costs one toggle, which is one restart per project per session. Adding a third flyout axis is the obvious follow-up if that proves annoying — deliberately deferred (YAGNI), and it keeps `RunSelection` unchanged so the prerequisite ticket's recorded-selection restart stays valid.
- **XAML/`.axaml` hot reload.** Avalonia has no built-in XAML hot reload and the Avalonia team has said a future one would be closed-source. `.axaml` edits will be rude-edit restarts. Third-party options (HotAvalonia) are not in scope.
- **Browser-refresh integration** for web projects (`DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH` and friends). Out of scope; suppress rather than integrate.
- **ANSI escape handling in the output pane**, still out of scope per `output-panel-plain-text`.
- **Persisting the live preference** across app restarts. There is no settings store anywhere in the app.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/run-tab-restart-strip.md` — the prerequisite. Its strip, its restart path, and its superseded-run guard are all reused here.
  - `docs/tickets/complete/2026-08-18 run-configuration-dropdown.md` — `RunSelection`, and why the test-project rule lives in `PlayAsync` rather than being an invariant the UI is trusted to maintain. Its Learnings carry the XAML `--` build failure, which matters more here than anywhere else in the codebase.
  - `docs/tickets/complete/2026-08-18 per-run-output-tabs.md` — per-tab process ownership, and `RunSession.Stop`'s `entireProcessTree: true`, which is already correct for watch's parent/child shape.
  - `docs/tickets/complete/2026-07-05 output-panel-plain-text.md` — the 5000-line cap and the no-ANSI decision, both of which live mode leans on harder.
- **Design docs**: `docs/roslyn.md` (the read-only disk-reconcile law and the pending-set the `SolutionWatcher` feeds); `docs/disk-watching.md`.
- **External**: [dotnet watch reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch) — the authority for options and environment variables.
- **Code anchors**:
  - `RunService.Start` — the verb selection and the `dotnet test` (positional) vs `dotnet run --project` asymmetry, which the live shape must not disturb; `RunSession` and its `Stop`.
  - `OutputTabViewModel` — the state added by the prerequisite ticket, plus `Append` and its UI-thread marshalling.
  - `MainWindowViewModel.PlayAsync` and the extracted start-into-tab path from the prerequisite; `NextRunTab` and its key.
  - `MainWindow.axaml` — the `OutputTabViewModel` strip.
  - `SolutionWatcher` — not consumed here, but read it: MiniIDE runs its own `FileSystemWatcher` over the same tree that `dotnet watch` will be watching, and the two are independent.

## Constraints & Gotchas

All of the following were verified empirically against SDK **10.0.204**, launching `dotnet watch` through MiniIDE's exact `ProcessStartInfo` shape (`UseShellExecute = false`, stdout/stderr/stdin redirected, `CreateNoWindow = true`) with an external process performing the edits.

- **`--non-interactive` is mandatory, and its absence fails silently.** Without it, `dotnet watch` starts the app, prints `Waiting for changes`, and then **never reacts to any file change** — the watcher is dead under redirected stdio, with no error. With it, hot reload works. This is the single most likely way to lose a day on this ticket.
- **Ctrl+R over redirected stdin does nothing.** Writing `0x12` to the child's stdin produced no restart. Combined with the point above (`--non-interactive` explicitly prevents console input being captured), there is no graceful "restart this watch session" channel — the Live toggle and Restart must kill and relaunch, paying the full cold start.
- **In Release, hot reload silently degrades to a full restart.** The *identical* method-body edit that hot-reloaded under `-c Debug` produced `Restart is needed to apply the changes.` under `-c Release`. A live Release run is therefore the worst of both worlds — the app dies and relaunches on *every* agent write, unprompted. This is why Debug-only eligibility is a correctness rule, not a nicety.
- **Rude edits auto-restart with no prompt.** Under `--non-interactive`, an edit too large to patch (adding a method and changing a `const string` both qualified in probes) exits the app, rebuilds, and relaunches. State is lost silently. Surfacing this is the indicator's main job.
- **`dotnet watch` writes all of its own status to stderr**, while the child `dotnet build` output goes to stdout. `RunService` currently prefixes every stderr line with `[err] `, which would tag every hot-reload success as an error. Watch's lines carry a recognizable `dotnet watch :` prefix (with `DOTNET_WATCH_SUPPRESS_EMOJIS=1` set; without it they carry emoji instead) — classify on that. Confirm the exact prefix at implementation time rather than trusting this sentence, and treat it as a heuristic: it distinguishes watch's chatter from the app's own stderr, which has no such prefix.
- **Observed status lines to classify** (emoji suppressed): `Hot reload succeeded.` (prefixed with the project, e.g. `[MiniIde (net10.0)]`), `Restart is needed to apply the changes.`, `No C# changes to apply.`, `File updated: .\<path>`, `Waiting for changes`, `Hot reload session starting.`
- **Cold start is ~9 s**, versus ~4 s for a plain `dotnet run`: build ~4 s, then `Evaluating projects` ~1.2 s, `Loading projects` ~1.4 s, then the session starts. The reload cycle itself is fast (~2.2–2.9 s from write to applied). Users toggling Live should expect the pause.
- **Watch covers the whole project-reference graph.** `dotnet watch --project src/MiniIde/MiniIde.csproj --list` on this repo returned `BenMakesGames.FileGrepper`'s sources too. Live mode therefore reacts to edits well outside the started project.
- **A content-identical write is a no-op**: touching a watched file without changing its bytes yielded `No C# changes to apply.` Watch diffs content, not mtime. This matters because MiniIDE performs its own operation-driven writes (NuGet operations, the rename refactor) — those *do* change content and *will* trigger a live reload, which is correct but worth knowing before it surprises someone mid-rename.
- **XAML comments cannot contain a double hyphen.** This ticket's XAML will want to explain watch's flags; writing `--non-interactive` in a comment fails the build as `AVLN1001: File doesn't contain valid XAML` with an `XmlException` stack that looks structural. Write such flags without leading hyphens in comments.
- **The strip is realized once and reused** across output tabs — all live state must come from the bound tab.
- **`RunSession.Stop`'s `entireProcessTree: true` is already correct** for the watch → app parent/child shape; no change needed, but don't "simplify" it away.
- **Run-tab numbering must not conflate engines.** `NextRunTab` keys on project + configuration + profile. Since a tab can now change engine mid-life, decide whether the engine belongs in that key or in the header — a live run and a plain run of the same selection must not end up indistinguishable in the tab strip.
- **The 5000-line cap bites harder.** Every rude-edit restart re-prints the full build output, so a long live session churns the buffer. The cap stays; just don't be surprised.

## Open Decisions
1. **Where the live shape lives in `RunService`** — a flag on `Start` versus a sibling `StartLive`. Default: a flag, since the verb/argument logic is otherwise identical and duplicating it invites the two to drift. Whichever is chosen, a test project must be unable to reach the watch path.
2. **How the tab starts a live run** — the prerequisite ticket already decides whether the tab holds a start-delegate or a service reference; follow whatever it landed on rather than introducing a second mechanism.
3. **Indicator wording and granularity** — e.g. `Live · reloaded 14:22` versus a running tally (`Live · 7 reloads · 2 restarts`). Default: the last event with a timestamp; the tally is more informative but more state.
4. **Live glyph** — MDI has a `fire` codepoint that matches the Visual Studio convention users will recognise. Default: use it; requires `cd tools && npm run subset` and a codepoint verified against the pinned MDI 6.8.96 cheatsheet.
5. **Whether toggling Live off restarts the app or lets it finish** — restarting is more predictable, letting it run avoids a surprise kill. Default: restart, matching what toggling on does.

## Acceptance Criteria
- [ ] `RunService` can launch a project under `dotnet watch` with `--non-interactive`, passing the same configuration and launch profile the non-live path passes, and setting `DOTNET_WATCH_SUPPRESS_EMOJIS=1` on the child environment.
- [ ] A `ProjectKind.Tst` project can never be launched under the watch path, by the same one-place rule that keeps `-lp` off `dotnet test` — not merely by the UI declining to offer it.
- [ ] Live is unavailable, with a tooltip naming the reason, when the tab's run is a test project, and separately when its `RunSelection.Configuration` is not Debug.
- [ ] Toggling Live on a tab restarts that tab's run under the other engine, in the same tab, keeping its `TabId`.
- [ ] `dotnet watch`'s own status lines are not prefixed `[err] ` in the output document; the app's genuine stderr still is.
- [ ] After an external tool edits a source file in a live run's project graph, and watch reports a successful hot reload, the indicator reflects that a reload occurred — distinctly from reporting a restart.
- [ ] When watch reports `Restart is needed to apply the changes.`, the indicator reports a restart, distinguishably from a successful reload.
- [ ] Stopping a live run, closing its tab, and closing the window each terminate both the `dotnet watch` process and the app process it spawned — no orphans.
- [ ] The strip on a non-live run tab shows the Live control present and un-engaged, never hidden.
- [ ] Every glyph referenced from `ActionIcon` is present in the shipped font subset (`IconFontCoverageTests` passes).

## Implementation

### 1. Teach `RunService` the watch invocation
Extend the service so the same `(ProjectEntry, RunSelection)` can be phrased as a watch invocation. The observed-good argument order is `watch --project <path> -c <config> --non-interactive` plus `-lp <profile>` when there is one — `--project`, `-c` and `-lp` all pass through correctly (verified: a profile's environment variable reached the app, and watch logged `Using launch settings from …`).

Set `DOTNET_WATCH_SUPPRESS_EMOJIS=1` on the child environment, honouring the plain-text output decision from `output-panel-plain-text`; it also makes watch's status lines stably greppable, which step 3 depends on.

Keep the service's existing character: it phrases invocations and hands back a `RunSession`, holding no "current process". The live path returns the same handle type — a watch run is still exactly one process tree that one tab owns.

Guard the test-project case here, not in the caller. The existing code already does this for `-lp` and records why: the verb-to-argument rule lives in one place rather than being an invariant the UI is trusted to maintain. Apply the same reasoning.

### 2. Give the tab its live state
`OutputTabViewModel` needs: whether it is live, whether live is *available* (and the reason if not), and the last live event. Availability is derivable from what the prerequisite ticket already recorded on the tab — the `ProjectEntry` (for the test-project rule) and the `RunSelection` (for the Debug rule) — so it needs no new inputs, only a computed property and a reason string.

The toggle command reuses the prerequisite's restart path: stop, separate, start again — just under the other engine. That is the whole reason this ticket depends on that one, and it means the superseded-run guard already protects the toggle.

### 3. Classify watch's stderr
`RunService.Start` currently wires `ErrorDataReceived` to `log("[err] " + e.Data)`. For a live run that is wrong twice over: watch's status is not an error, and the interesting lines are worth acting on rather than only displaying.

Route a live run's stderr through a classifier that recognises watch's own lines by their prefix, strips the `[err] ` treatment from them, and raises the reload/restart/no-changes outcomes to the tab. Everything unrecognised is still the app's genuine stderr and keeps its prefix.

Verify the exact prefix empirically before hard-coding it — emoji suppression changes it, and the Constraints section records what was observed, not a contract. Prefer a tolerant match (does the line start with watch's marker) over an exact-format parse; watch's wording is not a stable API and a missed classification should degrade to "show the line as-is", never to a crash or a wrong indicator state.

### 4. Extend the strip
Add the Live toggle to the strip from the prerequisite ticket, and replace the plain `RunState` word with the richer indicator. Enablement is a declarative `IsEnabled` binding plus a `ToolTip.Tip` bound to the reason property from step 2 — this is the case that justifies a bindable reason string, where the prerequisite ticket's single literal did not.

Keep the strip's existing sizing so it does not grow taller with a fourth control.

### 5. Add the glyph and re-subset
Per Open Decision 4 — add the constant to `ActionIcon` with its trailing name comment, verify the codepoint against the pinned MDI 6.8.96 cheatsheet (SPUA-A, so `\U000FXXXX`), then `cd tools && npm run subset`. `IconFontCoverageTests` catches a forgotten re-subset.

### 6. Write the findings down
Add a `docs/` note (a new `hot-reload.md`, or a section in an existing runtime doc) recording: `--non-interactive` being mandatory under redirected stdio, the Release degradation, the stderr routing, the observed status strings, the cold-start cost, and the fact that a manual gate is unavailable and why. These cost real experiment time to establish and are invisible from the code.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes, including `IconFontCoverageTests`.
- [ ] Run a console project in Debug, toggle Live on, and wait for `Waiting for changes` in the output. Then have Claude (or any external editor) change a method body's return value. Confirm the running app's output changes **without** the app restarting, within a few seconds, and that the indicator reports a reload.
- [ ] With that same live run going, have the agent **add a method**. Confirm the app restarts, and that the indicator says so rather than the restart being visible only as build spam in the scrollback.
- [ ] Confirm no line in the output document reads `[err] dotnet watch : …`.
- [ ] Deliberately have the app write to stderr (`Console.Error.WriteLine`) during a live run and confirm that line **is** still marked as error output — the classifier must not swallow genuine stderr.
- [ ] Switch the configuration to Release, start a run, and confirm the Live control is disabled and its tooltip names the Debug requirement.
- [ ] Select a test project and confirm the Live control is disabled with the test-project reason. Then confirm no code path can launch it under watch (a `dotnet watch test` invocation appearing in output means the step-1 guard was skipped).
- [ ] Start a live run, then use Task Manager or `Get-Process dotnet` to note both the watch process and the app process. Press Stop; confirm **both** disappear. Repeat, closing the tab instead. Repeat, closing the window instead.
- [ ] Toggle Live off on a live run and confirm the app comes back under the plain engine and stops reacting to edits.
- [ ] Run MiniIDE itself live (Debug) and edit a `.axaml` file — confirm it produces a restart, not a reload, matching the documented Avalonia limitation.
- [ ] Start a live run of a project that references another project in the solution; edit the *referenced* project's source and confirm the reload still fires — watch covers the reference graph.
- [ ] Leave a live run going and perform a NuGet operation or a rename refactor from within MiniIDE; confirm the resulting operation-driven writes reload the app rather than doing anything unexpected.
