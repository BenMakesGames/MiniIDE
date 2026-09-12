# Run Configuration Dropdown

## Context
**Current behavior**: The top bar has a plain `▶ Run` button bound to `PlayCommand`. A run is always whatever `dotnet run` / `dotnet test` does by default, which means the Debug configuration and the default launch profile. There is no way to run Release, and no way to pick a launch profile, short of leaving the IDE.

**New behavior**: The Run button becomes an Avalonia `SplitButton` whose flyout offers two independently-selected groups — **build configuration** (Debug, Release, and any custom configurations the solution declares) and **launch profile** (read from the project's launch settings, when it has any). Clicking the primary side runs the currently-selected combination; picking from the flyout changes the selection and that becomes what the primary side does from then on, starting from Debug and no explicit profile each session. The selected configuration is passed as `dotnet run -c <configuration>` (or `dotnet test -c <configuration>`), the profile as `dotnet run -lp <profile>`, and both surface in the output tab header and the status message.

## Prerequisites
- `docs/tickets/per-run-output-tabs.md` — this ticket extends the per-run handle and the per-invocation output-tab header that ticket introduces. Implementing this first would mean rewriting the same two methods twice.

## Scope
### In scope
- `SolutionService`: expose the solution's build configurations, which the already-loaded solution model provides.
- A launch-profile reader: parse a project's launch settings and return the profiles `dotnet run` can actually use.
- `MainWindowViewModel`: the selectable configuration and profile lists plus the current selection, re-resolved on solution reload; pass the selection through to the run.
- `RunService`: accept the configuration and profile and add the corresponding arguments.
- `MainWindow.axaml`: replace the Run `Button` with a `SplitButton` plus a grouped `MenuFlyout`.
- Output tab header and run status message name the configuration (and profile, when one is chosen).

### Out of scope
- **Platform selection** (`Any CPU` / `x64`). The solution model exposes platforms alongside configurations, but nothing in the app varies by platform today and `dotnet run` needs no platform argument for these projects.
- **Persisting the selection across app restarts.** There is no settings/persistence infrastructure anywhere in the app; even the startup project is only preserved across in-session solution reloads. Selection is session-only, matching that.
- **Editing or creating launch profiles.** Profiles are read-only input here — the IDE is a read-only window onto disk.
- **Non-`Project` launch profile kinds** (`IISExpress`, `Executable`, and anything else). They are filtered out rather than supported; see Constraints.
- **Honouring solution-to-project configuration remapping.** See the accepted risk in Constraints.
- **A separate debug/no-debug axis.** There is no debugger, so Visual Studio's F5-versus-Ctrl+F5 distinction has no meaning here.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/per-run-output-tabs.md` — the per-run handle and per-invocation tab headers this ticket feeds into.
  - `docs/tickets/complete/2026-07-04 startup-dropdown-project-kinds.md` and `docs/tickets/complete/2026-07-04 project-kind-icons-in-dropdowns.md` — the existing top-bar dropdown, its item template, and the disabled-item styling convention, if the flyout wants to look consistent with it.
  - `docs/tickets/complete/2026-07-07 fix-dotnet-test-invocation.md` — background on why the test and run argument lists diverge, which the new arguments must respect.
- **Design docs**: `docs/avalonia.md` — check it for control-pattern notes before reaching for `SplitButton`, and add anything learned about it.
- **Code anchors**:
  - `SolutionService.LoadAsync` — already opens a `SolutionModel` through `Microsoft.VisualStudio.SolutionPersistence`; that model has a `BuildTypes` property, so no new dependency and no extra process are needed.
  - `MainWindowViewModel.LoadTreeAsync` — the `previousStartup` re-resolve-by-path logic is the exemplar for how a user selection survives a solution reload, and why: an external change must not silently move the user's run target.
  - `MainWindowViewModel.PlayAsync`, `CanPlay`, and `OnStartupProjectChanged`.
  - `RunService` — the argument-building block, including the test-versus-run branch.
  - `ProjectEntry` / `ProjectKind` — `Kind` and `IsRunnable`; test projects need the profile group suppressed.
  - `MainWindow.axaml` — the top-bar `StackPanel` with the startup `ComboBox` and the Run button.
  - `MainWindow.axaml.cs` — the F5 branch in `OnGlobalKeyDown`, which routes to `PlayCommand` and so inherits the selection for free.

## Constraints & Gotchas
- **Configuration and launch profile are orthogonal, not alternatives.** A configuration controls compilation (`-c`, which drives `DEBUG`, optimization, and the `bin/<configuration>` path); a profile controls launch environment (`-lp`, which sets environment variables and URLs). They compose, and a profile is never a substitute for choosing Release. Two independent selections, not one merged list.
- **An unknown configuration fails silently.** `dotnet run -c <nonexistent>` does not warn or error — it builds into a `bin` folder of that name with `DEBUG` undefined and runs. So the configuration must only ever come from the enumerated list, never from free text, and a selection whose name disappears after a solution reload must fall back rather than be passed through.
- **Only `Project`-kind launch profiles work.** `dotnet run -lp` rejects other kinds *softly*: it prints that the profile type is not supported and then runs with **no** profile applied. Offering an unfiltered list would therefore present entries that silently do nothing. Filter to `commandName` of `Project` when reading.
- **`dotnet run` looks for launch settings in two places**, and the profile reader should check both: `Properties/launchSettings.json` under the project directory, and `<project file name without extension>.run.json` beside the project file (the .NET 10 addition for file-based apps). If neither exists there are no profiles.
- **Naming a profile when no launch settings file exists is a hard error**, unlike an unknown configuration — so only ever offer profiles that were actually found.
- **`dotnet test` has no `--launch-profile`.** It accepts `-c` but not `-lp`. The profile group must be suppressed for `ProjectKind.Tst` projects; the configuration group applies to both verbs.
- **The default profile is the first `Project`-kind one in file order**, which is what `dotnet run` picks with no `-lp`. Note that file order is not the filtered order — a non-`Project` profile may sit above it. If the flyout offers an explicit no-profile choice, that maps to `--no-launch-profile`, which is meaningfully different from passing nothing.
- **Accepted risk, deliberately not handled**: the solution's configuration names are assumed to match the project's one-for-one. A classic `.sln` can remap them via its per-project configuration rules, and because `-c` takes the *project* configuration and an unknown one fails silently, such a solution would quietly build the wrong configuration. Judged rare enough to accept; the escape hatch, if it ever bites, is reading those rules off the solution model.
- **Avoid per-project MSBuild queries.** `dotnet msbuild <project> -getProperty:Configurations` also yields the list and honours a per-project `<Configurations>` override, but it costs a process launch per project (measured around 0.7 s warm) for what the already-loaded solution model answers for free. Visual Studio's own toolbar is solution-level too.

## Open Decisions
1. **Where the profile reader lives** — a small dedicated service alongside `ProjectClassifier`, versus a static helper on the existing solution/classification surface. Default: mirror `ProjectClassifier`, which is the closest existing shape (a static per-project file reader returning a model type).
2. **JSON parsing approach** — `System.Text.Json` document parsing versus a typed DTO. Default: document parsing; the file is small, only two fields matter, and unknown-shape robustness is worth more than a model here.
3. **How the selection reads in the flyout** — radio-style menu items, versus checkmarks on the current entry. Default: whichever `MenuFlyout` supports most idiomatically with the least custom styling.
4. **Primary-button label** — whether the button text names the current selection (for example `▶ Run (Release)`) or stays plain `▶ Run` with the selection visible only in the flyout. Default: name it, so a Release run is not indistinguishable from a Debug one before it starts.
5. **Whether configurations get icons** to match the startup `ComboBox`'s project-kind glyphs. Default: no; a configuration has no natural icon and the existing dropdown's icons carry real information.

## Acceptance Criteria
- [ ] The top bar's Run control is a `SplitButton`; its primary side starts a run and its secondary side opens a flyout.
- [ ] The flyout's configuration group lists exactly the solution's build configurations — `Debug` and `Release` for a solution that declares none, plus any custom ones a `.slnx` or `.sln` does declare.
- [ ] Selecting Release and clicking the primary side runs that project with `-c Release`, producing a build under `bin/Release` and a binary in which `DEBUG` is not defined.
- [ ] After selecting a configuration from the flyout, the primary side keeps using it for subsequent runs within the session, and a fresh session starts on `Debug`.
- [ ] Pressing F5 runs the same configuration and profile as the primary side.
- [ ] For a project with launch settings, the flyout's profile group lists only profiles whose `commandName` is `Project`; profiles of other kinds do not appear.
- [ ] For a project with no launch settings in either recognized location, the flyout shows no profile group at all and runs pass no profile argument.
- [ ] Selecting a launch profile runs with `-lp <profile>`, and that profile's environment variables are visible to the running app.
- [ ] For a `ProjectKind.Tst` project no profile group is offered, and the run passes `-c <configuration>` to `dotnet test` with no launch-profile argument.
- [ ] The output tab header and the run status message both name the configuration used, and the profile when one is selected.
- [ ] After a solution reload that keeps the selected configuration name available, the selection is retained; when the name is gone, the selection falls back to an available configuration rather than being passed through.

## Implementation

### 1. Expose the solution's configurations
`SolutionService.LoadAsync` already has the opened `SolutionModel` in hand, and its `BuildTypes` property is exactly this list — verified to yield `Debug` and `Release` for a solution declaring nothing, and to pick up custom entries from both `.slnx` `Configurations` declarations and a classic `.sln` solution-configuration section. Capture it into a public read-only property beside `Projects`, following how that one is assigned at the end of the load and defaults to empty before any solution is open.

### 2. Read a project's launch profiles
Add a reader that takes a project path and returns the usable profiles in file order, mirroring `ProjectClassifier`'s shape: static, per-project, tolerant of a missing or malformed file (return none rather than throwing — that class swallows load failures and falls back for the same reason). Check both recognized locations named in Constraints, keep only entries whose `commandName` is `Project`, and preserve order so that the first one is identifiable as the `dotnet run` default.

### 3. Hold the selection in the view model
Add the configuration list, the profile list, and the two current selections to `MainWindowViewModel`. Populate configurations from the solution service after a load, and profiles from the reader whenever the startup project changes — `OnStartupProjectChanged` is already the hook that reacts to that. Default the configuration to `Debug` when present and otherwise the first available; default the profile per Constraints. On reload, re-resolve the configuration selection by name in the spirit of `LoadTreeAsync`'s `previousStartup` handling, falling back when the name is gone.

### 4. Pass the selection into the run
Widen the run entry point so `PlayAsync` hands the chosen configuration and profile to `RunService`, which appends `-c` for both verbs and `-lp` only for the run verb. The existing test-versus-run branch is where that asymmetry belongs, next to the comment explaining the `--project` difference. Since the prerequisite ticket left exactly one call site, this is a small signature change rather than a ripple.

### 5. Label the tab and the status
Fold the configuration into the output tab header that the prerequisite ticket generates, so a Debug tab and a Release tab of the same project are distinguishable, and so the repeat-run numbering groups by project *and* configuration rather than by project alone. Do the same for the run status message, which currently names only the project.

### 6. Replace the Run button with a SplitButton
In `MainWindow.axaml`, swap the Run `Button` for a `SplitButton` whose `Command` is still `PlayCommand` and whose `Flyout` is a `MenuFlyout` carrying the two groups with a separator and headers between them. Bind the profile group's visibility so it disappears when the profile list is empty or the startup project is a test project. `SplitButton` ships in `Avalonia.Controls` and is styled by the Fluent theme the app already references, so no extra package or custom template is needed. Leave the F5 branch in `OnGlobalKeyDown` alone — it routes through `PlayCommand` and inherits the selection.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes.
- [ ] Open this repository's own solution. Confirm the flyout offers `Debug` and `Release` and no profile group (no project here has launch settings).
- [ ] Select Release, run, and confirm the output shows a Release build, `bin/Release` is populated, and the tab header and status name Release.
- [ ] Run again without touching the flyout; confirm it is still Release. Restart the app and confirm it is back to Debug.
- [ ] With Release selected, press F5 and confirm it runs Release rather than Debug.
- [ ] Select the test project and confirm no profile group appears and that `dotnet test` still runs cleanly with a non-default configuration selected.
- [ ] Against a scratch solution: add a `.slnx` `Configurations` block with a third build type and confirm it appears in the flyout; add a `Properties/launchSettings.json` with a `Project` profile, an `IISExpress` profile, and an `Executable` profile, and confirm only the `Project` one is offered.
- [ ] Run that scratch project under the offered profile and confirm one of its `environmentVariables` reaches the app.
- [ ] Reload a solution while a non-Debug configuration is selected and confirm the selection survives.

## Learnings

### Architectural decisions
- **Open Decision 1 — dedicated reader, default taken.** `Services/LaunchProfileReader.cs` mirrors `ProjectClassifier` exactly: static, per-project, swallowing every failure into "no profiles". The shape carried over cleanly because the two solve the same problem — read one optional file per project and answer a question the UI needs synchronously.
- **Open Decision 2 — document parsing, default taken.** `JsonDocument` with `CommentHandling = Skip` and `AllowTrailingCommas` (launch settings are hand-edited and Visual Studio tolerates both). Only `profiles.*.commandName` matters, and `EnumerateObject` preserves file order for free, which is what makes "the first entry is the one `dotnet run` would have picked" true without extra bookkeeping.
- **Open Decision 3 — neither radio menu items nor checkmarks; a plain `Flyout` with `RadioButton`s.** The default was "whichever `MenuFlyout` supports most idiomatically", and the answer turned out to be *neither*: see Workarounds. The flyout is hand-laid content — a header `TextBlock`, an `ItemsControl` of `RadioButton`s, a `Border` separator, then the same again for profiles — which is exactly the layout the ticket described, just not built out of `MenuItem`s.
- **Open Decision 4 — name the selection, default taken.** The primary label binds to `RunLabel` (`▶ Run (Release)`). Only the configuration is named; folding the profile in too would grow the button every time a profile is picked, and the profile is one flyout click away.
- **Open Decision 5 — no icons, default taken.**
- **`RunSelection` record rather than two loose `string?` parameters.** `RunService.Start(entry, selection, log, ct)` cannot have its configuration and profile transposed at a call site, which two adjacent nullable strings invited. It also gave the tab header, the status message, and the run-tab numbering key one object to read from, so those three cannot drift.
- **Both flyout groups are rebuilt only when their inputs change** (solution load, startup-project change); picking a row mutates `RunChoice.IsSelected` in place. Rebuilding the `ObservableCollection` from inside a row's own command would pull the clicked container out from under the click that is still being handled.
- **The profile group is *absent*, not hidden, when there is nothing to offer.** The ticket suggested binding its visibility; emitting no rows is simpler, and the `IsVisible` binding on the group's wrapper then only has to answer one flag (`HasLaunchProfiles`). Test projects take the same path — `RefreshRunOptions` never even reads profiles for a `ProjectKind.Tst` startup project.
- **`PlayAsync` drops the profile for a test project a second time**, independently of the flyout having offered none. The verb-to-argument rule lives in one place that way, rather than being an invariant the UI is trusted to maintain.
- **Run-tab numbering keys on project + configuration + profile**, one step beyond the ticket's "project and configuration": the header names all three, so the numbering groups exactly as the labels read.
- **The profile group offers an explicit `(dotnet default)` row** meaning "pass no `-lp`", which is where every session starts and the only way back once a profile is picked. `--no-launch-profile` is deliberately *not* wired up — it means "suppress the default profile", a third state nothing in the app asks for (YAGNI).

### Problems encountered
- **`MenuFlyout` cannot express this at all**, which was the one real design casualty. Two independent blockers: (1) `MenuFlyout.Items` and `ItemsSource` are mutually exclusive (`ItemsSource` calls `Items.SetItemsSource`, which rejects inline children), so a flyout can't mix static group headers with a bound list; (2) `ItemContainerTheme` — the only hook for per-item `ToggleType`/`IsChecked`/`Command` — has an open bug where its setter bindings resolve against the parent VM instead of the item ([Avalonia #18146](https://github.com/AvaloniaUI/Avalonia/issues/18146), unfixed since 11.2.1). Confirmed against the Avalonia sources rather than by trial: `MenuBase.NeedsContainerOverride` returns false only for `MenuItem`/`Separator` *instances*, which is the escape hatch — and it means putting controls in a view model.
- **A `--launch-profile` mention inside an XAML comment fails the build.** XML comments cannot contain a double hyphen; the Avalonia build task surfaces it as `AVLN1001: File doesn't contain valid XAML` with a raw `XmlException` stack, which reads like a structural error rather than a comment typo.
- **`FlyoutBase` is an `AvaloniaObject`, not a `StyledElement`** — no `DataContext`, not in any tree. Bindings inside `<SplitButton.Flyout>` work anyway because `PopupFlyoutBase.ShowAtCore` calls `Popup.SetPopupParent(placementTarget)`, parenting the popup to the button. Worth knowing before assuming a flyout is a dead zone for bindings, or that it is a live one.

### Interesting tidbits
- `SolutionModel.BuildTypes` returns `Debug`/`Release` for a `.slnx` that declares nothing at all — the model synthesizes the pair rather than returning empty. Both that and a custom `<Configurations><BuildType Name="Profiling"/></Configurations>` block are now covered by `SolutionConfigurationTests`, which needs no MSBuild and runs in ~100 ms.
- `dotnet`'s launch-settings precedence is winner-takes-all, not merge: `Properties/launchSettings.json` is consulted first and `<name>.run.json` **only if that file is absent**. The reader matches it, so the offered list always equals the list that would actually apply.
- `SplitButton` disables *both* halves when `Command.CanExecute` is false (`IsEnabledCore => base.IsEnabledCore && _commandCanExecute`). So with a class library selected the flyout is unreachable — same as the old Run button being greyed, but it does mean the configuration can't be changed until a runnable project is selected. Acceptable here; a surprise waiting for anyone who puts unrelated settings behind a `SplitButton`.
- A one-way `IsChecked` binding survives the click that toggles it: `RadioButton` sets the value with `SetCurrentValue`, which does not clear the binding, so the VM's answer lands immediately after and wins. That is what lets `Command` own the write while `IsChecked` stays a pure read.

### Workarounds / limitations
- **The flyout does not close when a row is picked.** A plain `Flyout` light-dismisses on an outside click only; a `MenuFlyout` would have closed on the pick. Given two independent axes, staying open lets both be set in one visit — but it is a consequence of the control choice, not an independent decision, and it is the thing to revisit if #18146 is ever fixed.
- **The solution's configuration names are still assumed to match each project's**, exactly as the ticket's accepted risk states. Nothing was added to read the `.sln` per-project configuration remapping rules.
- **Two Test Plan items are unverified.** A launch profile's `environmentVariables` reaching a running app, and the `IISExpress`/`Executable` filtering *as rendered in the flyout*, both need a scratch project with launch settings driven through the UI. The filtering itself is covered by `LaunchProfileReaderTests` against real files; what is untested is only the last hop from that list into the popup.

### Related areas affected
- `RunService.Start` gained a parameter. The prerequisite ticket had already reduced it to one call site, so this stayed a one-line change at the caller, as predicted.
- `LoadTreeAsync` now re-resolves two selections instead of one, on the same principle for both: an external change must not silently move what the Run button does.
- `docs/avalonia.md` gained seven entries — flyout binding mechanics, the `MenuFlyout` limitations, `SplitButton` enablement, and the one-way-`IsChecked` radio pattern.

### Rejected alternatives
- **Submenu groups** (`<MenuItem Header="Configuration" ItemsSource="…">`) — the one shape `MenuFlyout` *can* express with a bound list. Rejected because in the common case (no launch settings anywhere, as in this repo) the entire flyout would collapse to a single `Configuration ▸` row, turning a two-click choice into three for no gain. It also still needs per-item `ToggleType`/`IsChecked`, so it does not actually escape #18146.
- **A flat `ItemsSource` mixing `RunChoice` view models with literal `Separator` controls** — works (container generation passes both through), but puts Avalonia controls inside a view model to buy a horizontal line.
- **Populating the flyout from code-behind** — reliable, and not out of character for this codebase's context menus, but it moves the rebuild-on-reload wiring out of the view model for no benefit once a plain `Flyout` binds normally.
- **`dotnet msbuild -getProperty:Configurations` per project** — rejected up-front by the ticket; nothing found during implementation argued for revisiting it.
- **Persisting the selection** — out of scope by the ticket, and confirmed as the right call by watching the child MiniIDE that a Release run launched come up on `▶ Run (Debug)`: a fresh session starting clean is legible, and there is still no settings store to hang persistence on.
