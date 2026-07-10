# Problems Panel

## Context
**Current behavior**: The bottom `TabControl` (`BottomTabs`) has three hand-written tabs — Find, Output, NuGet. Compiler errors and warnings are never captured as structured data; they only appear as unparsed text lines that `dotnet run`/`dotnet test` incidentally emit into the Output panel. There is no diagnostics model, no problems list, and no squiggle rendering anywhere.

**New behavior**: A new "Problems" tab lists the solution's compiler errors and warnings, populated **on demand** when the user clicks a Refresh button — nothing is captured automatically or kept live. Diagnostics come from the already-loaded Roslyn `MSBuildWorkspace` in `WorkspaceService` (`GetCompilationAsync().GetDiagnostics()`), filtered to Error + Warning severities. The results render as a two-level `TreeView` with a toggle between two grouping modes: **by file** (file → issues in that file) and **by diagnostic code** (e.g. `CS8602` → the occurrences across files). Double-clicking an individual issue jumps to that file/line/column in the editor, reusing the existing navigation path. First refresh may pay the Roslyn cold-start, so the button disables and progress is shown while analysis runs.

## Prerequisites
- None. (The workspace-load infrastructure this builds on — `WorkspaceService` — already exists.)

## Scope
### In scope
- New `ProblemItem` model record (the structured diagnostic).
- New `WorkspaceService.GetDiagnosticsAsync` that compiles each project and returns filtered, de-duplicated diagnostics as `ProblemItem`s (Roslyn types stay inside the service).
- New `ProblemsViewModel` (Refresh command, grouping-mode toggle, tree build, counts, busy/empty state).
- New `<TabItem Header="Problems">` in `BottomTabs` with the tree UI + toolbar, bound to the VM.
- Wiring in `MainWindowViewModel` (own the VM, inject the open-callback through the existing `RequestOpen` event).

### Out of scope
- **Third-party / analyzer diagnostics** (StyleCop, Roslynator, etc.) and **NuGet/MSBuild-level errors**. `Compilation.GetDiagnostics()` returns compiler diagnostics only (all `CS####`, including nullable warnings). Analyzer diagnostics need `CompilationWithAnalyzers` — a deliberate future extension, not this pass.
- **Reflecting unsaved editor buffers.** The workspace analyzes on-disk text; unsaved edits are not seen. Pushing live buffers into the workspace is the separate pending `sync-open-buffers-to-workspace` work — this feature composes with it, does not duplicate it. Do **not** auto-save on refresh.
- **Info/Hidden diagnostics.** Only Error and Warning are surfaced.
- **Live/automatic refresh, squiggles in the editor gutter, quick-fixes.** None of these; Refresh is the only trigger and the panel is the only surface.
- Tab-header live counts (see Open Decisions) and any grouping mode beyond the two specified.

## Relevant Docs & Anchors
- `docs/roslyn.md` — workspace split rationale and the cold-start cost warning (per-project compile on load; report progress). Directly relevant to why Refresh is on-demand and shows progress.
- `docs/global-find.md` — the Find panel's design; its selection→open navigation is the exemplar to mirror.
- **Panel-add pattern** — there is no panel abstraction. Adding a panel = a VM property on `MainWindowViewModel` + a hand-written `<TabItem>` in `MainWindow.axaml` bound via `DataContext`. The Find and NuGet tabs in the `BottomTabs` `TabControl` are the templates.
- **`WorkspaceService`** (`src/MiniIde/Services/WorkspaceService.cs`) — `EnsureLoadedAsync(solutionPath, ct)` (lazy load, idempotent), `_solution.Projects`, the `Progress` event (already routed to the window status bar via `Workspace.Progress += ...` in the `MainWindowViewModel` ctor), and `GoToDefinitionAsync` — whose `loc.GetLineSpan()` → `(SourceTree.FilePath, StartLinePosition.Line + 1, StartLinePosition.Character + 1)` mapping is the **exact pattern** to reuse for turning a `Diagnostic.Location` into `(file, line, col)`.
- **Navigation** — `MainWindowViewModel.RequestOpen` event → `MainWindow.OpenHit(file, line, col)` in `MainWindow.axaml.cs` (opens/focuses the tab, sets caret via `Document.GetOffset(line, col)`, scrolls). The Find VM receives this as a `Func<string,int,int,Task>` open-callback wired in the `MainWindowViewModel` ctor. Reuse the identical wiring for Problems.
- **`FindResultsViewModel`** (`src/MiniIde/ViewModels/FindResultsViewModel.cs`) — mirror for VM shape: `[RelayCommand]` async refresh, `_cts` cancel-prior pattern (new refresh cancels the previous), `IsSearching`/`Status` observable properties, the injected open-callback, `Dispatcher.UIThread.Post` to mutate the bound collection off the streaming path.
- **`Models/FindHit.cs`** — `record FindHit(string File, int Line, int Column, string Preview)` is the exemplar record shape for `ProblemItem`.
- **`ShowOutput()`** in `MainWindow.axaml.cs` — optional exemplar if a "reveal Problems tab" helper is wanted; not required for this ticket.

## Constraints & Gotchas
- **Multi-targeted projects yield one `Compilation` per target framework**, so the same diagnostic can appear more than once. De-duplicate the collected `ProblemItem`s (by Id + File + Line + Column + Message) before returning.
- **Suppressed diagnostics**: `GetDiagnostics()` includes pragma/`#pragma warning disable`-suppressed entries with `IsSuppressed == true`. Filter those out so the panel matches what a build would surface.
- **Locationless diagnostics**: some diagnostics have `Location.None` / `!Location.IsInSource` (e.g. a missing assembly reference). These have no file to navigate to. Keep them (they are real errors) but give them a home under a `(No file)` group in by-file mode, and make double-click a no-op for them.
- **Roslyn types must not leak into the ViewModel or Models.** `WorkspaceService.GetDiagnosticsAsync` maps `Diagnostic` → `ProblemItem` internally and returns the model records, consistent with how `GoToDefinitionAsync`/`FindReferencesAsync` return plain tuples, not Roslyn objects.
- **Cost & threading**: compiling every project is the expensive step and can be slow on a first, cold load (see `docs/roslyn.md`). Run it off the UI thread (it is already `async`), honor a `CancellationToken`, and report per-project progress. The existing `Workspace.Progress` event already forwards to the window status bar, so emitting progress there is enough — no new plumbing required for status.
- **Do not spawn `dotnet build`.** This feature is purely in-process Roslyn; there is no external process to contend with a running app.
- **`OpenFileAsync` ignores its line/col args** — caret positioning is done by `OpenHit` in the code-behind after the tab opens. Route navigation through `RequestOpen`/`OpenHit`, not directly through `OpenFileAsync`.

## Open Decisions
1. **Tree node classes** — whether leaves are the raw `ProblemItem` record with a second `DataTemplate`, or a thin leaf-node VM carrying a precomputed display string. Default: a small leaf-node type, so the display text can differ by grouping mode (in by-file mode the parent already shows the file, so the leaf reads `line — message`; in by-code mode the leaf reads `relativePath(line) — message`). Implementer's call.
2. **Group-header composition (by-code mode)** — e.g. `CS8602 — dereference of possibly-null (12)` using the first occurrence's message, vs. just `CS8602 (12)`. Default: include a representative message + count.
3. **Top-level ordering** — errors-before-warnings then path, vs. alphabetical, vs. by descending count. Default: errors first, then by path (by-file) / by code (by-code).
4. **Tab-header live counts** (e.g. `Problems (2⛔ 7⚠)`). Default: out — show the counts in a summary line inside the panel instead; revisit if wanted.
5. **Grouping toggle control** — segmented buttons vs. two radio buttons vs. a combo. Default: two radio buttons or a segmented pair in the panel toolbar; whichever reads cleanest next to the Refresh button.

## Acceptance Criteria
- [ ] `BottomTabs` contains a fourth `TabItem` headed "Problems", bound to a `Problems` property of type `ProblemsViewModel` on `MainWindowViewModel`.
- [ ] `Models/ProblemItem.cs` exists as a record carrying at least: diagnostic id (e.g. `CS8602`), severity (Error or Warning), message, file path (nullable/empty for locationless), line, and column.
- [ ] `WorkspaceService` exposes an async method returning `IReadOnlyList<ProblemItem>` that, for a loaded solution, includes exactly the Error + Warning compiler diagnostics of all projects, excludes Info/Hidden and `IsSuppressed` diagnostics, and contains no duplicate (id, file, line, column, message) tuples.
- [ ] Clicking Refresh with no solution open does nothing (the command is disabled); with a solution open it analyzes and populates the tree.
- [ ] The panel offers two grouping modes; toggling between them **re-groups the already-analyzed results without re-running analysis**.
- [ ] In by-file mode, every top-level node is a file (or the `(No file)` bucket) and expanding it lists that file's issues; in by-code mode, every top-level node is a diagnostic code and expanding it lists the occurrences across files.
- [ ] Double-clicking an individual issue with a source location opens/focuses that file and places the caret at the diagnostic's line/column; double-clicking a locationless issue does nothing.
- [ ] While analysis is running the Refresh command is disabled; when it completes the panel reflects the new results (or an explicit empty state when there are zero problems).
- [ ] Starting a new Refresh while one is in progress cancels the prior analysis (no interleaved/duplicated results).

## Implementation

### 1. Add the `ProblemItem` model
Create `src/MiniIde/Models/ProblemItem.cs` mirroring the `FindHit` record style. Carry the diagnostic id, a severity value (introduce a small `ProblemSeverity { Error, Warning }` enum in Models rather than leaking `DiagnosticSeverity`), the message, the file path (allow null/empty for locationless), line, and column. Keep it a plain immutable record — it is data handed from the service to the VM.

### 2. Add diagnostics collection to `WorkspaceService`
Add an async method (e.g. `GetDiagnosticsAsync(CancellationToken ct = default)`) that returns `IReadOnlyList<ProblemItem>`. Guard: if `_solution` is null, return empty. Iterate `_solution.Projects`; for each, emit a progress line via the existing `Progress` event (e.g. `"Analyzing <projectName>"`), `await project.GetCompilationAsync(ct)`, and enumerate `compilation.GetDiagnostics(ct)`. Keep only diagnostics whose `Severity` is `Warning` or `Error` and whose `IsSuppressed` is false. Map each to a `ProblemItem`: for the location, reuse the `GoToDefinitionAsync` pattern — when `Location.IsInSource`, take `GetLineSpan()` and produce `(SourceTree.FilePath, StartLinePosition.Line + 1, StartLinePosition.Character + 1)`; otherwise mark it locationless (null/empty file). De-duplicate across projects (a `HashSet` keyed on id+file+line+col+message) to collapse the multi-target duplicates. Honor `ct` between projects. Do not touch the existing symbol-nav methods.

### 3. Create `ProblemsViewModel`
Create `src/MiniIde/ViewModels/ProblemsViewModel.cs` as a `partial ViewModelBase`, modeled on `FindResultsViewModel`. Constructor injects `WorkspaceService`, `SolutionService`, and a `Func<string,int,int,Task>` open-callback (same three dependencies pattern Find uses). Hold the flat last-analysis result list privately, an observable `Groups` collection bound by the tree, observable `IsBusy`/`Status` and `ErrorCount`/`WarningCount`, an observable grouping-mode property (enum `ByFile`/`ByCode`), and a `_cts` for cancel-prior.
- `[RelayCommand]` `RefreshAsync`: bail if `SolutionService.SolutionPath` is null; cancel any prior `_cts`; set `IsBusy`; `await Workspace.EnsureLoadedAsync(SolutionPath, ct)` then the step-2 method; store the flat results; rebuild the tree; update counts; set `Status`. Mirror Find's try/catch for `OperationCanceledException` and general exceptions. Gate the command's `CanExecute` on "solution open and not busy".
- A private `RebuildTree()` that groups the stored flat list per the current mode and repopulates `Groups`. The grouping-mode change hook calls `RebuildTree()` **without** re-analyzing.
- By-file grouping: group on file path (locationless → a single `(No file)` group), children ordered by line. By-code grouping: group on diagnostic id, header per Open Decision #2, children across files. Apply top-level ordering per Open Decision #3.
- Leaves must carry file/line/column (and a display string per Open Decision #1) so the view can navigate. Reuse the injected open-callback for activation, exactly as Find calls `_openHit`.

### 4. Own the VM in `MainWindowViewModel`
In `MainWindowViewModel`, add `public ProblemsViewModel Problems { get; }` and construct it in the ctor right after `Find`, injecting `Workspace`, `Solution`, and the same open-callback lambda Find uses (`async (f, l, c) => { if (RequestOpen is not null) await RequestOpen(f, l, c); }`). No other MainWindowViewModel changes are required — `RequestOpen` is already wired to `OpenHit` in the code-behind.

### 5. Add the Problems tab UI
In `src/MiniIde/Views/MainWindow.axaml`, add a `<TabItem Header="Problems">` to `BottomTabs` (alongside Find/Output/NuGet) with `DataContext="{Binding Problems}"`. Lay out a small toolbar row (Refresh button bound to the refresh command; the grouping-mode toggle per Open Decision #5; a summary line showing error/warning counts + `Status`) above a `TreeView` bound to `Groups`. Use a `HierarchicalDataTemplate` for group nodes (its `ItemsSource` = the group's children) and a `DataTemplate` for leaves, keyed by `DataType` so the tree renders both. Show a placeholder for the empty/first-run state ("Press Refresh to analyze the solution" before first run, "No problems found" after a zero-result refresh) — a `TextBlock` gated on the results being empty is sufficient.

### 6. Wire double-click navigation
Double-click on a leaf must invoke the open-callback with the leaf's `(file, line, col)`; locationless leaves do nothing. Follow the Find navigation contract (`RequestOpen` → `OpenHit`) — the leaf hands its tuple to the VM's open-callback. Tree double-click is a view concern: attach a `DoubleTapped` handler in `MainWindow.axaml.cs` that resolves the tapped element's `DataContext`, and if it is a leaf node with a source location, calls into the VM to activate it (or bind an activation command on the leaf). Single-click / expander behavior stays default (expand/collapse groups).

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds (XAML compiles, so the new tab, bindings, and templates resolve).
- [ ] Launch the app, open a solution that has known warnings (e.g. a project with nullable enabled and an obvious possibly-null dereference, plus an unused variable). Click Problems → Refresh. Confirm the tree populates with the expected errors/warnings and the summary line shows non-zero counts.
- [ ] Introduce a hard compile error (e.g. delete a `;`), **save**, Refresh — the error appears with the correct file/line. Fix and save, Refresh — it disappears.
- [ ] Toggle from by-file to by-code and back **without** clicking Refresh — the same issues re-group instantly (by-code shows `CS####` nodes; by-file shows file nodes with a `(No file)` bucket if any locationless diagnostics exist). Confirm no re-analysis runs (status doesn't return to "analyzing").
- [ ] Double-click an issue under a file group — the correct editor tab opens/focuses with the caret at the diagnostic's line/column and scrolled into view. Repeat from by-code mode. Confirm a locationless issue (if present) does nothing on double-click.
- [ ] On a large/cold solution, click Refresh and confirm the button disables while analysis runs and the status bar shows per-project progress; clicking Refresh again mid-run cancels and restarts cleanly (no duplicated tree entries).
- [ ] Refresh with no solution open — nothing happens (button disabled). Refresh a clean solution with zero problems — the empty state ("No problems found") shows.
- [ ] Regression: Find, Output, and NuGet tabs and the run/F5 flow behave exactly as before; opening a file via a Find result still works (shared `RequestOpen`/`OpenHit` path untouched).
- [ ] Regression: a multi-targeted project's warnings appear once, not once per target framework (de-dup working).

## Learnings

### Architectural decisions
- **Open Decisions resolved to their defaults**, all confirmed by the code:
  1. *Tree node classes* — introduced thin `ProblemLeaf`/`ProblemGroup` VM types (`ViewModels/ProblemNodes.cs`) rather than templating the raw `ProblemItem` twice. Each leaf carries a precomputed, mode-specific `Display` string so the by-file leaf leads with `{code} Ln {n}: {msg}` (parent already shows the file) and the by-code leaf leads with `{relPath}({l},{c}): {msg}` (parent already shows the code).
  2. *By-code header* — `{Id} — {representative message} ({count})`, representative = first occurrence after ordering.
  3. *Ordering* — errors-before-warnings (`HasError ? 0 : 1`) then path (by-file) / code (by-code); children by line/col (by-file) or file/line/col (by-code).
  4. *Tab-header counts* — omitted; counts live in the in-panel `Status` line (`"{E} error(s), {W} warning(s)"`).
  5. *Grouping toggle* — two `RadioButton`s bound to bool facade properties (`IsByFile`/`IsByCode`) over the `Grouping` enum; `OnGroupingChanged` re-notifies both so the other radio unchecks. Avoids an enum↔bool converter for a two-value toggle.
- **`ProblemSeverity { Error, Warning }` enum in Models** keeps `DiagnosticSeverity` (Roslyn) out of the VM/Models, matching how `GoToDefinitionAsync`/`FindReferencesAsync` return plain tuples. Verified: zero `Microsoft.CodeAnalysis` references in `ProblemsViewModel`/`ProblemItem`.

### Problems encountered
- **AC8 vs AC9/Test-Plan tension.** AC8 + Implementation step 3 say the Refresh command is *disabled while busy* (`CanRefresh => !IsBusy && SolutionPath != null`), but AC9 and the Test Plan talk about "clicking Refresh again mid-run cancels the prior analysis." With the button disabled during a run, the user can't double-trigger via the button — which already guarantees "no duplicated tree entries." Resolved by keeping the busy-disable (the explicit, twice-stated behavior) **and** the cancel-prior `_cts` machinery for correctness. Flag for future: if we ever want visible mid-run cancel/restart, drop `!IsBusy` from `CanRefresh` and show busy via a spinner instead.
- **Superseded-run state race.** The naive `finally { IsBusy = false; }` + `catch { Status = ... }` let a *cancelled* prior run reset `IsBusy`/`Status` after a newer run had taken over. Guarded every shared-state mutation in the catch/finally with `ReferenceEquals(_cts, cts)` (capture the run's own CTS locally) so only the current run touches VM state. This is what actually makes AC9 hold regardless of how a second refresh is triggered.

### Interesting tidbits
- **Multi-target dedup falls out of `_solution.Projects`.** A multi-targeted project surfaces as *one `Project` per TFM*, each with its own `Compilation` and thus duplicate diagnostics. De-dup on `id|file|line|col|message` via a `HashSet<string>` collapses them.
- **`Compilation.GetDiagnostics()` includes suppressed entries** (`IsSuppressed == true` for `#pragma warning disable`); filter them so the panel matches a real build.
- **Locationless diagnostics** (`!Location.IsInSource`, e.g. missing assembly ref) map to `File == null` and land in a `(No file)` by-file bucket; `ProblemLeaf.HasLocation` gates navigation to a no-op.
- **`Diagnostic.Location` → `(file, line, col)`** reuses the exact `GoToDefinitionAsync` mapping: `GetLineSpan()` → `SourceTree.FilePath`, `StartLinePosition.Line + 1`, `.Character + 1` (Roslyn is 0-based; the editor's `Document.GetOffset` is 1-based).
- **TreeView with heterogeneous nodes**: put a `TreeDataTemplate DataType="vm:ProblemGroup" ItemsSource="{Binding Children}"` and a plain `DataTemplate DataType="vm:ProblemLeaf"` in `TreeView.DataTemplates` (not `ItemTemplate`). Avalonia resolves per-node by runtime type — same mechanism the main `TabControl.DataTemplates` uses for editor vs image tabs.

### Related areas affected
- `MainWindowViewModel.OpenSolutionAsync` gained one line: `Problems.NotifyCanRefreshChanged()` — because `SolutionService.SolutionPath` isn't observable, the Refresh button's `CanExecute` won't re-evaluate on solution load without an explicit nudge. Same reason a future NuGet/Find enablement would need it.
- Navigation reuses the untouched `RequestOpen` → `OpenHit` path; no changes to the shared Find navigation contract.

### Rejected alternatives
- **`DoubleTapped` for leaf activation** (suggested in the ticket) — rejected per `docs/avalonia.md`: it drops presses near row edges on templated `TreeViewItem`s. Mirrored the solution tree's tunnel `PointerPressed` + `ClickCount == 2` pattern instead, hooked once via `AttachedToVisualTree` (the tree lives in a bottom `TabItem`, so it isn't in the visual tree at window-ctor time).
- **Spawning `dotnet build`** — out of scope; diagnostics come purely in-process from the already-loaded Roslyn `MSBuildWorkspace`.
- **Unit tests** — no test project exists and the Test Plan is deliberately manual UI verification; per implement-ticket guidance, didn't invent low-value tests.
