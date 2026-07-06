# Open Solution From Command-Line Argument

## Context
**Current behavior**: `Program.Main` forwards `args` to `StartWithClassicDesktopLifetime(args)`, but nothing ever reads them. A solution can only be opened after launch via the file picker (`OpenSolutionDialogAsync`, Ctrl+O). When Windows launches `MiniIde.exe "<path>.slnx"` from a double-clicked `.sln`/`.slnx` (per `scripts/associate-solution-files.ps1`, which registers the open command `"MiniIde.exe" "%1"`), the app starts to an empty workspace and ignores the file.

**New behavior**: When `MiniIde.exe` is launched with a `.sln`/`.slnx` path argument, that solution loads automatically at startup — identical end state to opening it via the picker. Launched with no argument (or an argument that isn't an existing `.sln`/`.slnx` file), the app starts to its normal empty workspace as before. This closes the loop on the file association: double-clicking a solution in Explorer now opens it in MiniIDE.

## Prerequisites
- None. The association scripts (`scripts/associate-solution-files.ps1`, `scripts/unassociate-solution-files.ps1`) already exist and register `"MiniIde.exe" "%1"` — this ticket makes the exe honor that `%1`.

## Scope
### In scope
- `App.axaml.cs` (`OnFrameworkInitializationCompleted`): read the startup argument from `desktop.Args`, resolve it to a valid solution path, and trigger the existing open pathway after the window is created.
- A small helper (private static in `App`, or inline) to pick the first argument that is an existing file with a `.sln`/`.slnx` extension and return its full path.

### Out of scope
- Opening folders, project files (`.csproj`), or arbitrary files passed as arguments — solution files only.
- Handling multiple solution arguments / multiple windows — take the first valid one, ignore the rest.
- Any change to `Program.Main`'s argument plumbing — `StartWithClassicDesktopLifetime(args)` already populates `desktop.Args`; don't add a parallel static-field hand-off.
- Command-line flags/switches (`--solution`, `-o`, etc.) — the argument is a bare path, matching what `%1` supplies.
- Changes to the association scripts themselves.

## Relevant Docs & Anchors
- **Code anchors**:
  - `App.OnFrameworkInitializationCompleted` (`src/MiniIde/App.axaml.cs`) — where the `IClassicDesktopStyleApplicationLifetime desktop` is already pattern-matched and `MainWindow` + `MainWindowViewModel` are constructed. `desktop.Args` (nullable `string[]?`) holds the launch arguments here, already stripped of the exe path.
  - `MainWindowViewModel.OpenSolutionAsync` / generated `OpenSolutionCommand` — the existing load pathway (used by the picker via `OpenSolutionCommand.ExecuteAsync(path)`). It sets `Status`, clears/populates `Tree`, `Projects`, `SolutionName`, and **catches its own exceptions**, surfacing failures to `Status`.
  - `MainWindow.OpenSolutionDialogAsync` (`Views/MainWindow.axaml.cs`) — the picker calls `Vm.OpenSolutionCommand.ExecuteAsync(path)` after resolving a local path; mirror that call.
  - `SolutionService.LoadAsync` — throws `InvalidOperationException` for an unrecognized moniker; relies on `SolutionSerializers.GetSerializerByMoniker(path)`, which keys off the `.sln`/`.slnx` extension.
  - `scripts/associate-solution-files.ps1` — `$command = "\"$resolved\" \"%1\""` confirms the exe receives the solution path as its sole argument.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 open-solution-file-on-name-doubleclick.md` — background on `Solution.SolutionPath` and the open pathway (opening the sln *as a file tab*; distinct from *loading* it as the workspace, which is this ticket).

## Constraints & Gotchas
- **`desktop.Args` excludes the executable path.** Avalonia populates it from the `args` array passed to `StartWithClassicDesktopLifetime`, which is already argv-without-program-name on .NET. Do **not** skip element `[0]` — the solution path is `Args[0]` for a double-click launch.
- **`desktop.Args` can be null** (no arguments). Guard before indexing/enumerating.
- **Don't block framework init.** `OnFrameworkInitializationCompleted` runs before the window is shown; loading synchronously here would stall startup and the load touches the VM's observable collections. Dispatch the open onto the UI thread (`Dispatcher.UIThread.Post`) so it runs after the window is up, rather than `await`ing inline. Keep the same VM instance that the `MainWindow` is bound to — don't construct a second one.
- **Validate before opening.** Only pass through arguments that are an existing file with a `.sln`/`.slnx` extension (case-insensitive). This avoids handing `SolutionService.LoadAsync` a bad path for arbitrary/relative junk; a valid-but-corrupt solution still routes its error to `Status` via `OpenSolutionAsync`'s existing catch.
- Resolve the argument to a full path (`Path.GetFullPath`) before opening so a relative launch path (rare, but possible from a shell) doesn't confuse `SolutionService`'s directory math (`Path.GetDirectoryName(path)!`).

## Open Decisions
1. **Invalid/nonexistent argument** — silently start empty vs. set a `Status` message. Default: start empty silently for a non-solution/nonexistent argument (it's indistinguishable from a normal launch); let a valid-path-but-load-failure flow through `OpenSolutionAsync`'s existing `Status = ex.Message`. Revisit only if debugging launches proves painful.
2. **Helper placement** — inline in `OnFrameworkInitializationCompleted` vs. a private static `ResolveStartupSolution(string[]? args)` on `App`. Default: private static helper (keeps the lifetime method readable; trivially unit-inspectable). Implementer's call.

## Acceptance Criteria
- [ ] Launching `MiniIde.exe "<absolute path to an existing .slnx>"` loads that solution at startup: the project tree, `Projects`, and `SolutionName` populate exactly as they do when the same file is opened via the picker.
- [ ] The same holds for an existing `.sln` file argument.
- [ ] Launching `MiniIde.exe` with no argument starts to the normal empty workspace — no exception, no error status.
- [ ] Launching with an argument that is not an existing `.sln`/`.slnx` file (nonexistent path, a `.txt`, a `.csproj`, or a directory) starts to the normal empty workspace without throwing.
- [ ] Double-clicking a `.slnx`/`.sln` file in Explorer, after running `scripts/associate-solution-files.ps1`, opens MiniIDE with that solution loaded.
- [ ] Only one solution loads when multiple path arguments are supplied (first valid one wins); no crash on the extras.

## Implementation

### 1. Resolve the startup solution from launch arguments
In `App.axaml.cs`, add a private static helper that takes `string[]? args` and returns `string?`: return null if `args` is null/empty; otherwise return the first entry that (a) has a `.sln` or `.slnx` extension compared case-insensitively (`Path.GetExtension` + `StringComparison.OrdinalIgnoreCase`) and (b) exists as a file (`File.Exists`), normalized via `Path.GetFullPath`. Return null if none qualify.

### 2. Trigger the open after the window is created
In `OnFrameworkInitializationCompleted`, inside the existing `if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)` block, after assigning `desktop.MainWindow`: keep a reference to the `MainWindowViewModel` you construct (assign it to a local rather than only inlining it into the window initializer), call the helper with `desktop.Args`, and if it returns a non-null path, `Dispatcher.UIThread.Post(async () => await vm.OpenSolutionCommand.ExecuteAsync(path))`. This reuses the picker's exact load pathway and defers execution until after startup completes. Add the `using Avalonia.Threading;` (and `System.IO` if not already imported) needed for `Dispatcher` / `Path` / `File`.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds.
- [ ] Build, then from the repo root launch the exe directly with the repo's own solution as an argument, e.g. `& "src/MiniIde/bin/Debug/net10.0/win-x64/MiniIde.exe" "$(Resolve-Path MiniIde.slnx)"` — confirm the window opens with the solution tree, project dropdown, and solution name populated (no picker interaction needed).
- [ ] Launch the exe with no argument (`scripts/run.ps1`) — confirm normal empty-workspace startup, unchanged from before.
- [ ] Launch the exe with a bogus argument (`& "<exe>" "C:\does\not\exist.slnx"`) and with a non-solution file (`& "<exe>" "<exe>"`) — confirm the app starts empty and does not crash.
- [ ] Run `scripts/associate-solution-files.ps1`, point it at the built exe, then double-click a `.slnx` (and a `.sln` if available) in Explorer — confirm MiniIDE launches with that solution loaded. Run `scripts/unassociate-solution-files.ps1` afterward if restoring prior associations.
- [ ] Regression: Ctrl+O / the Open Solution picker still loads a solution as before.

## Learnings

- **Resolved as specified.** `ResolveStartupSolution(string[]? args)` added as a private static helper on `App`; `OnFrameworkInitializationCompleted` now hoists the `MainWindowViewModel` to a local `vm` and, when the helper returns a path, does `Dispatcher.UIThread.Post(async () => await vm.OpenSolutionCommand.ExecuteAsync(path))`. Reusing the picker's exact command is the correctness guarantee — no second load pathway.
- **Open Decision 1 (invalid arg):** took the default — start empty silently. Non-solution/nonexistent args return null from the helper and never reach `OpenSolutionAsync`, so they're indistinguishable from a normal launch. A valid-path-but-corrupt solution still routes its error to `Status` via the command's existing catch.
- **Open Decision 2 (helper placement):** took the default — private static helper, keeps the lifetime method readable.
- **Usings:** needed `using System;` (for `StringComparison`), `System.IO` (`Path`/`File`), and `Avalonia.Threading` (`Dispatcher`) — none were previously imported in `App.axaml.cs`.
- **Verification:** smoke-launched the built exe with (a) the repo's own `.slnx`, (b) a nonexistent path, (c) the exe itself as a non-solution file, and (d) no argument — all start without crashing. The populated-tree assertion follows from reusing the proven picker command rather than re-inspecting the GUI. The Explorer double-click item is inherently manual (registry association) and left to the user.
- **`git mv` note:** the ticket file was still untracked (`??`) at implementation time, so it moved to `complete/` with a plain `mv` rather than `git mv`.
