# Headless View Tests for the Tab↔Editor Binder

## Context
**Current behavior**: `MiniIde.Tests` does not reference `MiniIde` at all — its `.csproj` has no `ProjectReference`, and its single test (`IconFontCoverageTests`) regex-scans `src/MiniIde/**/*.cs` as *text* and probes the shipped TTF with SkiaSharp. Not one line of application code is executed by the test suite. There is no CI (`.github/` does not exist).

Consequently the entire view layer is verified by hand. The `unify-tab-editor-binding` ticket's Test Plan is fourteen manual steps, and one of its core invariants — *"after detach → re-attach, exactly one `RoslynColorizer` is in `LineTransformers`"* — is **structurally unverifiable by eye**: two colorizer instances paint the same brushes, so double-colorized text looks identical to correctly-colorized text. That regression can only ever be caught by inspecting object state.

**New behavior**: `MiniIde.Tests` runs the real `MainWindow` and real `MainWindowViewModel` in-process against Avalonia's headless windowing platform, with no visible window, and asserts on the binder's actual state. The six invariants below become automated; the manual Test Plan shrinks to the things headless genuinely cannot observe. No production behavior changes — the only production edit is one `InternalsVisibleTo` line.

## Prerequisites
- `docs/tickets/complete/2026-07-11 unify-tab-editor-binding.md` — created `TabEditorBinder`, `CodeEditorState`, `RoslynColorizer` and the `DocumentTabViewModel` contract. This ticket tests exactly that machinery; every assertion below targets a symbol it introduced.

## Scope
### In scope
- **`src/MiniIde.Tests/MiniIde.Tests.csproj`**: add a `ProjectReference` to `MiniIde.csproj` and a `PackageReference` to `Avalonia.Headless.XUnit`.
- **New test-host wiring** in `MiniIde.Tests`: an `AvaloniaTestApplication` app-builder, and assembly-level test-parallelization disable.
- **New test file(s)** in `MiniIde.Tests` covering the six invariants in Acceptance Criteria.
- **`src/MiniIde/MiniIde.csproj`**: one `InternalsVisibleTo` entry for `MiniIde.Tests` (the *only* production change).
- **New `docs/testing.md`**: how to run the tests, the headless setup, and — importantly — the list of things headless *cannot* cover, so nobody assumes they are.
- **`docs/stack.md`**: gains a testing line (it currently has none).

### Out of scope
- **A second test project.** These live in the existing `MiniIde.Tests` alongside `IconFontCoverageTests`. One more csproj buys separation we have no use for.
- **Real-pixel rendering.** No `Avalonia.Skia` / `UseHarfBuzz` / `UseHeadlessDrawing = false`. Every assertion here reads object state, not pixels or font metrics. Add Skia only if a future test genuinely needs a framebuffer.
- **Tail-follow, and the 200 ms debounce.** Both proven not observable under headless — see Constraints. They stay manual.
- **Service seams / interfaces / DI.** There are zero interfaces in the codebase today and this ticket adds none. It needs none — see Constraints.
- **View-model, service, or `Program`/`App` tests.** Tab dedup, the live-run kill-race, `SolutionService`, `NuGetService` — all a separate ticket if ever wanted.
- **CI.** None exists; setting it up is its own ticket. This ticket only makes `dotnet test MiniIde.slnx` meaningful.
- **Fixing `docs/stack.md`'s stale ".NET 9"** (the projects are `net10.0`). Noted, not bundled.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/avalonia.md` — the three bullets on `TabControl` control-reuse, unbind-on-detach, and the per-kind binding registry. These are the invariants under test; the tests are their executable form.
  - `docs/avaloniaedit.md` — `TextEditor.Document` is a CLR property (why a binder exists at all), and the rule that `editor.SyntaxHighlighting` and a `DocumentColorizingTransformer` must never both be live.
- **Related tickets** (read the Constraints/Learnings):
  - `docs/tickets/complete/2026-07-11 unify-tab-editor-binding.md` — the machinery under test. Its Learnings note that a double-added colorizer is invisible on screen; that is this ticket's reason to exist.
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — the two-output-tabs regression (the original one-shot-`HashSet` binder bug) that AC 4 pins down.
  - `docs/tickets/complete/2026-07-05 xml-json-syntax-highlighting.md` — the color-bleed hazard that AC 5 pins down.
- **Code anchors**:
  - `TabEditorBinder<TTab, TState>` (`Views/TabEditorBinder.cs`) — `Bind` / `Unbind` / `EditorFor` / `StateFor`, and the two per-kind wirings beneath it.
  - `MainWindow`'s `_codeEditors` / `_outputEditors` fields and the six attach/detach/DataContextChanged forwarders.
  - `MainWindowViewModel` — its **parameterless** constructor, and the public `Tabs` / `ActiveTab` members the tests drive.
  - `OutputTabViewModel.Append` / `Clear` / `MaxLines`.
  - `src/MiniIde/Program.cs` `BuildAvaloniaApp` — read it to see why it **cannot** be reused here (see Constraints).
  - `src/MiniIde.Tests/IconFontCoverageTests.cs` — the existing (and only) test; it must keep passing.

## Constraints & Gotchas

These were each established empirically against this codebase, not taken from documentation. Several contradict what the Avalonia docs and 11.x-era tutorials say.

- **Avalonia 12 targets xUnit v3 — most tutorials are wrong.** `Avalonia.Headless.XUnit` 12.0.5 targets `net10.0` and depends on `xunit.v3.extensibility.core >= 3.2.2`. `MiniIde.Tests` already has `xunit.v3` 3.2.2, `net10.0`, and `OutputType=Exe`, so it is a drop-in. Any guide showing `xunit.core` 2.x, or a separate `Avalonia.Headless.XUnit.v3` package, is describing Avalonia 11 — the v3 migration happened *in* 12. Do not go hunting for a v3-specific package; it does not exist.
- **`Program.BuildAvaloniaApp()` is not the seam.** It hardwires `UsePlatformDetect()` + `Win32PlatformOptions`. The test host must build its own `AppBuilder` over the real `MiniIde.App` and call `UseHeadless(...)` instead. This is normal; just don't try to reuse the production builder.
- **`App.OnFrameworkInitializationCompleted` does nothing under headless** — it only constructs `MainWindow` when `ApplicationLifetime is IClassicDesktopStyleApplicationLifetime`, which headless is not. That is convenient, not a problem: each test constructs `new MainWindow { DataContext = new MainWindowViewModel() }` itself and calls `Show()`. **`Show()` is required** — without it the `TabControl` never realizes a `TextEditor`.
- **Tests must use `[AvaloniaFact]`, not `[Fact]`.** A plain `[Fact]` runs on an xUnit worker thread with no Avalonia dispatcher, so constructing any `Visual` throws. Headless also does not support parallel execution — disable test parallelization assembly-wide. This affects `IconFontCoverageTests` not at all (it is one test).
- **No DI is needed, despite there being none.** `MainWindowViewModel`'s constructor only *allocates* its seven services: `WorkspaceService` doesn't touch MSBuild until `EnsureLoadedAsync`, `SyntaxHighlightService`'s ctor is a cheap `AdhocWorkspace`, and `RunService` spawns nothing until `RunAsync`. So `new MainWindowViewModel()` is safe in a test process, and `Tabs` / `ActiveTab` / `OutputTabViewModel.Append` are public — the whole tab/binder machine is drivable **without loading a solution or running a process**. Do not introduce interfaces or constructor injection for this ticket.
- **Never assert on `LineTransformers.Count`.** AvaloniaEdit puts its **own** transformers in there, and the set *varies by highlight mode*. Observed on a real bound editor: C# mode → `[SelectionColorizer, RoslynColorizer]`; XML mode → `[HighlightingColorizer, SelectionColorizer, RoslynColorizer]` (AvaloniaEdit adds `HighlightingColorizer` when `SyntaxHighlighting` is set, and removes it when nulled). Always assert on `LineTransformers.OfType<RoslynColorizer>().Count()`. This is why `InternalsVisibleTo` is worth its one line — the alternative is matching `GetType().Name` strings that rot silently on rename.
- **The colorizer stays in `LineTransformers` during XML/JSON mode** — it is `Clear()`ed (emptied of spans), not removed. So AC 5 must assert on `editor.SyntaxHighlighting`, not on colorizer presence.
- **Tail-follow is NOT observable headlessly — do not try to test it.** `editor.ScrollToLine(...)` does not land under the headless windowing platform. Confirmed: streaming 60 `Append`s with `Dispatcher.UIThread.RunJobs()` + `AvaloniaHeadlessPlatform.ForceRenderTimerTick()` pumped between *every one* leaves `Offset.Y` at 0 while `Extent.Height` correctly grows to 1271 against a 285 viewport. Manually assigning `ILogicalScrollable.Offset` *does* stick, so it is specifically `ScrollToLine` that no-ops. **This is not an app bug** — tail-follow demonstrably works in the real window. It is a platform limitation. Say so in `docs/testing.md`; do not let a reader assume tail-follow is covered.
- **The 200 ms debounce is not observable either** — the binder kicks reclassification off as fire-and-forget (`_ = DebouncedRefreshAsync(...)`), so there is no task to await and no completion to observe. Leave it manual. Do **not** reshape production code to make it testable.
- **Adding the `ProjectReference` couples `dotnet test` to the app build.** A running MiniIde locks its own DLL, so `dotnet test` then fails with `MSB3027` / `MSB3021` ("being used by another process"). This is the same build-lock gotcha as the previous ticket, now reaching the test loop. `scripts/stop.ps1` is the fix; document it in `docs/testing.md`.
- **Expect the three pre-existing warnings** to now surface during `dotnet test` (they come from building `MiniIde`): CS0618 `Workspace.WorkspaceFailed`, IL3000 in `SyntaxHighlightService`, AVLN5001 `TextBox.Watermark`. Introduce no new ones.
- **Give the window an explicit `Width`/`Height`.** The headless screen is a fake 1920×1280 and a default-sized window yields a degenerate viewport. Something like 900×600 is enough for the `TabControl` to lay out.

## Open Decisions
1. **Test file organization** — one `TabEditorBinderTests.cs`, or split code-tab and output-tab cases into two files. Default: **one file**; six tests is not a lot. Split if it reads better.
2. **`InternalsVisibleTo` placement** — an `<InternalsVisibleTo Include="MiniIde.Tests" />` MSBuild item in `MiniIde.csproj` vs. an `[assembly: InternalsVisibleTo]` attribute in a C# file. Default: **the MSBuild item** — no new file, and it sits next to the thing it's scoping.
3. **Shared setup helper** — a small `Launch()` returning `(vm, window)` plus a `Pump()` wrapping `RunJobs()`/`ForceRenderTimerTick()`, vs. inlining in each test. Default: **extract both**; every test needs them.
4. **How to reach the live editor** — `window.GetVisualDescendants().OfType<TextEditor>().Single()` works (only the realized control is in the tree) but is a visual-tree scan, which is precisely the smell the prerequisite ticket removed from production. It's acceptable *in a test* (the test is the outside observer and has no registry access). Default: **use it**, with a comment noting why it's fine here and not in production.

## Acceptance Criteria
- [ ] `MiniIde.Tests` references `MiniIde` and `Avalonia.Headless.XUnit`; `dotnet test MiniIde.slnx` builds and runs both the new tests and the pre-existing `IconFontCoverageTests`, all green, with no new warnings.
- [ ] Tests execute against the real `MainWindow` + real `MainWindowViewModel` under the headless platform — no mocks, no stub window, no solution loaded, no process spawned.
- [ ] **Bind on activate**: activating an `EditorTabViewModel` leaves the realized `TextEditor`'s `Document` reference-equal to that tab's `Document`.
- [ ] **No duplicate colorizer**: after code-tab → output-tab → code-tab, the realized code editor's `LineTransformers` contains **exactly one** `RoslynColorizer`.
- [ ] **Unbind on detach**: a `TextEditor` captured while bound has **zero** `RoslynColorizer`s in its `LineTransformers` after the active tab switches to an output tab. (Same check also demonstrates the two tab kinds realize *different* `TextEditor` instances.)
- [ ] **Two output tabs stay distinct**: with two `OutputTabViewModel`s, activating each points the realized editor at that tab's own `Document`, and switching back to the first restores its buffer — not the second's.
- [ ] **Highlight paths are mutually exclusive**: on a `.cs` tab `editor.SyntaxHighlighting` is null; on a `.csproj` tab it is the XML definition; switching back to the `.cs` tab nulls it again.
- [ ] **Output line cap**: appending 6000 lines to an `OutputTabViewModel` leaves `Document.LineCount` at 5000.
- [ ] `docs/testing.md` exists and states explicitly which behaviors headless **cannot** verify (tail-follow, the debounce, the Ctrl+O picker, F5 runs, Go-to-definition) and that those remain manual.

## Implementation

### 1. Wire the test project to the app
Give the tests something to test. In `MiniIde.Tests.csproj` add a `ProjectReference` to `src/MiniIde/MiniIde.csproj` and a `PackageReference` to `Avalonia.Headless.XUnit` version `12.0.5` (matching the app's Avalonia 12.0.5). Nothing else in the csproj needs to change — `net10.0`, `OutputType=Exe`, and `xunit.v3` 3.2.2 are already exactly what Avalonia 12's headless xUnit package requires. Confirm the existing `IconFontCoverageTests` still passes; with the app now in the build graph, close any running MiniIde first (see Constraints).

### 2. Expose internals to the tests
Add an `InternalsVisibleTo` for `MiniIde.Tests` to `MiniIde.csproj` (Open Decision 2). This is the ticket's only production change. It exists so tests can name `RoslynColorizer` — see the Constraint on `LineTransformers`.

### 3. Stand up the headless test host
Add an `AvaloniaTestApplication` assembly attribute pointing at a small app-builder type whose `BuildAvaloniaApp()` configures the **real** `MiniIde.App` with `UseHeadless(...)` and default `AvaloniaHeadlessPlatformOptions` (i.e. headless drawing on, no Skia — see Out of scope). Disable test parallelization assembly-wide in the same place. Do not reuse `Program.BuildAvaloniaApp` (see Constraints).

### 4. Add the shared test scaffolding
A helper that constructs `new MainWindowViewModel()` and a `MainWindow` bound to it, sizes the window (see Constraints), calls `Show()`, and hands back both; plus a pump helper wrapping `Dispatcher.UIThread.RunJobs()` and `AvaloniaHeadlessPlatform.ForceRenderTimerTick()`, and a temp-file helper for the `.cs`/`.csproj` fixtures that `EditorTabViewModel` reads via `File.ReadAllText`. Per Open Decision 4, resolving the live editor is a visual-tree scan.

### 5. Write the code-tab binder tests
Cover, per Acceptance Criteria: bind-on-activate (`Document` identity); no duplicate colorizer across detach → re-attach; unbind clears the colorizer from the control that got detached (capture the `TextEditor` reference *before* switching away — after the switch a different control is realized). Every test drives the binder purely by adding to `vm.Tabs` and assigning `vm.ActiveTab`. Assert via `LineTransformers.OfType<RoslynColorizer>()`, never `.Count`.

### 6. Write the output-tab and mode-switch tests
Two output tabs keep their own documents across switching. The 5000-line cap holds after 6000 `Append`s (this one needs the dispatcher pumped, since `Append` marshals via `Dispatcher.UIThread.Post`, but needs no layout). The highlight-mode switch (`.cs` → `.csproj` → `.cs`) never leaves both paths live — assert on `editor.SyntaxHighlighting`, not on colorizer presence (see Constraints). **Do not** attempt a tail-follow test.

### 7. Write `docs/testing.md`, and add a testing line to `docs/stack.md`
`docs/testing.md`: how to run (`dotnet test MiniIde.slnx`, and stop a running MiniIde first — with the `MSB3027` symptom named so the next person recognizes it), the headless setup in one paragraph, the `[AvaloniaFact]` requirement, the `LineTransformers`-varies-by-mode trap, and — load-bearing — an explicit **"what headless cannot verify"** list: tail-follow (`ScrollToLine` no-ops), the reclassify debounce (fire-and-forget), the Ctrl+O picker (headless `StorageProvider` is a no-op stub that returns empty rather than throwing), F5 actually running a project, and Go-to-definition (needs `MSBuildLocator`, which only `Program.Main` performs). Keep to the house style in `docs/CLAUDE.md`: fragments, one idea per line. Then add a testing bullet to `docs/stack.md`, which currently has none.

## Test Plan
- [ ] With MiniIde **not** running: `dotnet test MiniIde.slnx` → all tests pass, 0 errors, only the three pre-existing warnings.
- [ ] Launch MiniIde (`scripts/run.ps1`), then run `dotnet test MiniIde.slnx` → confirm it fails with the DLL-lock error, and that `docs/testing.md` names that symptom and the fix.
- [ ] Deliberately break `TabEditorBinder.Unbind` (comment out the `LineTransformers.Remove` in the code-editor teardown) → the no-duplicate-colorizer and unbind-on-detach tests **fail**. Restore. This proves the tests actually bite; a green suite that can't fail is worthless.
- [ ] Deliberately break the mode switch (drop the `editor.SyntaxHighlighting = null` in the `CSharp` arm of `RefreshAndRedraw`) → the highlight-path test **fails**. Restore.
- [ ] Confirm the suite runs with no visible window appearing and does not steal focus.
- [ ] Re-run the still-manual checks from `unify-tab-editor-binding`'s Test Plan that headless cannot cover — tail-follow while a run streams, and the 200 ms typing debounce — and confirm `docs/testing.md` lists exactly those as manual.
