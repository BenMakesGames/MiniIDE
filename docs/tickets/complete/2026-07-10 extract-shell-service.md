# Extract ShellService; Move Shell/Path Logic Out of the Window Code-Behind

## Context
**Current behavior**: `Views/MainWindow.axaml.cs` (the `Window` subclass) directly holds all the OS-integration logic behind the context-menu path actions: launching Windows Terminal / PowerShell to run `claude` (`OnCtxOpenWithClaudeClick`, including the app-lifetime `_wtUnavailable` fallback state field), revealing files/folders in Explorer (`OnCtxOpenInExplorerClick`), and computing a solution-relative path (`OnCtxCopyRelativePathClick`). That is ~80 lines of `Process.Start` / path business logic living in the view layer, plus mutable process state (`_wtUnavailable`) on the window.

**New behavior**: A new `Services/ShellService` owns the OS process launches (Explorer reveal, terminal-with-Claude + its wt→PowerShell fallback and `_wtUnavailable` state). `SolutionService` gains a `ToRelativePath` method owning the solution-relative math. The four context-menu `Click` handlers in the code-behind shrink to thin delegations (~1–5 lines each) that resolve the target and call the service. **No user-visible behavior changes** — same menus, same actions, same fallback, same status-bar messages.

The `Click`-handler trigger mechanism is deliberately kept (not converted to bound `Command`s) — see Constraints for the Avalonia framework reasons.

## Prerequisites
None. Builds directly on the three completed context-menu tickets:
- `docs/tickets/complete/2026-07-05 file-path-context-menu.md`
- `docs/tickets/complete/2026-07-05 solution-name-context-menu.md`
- `docs/tickets/complete/2026-07-05 solution-context-menu-actions.md`

## Scope
### In scope
- New `Services/ShellService.cs`: process-launch operations with no view/VM dependency — Explorer reveal (folder vs. `/select,` file) and open-terminal-with-Claude (wt→PowerShell fallback, owns `_wtUnavailable`).
- `Services/SolutionService.cs`: add a `ToRelativePath(string absolutePath)` method (solution-relative, silent absolute fallback when no solution loaded).
- `ViewModels/MainWindowViewModel.cs`: construct and expose `ShellService` the same way the other services are exposed (`new` in the constructor, public get-only property).
- `Views/MainWindow.axaml.cs`: reduce `OnCtxOpenWithClaudeClick`, `OnCtxOpenInExplorerClick`, and `OnCtxCopyRelativePathClick` to thin delegations; delete the `_wtUnavailable` field; drop now-unused `using System.Diagnostics;` if nothing else needs it.

### Out of scope
- **Converting the `Click` handlers to bound `Command`s / `CommandParameter`.** Evaluated and rejected — see Constraints (Avalonia ContextMenu command-binding bugs). The handlers stay as `Click` delegations.
- **De-duplicating the three inline `<ContextMenu>` blocks** (tree / tab / solution) into a shared resource. Once the logic moves to services the remaining markup duplication is ~3 trivial `MenuItem` lines per site; hoisting it introduces shared-instance `DataContext` quirks for no real gain. Leave the three inline copies.
- **The context-menu enable/disable handlers** (`OnSolutionCtxOpening`, `OnCodeCtxOpening`) — that is the review's §1c (imperative menu state), a separate ticket. Do not touch them here.
- **Clipboard extraction.** `CopyToClipboardAsync` needs a `TopLevel` (`TopLevel.GetTopLevel(this)`), which is genuine view glue — it stays in the code-behind. Only the *relative-path computation* feeding it moves out.
- Cross-platform shell equivalents (`open`/`xdg-open`). App is Windows-only (`RuntimeIdentifier win-x64`).
- Adding a test project. None exists today; this ticket does not create one (the extraction still isolates the logic and removes state from the window).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml.cs` — the methods to gut/delegate: `OnCtxOpenWithClaudeClick` (wt→PowerShell fallback; the `_wtUnavailable` field sits just above it), `OnCtxOpenInExplorerClick` (folder-vs-`/select,` branch keyed on `ctx is TreeNode { Kind: NodeKind.Folder }`), `OnCtxCopyRelativePathClick` (calls `Path.GetRelativePath(Path.GetDirectoryName(slnPath)!, target)`), and the shared `GetTargetPath(object?)` resolver + `CopyToClipboardAsync(string)` helper (both stay).
  - `Services/RunService.cs` and `Services/NuGetService.cs` — the `ProcessStartInfo` + `ArgumentList` idiom to mirror. Note the *difference*: those use `UseShellExecute = false` (piped output, no window); Explorer/terminal launches use `UseShellExecute = true` (visible window).
  - `ViewModels/MainWindowViewModel.cs` — the constructor block where `Solution`, `Workspace`, `Search`, `NuGet`, `Run`, `Highlight` are each `new`-ed and exposed as get-only properties. Mirror that for `ShellService`.
  - `Services/SolutionService.cs` — `SolutionPath` property; add `ToRelativePath` beside it.
- **Related tickets** (read the Learnings, not the structure):
  - `solution-context-menu-actions.md` — documents the wt→PowerShell fallback design, why `UseShellExecute = true`, and that only a *launch* failure of `wt.exe` (a possibly-absent app-execution alias → `Win32Exception`) trips the fallback; `claude` failing inside a spawned terminal is never observed by this code.
  - `file-path-context-menu.md` — the `GetTargetPath` resolver, the Explorer `/select,` reveal form (comma, not space), and that `IClipboard.SetTextAsync` is an extension method needing `using Avalonia.Input.Platform;`.

## Constraints & Gotchas
- **Why `Click` handlers, not bound `Command`s.** In Avalonia 11, binding a `MenuItem`'s `Command`/`CommandParameter` inside a `ContextMenu` is unreliable: `ContextMenu.PlacementTarget` is always `null` ([Avalonia #16344](https://github.com/AvaloniaUI/Avalonia/issues/16344)), and binding `CommandParameter` on a `ContextMenu` `MenuItem` can gray the item out / misfire `CanExecute` because the menu is a separate popup namescope ([#18032](https://github.com/AvaloniaUI/Avalonia/issues/18032), [#16511](https://github.com/AvaloniaUI/Avalonia/issues/16511)). The existing `Click` + `sender.DataContext` (via `GetTargetPath`) pattern sidesteps all of this. Keep it; this ticket moves only the handler *bodies* into services.
- **`ShellService` must not depend on the view or VM.** Follow the other services: no `MainWindow`, no `Vm.Status` reference inside the service. Surface failures back to the caller (throw, or return an error string — see Open Decisions); the thin code-behind handler is responsible for putting the message on `Vm.Status`, mirroring today's `catch (Exception ex) { Vm.Status = $"...: {ex.Message}"; }`.
- **Fallback state moves into the service.** `_wtUnavailable` becomes private state on `ShellService`. The wt→PowerShell fallback must stay encapsulated: the open-terminal method attempts `wt.exe` first (unless already known-unavailable), and only surfaces failure to the caller when *both* wt and PowerShell fail to launch. A successful wt **or** PowerShell launch is a success. Only a `wt.exe` launch failure sets the unavailable flag.
- **`UseShellExecute = true`** for both Explorer and the terminal launches (visible window), unlike `RunService`/`NuGetService`. Build `ProcessStartInfo` with `ArgumentList.Add(...)` per argument (safer with spaces/commas) — do not concatenate a raw argument string.
- **Explorer reveal form**: folders → `explorer.exe <path>` (open into folder); files/projects → `explorer.exe /select, <path>` (reveal-and-select). The folder-vs-file decision currently lives in `OnCtxOpenInExplorerClick` as `ctx is TreeNode { Kind: NodeKind.Folder }`. Decide the `isFolder` boolean in the code-behind (it needs the `TreeNode`/DataContext) and pass it to `ShellService.RevealInExplorer(path, isFolder)` — keep the service parameterized and free of `TreeNode` knowledge.
- **No-solution safety preserved.** `OnCtxOpenWithClaudeClick` no-ops (status `"No solution open"`) when `Solution.SolutionPath` is null; `ToRelativePath` returns the absolute path unchanged when `SolutionPath` is null. Keep both behaviors.
- **Build lock**: a running MiniIde instance locks the output DLL, so an in-place `dotnet build` can fail only at the file-copy step. Build to a temp `-o` dir to verify compilation cleanly (per prior tickets' Learnings). Expect the pre-existing `CS0618` (`Workspace.WorkspaceFailed` obsolete) warning; introduce no new warnings.

## Open Decisions
1. **Error propagation from `ShellService`** — throw on launch failure (thin handler wraps in try/catch → `Vm.Status`, matching today's idiom) vs. return a nullable error string (handler sets `Vm.Status` when non-null). Default: **throw** — mirrors the current per-handler try/catch and keeps the service signature simple. Implementer's call.
2. **Home for the relative-path helper** — `SolutionService.ToRelativePath` (solution owns the anchor; reads its own `SolutionPath`) vs. a free static util. Default: **`SolutionService.ToRelativePath`** — the solution directory is the anchor and lives there already.
3. **`ShellService` method names/shape** — e.g. `RevealInExplorer(string path, bool isFolder)` and `OpenTerminalWithClaude(string directory)` (or `OpenWithClaude`). Default names as written; implementer may rename for local consistency.

## Acceptance Criteria
- [ ] A `ShellService` class exists in `MiniIde.Services` with no reference to `MainWindow`, `MainWindowViewModel`, or any Avalonia view type; it encapsulates the Explorer-reveal and open-terminal-with-Claude launches and holds the wt-unavailable state privately.
- [ ] `MainWindowViewModel` exposes the `ShellService` as a get-only property, constructed in its constructor alongside the other services.
- [ ] `SolutionService.ToRelativePath(absolutePath)` returns `Path.GetRelativePath(Path.GetDirectoryName(SolutionPath), absolutePath)` when a solution is loaded, and returns `absolutePath` unchanged when `SolutionPath` is null.
- [ ] The `_wtUnavailable` field no longer exists on `MainWindow`; the wt→PowerShell fallback state lives on `ShellService`.
- [ ] `OnCtxOpenWithClaudeClick`, `OnCtxOpenInExplorerClick`, and `OnCtxCopyRelativePathClick` contain no `ProcessStartInfo` / `Process.Start` / `Path.GetRelativePath` calls — they resolve the target (and, for Explorer, the `isFolder` flag) and delegate to `Vm.Shell` / `Vm.Solution.ToRelativePath`.
- [ ] `GetTargetPath`, `CopyToClipboardAsync`, `OnCtxCopyAbsolutePathClick`, and the enable/disable handlers (`OnSolutionCtxOpening`, `OnCodeCtxOpening`) are unchanged in behavior; the three inline `<ContextMenu>` blocks in the XAML are unchanged.
- [ ] The project compiles with no new warnings (temp-`-o` build to avoid the running-instance file lock).

## Implementation

### 1. Add `SolutionService.ToRelativePath`
In `Services/SolutionService.cs`, add a method returning the solution-relative form of an absolute path, anchored on `Path.GetDirectoryName(SolutionPath)`, with a silent fallback to the input path when `SolutionPath` is null. This is the exact math currently inlined in `OnCtxCopyRelativePathClick` — lift it verbatim so behavior (including the bare-filename result for the solution file itself) is identical.

### 2. Create `Services/ShellService.cs`
A `sealed` class with private `bool _wtUnavailable` and two operations, mirroring the `ProcessStartInfo` + `ArgumentList` idiom already in the codebase but with `UseShellExecute = true`:
- **Reveal in Explorer** — takes an absolute path and an `isFolder` flag. Folder → launch `explorer.exe <path>`; otherwise → `explorer.exe /select, <path>` (two separate `ArgumentList` entries: `"/select,"` then the path). This is the body of today's `OnCtxOpenInExplorerClick` minus the `GetTargetPath`/`TreeNode` resolution (which stays in the code-behind).
- **Open terminal with Claude** — takes a directory. Unless `_wtUnavailable`, attempt Windows Terminal: `wt.exe` with `ArgumentList` `-d`, `<dir>`, `claude`. On launch failure (`Win32Exception` etc.) set `_wtUnavailable` and fall through to PowerShell: `powershell.exe` with `WorkingDirectory = <dir>`, `ArgumentList` `-NoExit`, `-Command`, `claude`. Only surface failure to the caller if PowerShell *also* fails to launch. This is today's `OnCtxOpenWithClaudeClick` fallback block, moved wholesale.

Per Open Decision 1, default to throwing on the final failure (do not swallow); the caller reports status.

### 3. Expose `ShellService` on the VM
In `ViewModels/MainWindowViewModel.cs`, add `public ShellService Shell { get; }` and `new` it in the constructor next to `Run`, `Highlight`, etc. No other wiring (it has no events/progress).

### 4. Thin out the code-behind handlers
In `Views/MainWindow.axaml.cs`:
- `OnCtxOpenInExplorerClick`: resolve `ctx = (sender as MenuItem)?.DataContext`, `target = GetTargetPath(ctx)` (bail on null), compute `isFolder = ctx is TreeNode { Kind: NodeKind.Folder }`, then `try { Vm.Shell.RevealInExplorer(target, isFolder); } catch (Exception ex) { Vm.Status = $"Open in Explorer failed: {ex.Message}"; }`.
- `OnCtxOpenWithClaudeClick`: resolve the directory from `Vm.Solution.SolutionPath` (`Path.GetDirectoryName`); if null → `Vm.Status = "No solution open"` and return; else `try { Vm.Shell.OpenTerminalWithClaude(dir); } catch (Exception ex) { Vm.Status = $"Open with Claude Code failed: {ex.Message}"; }`.
- `OnCtxCopyRelativePathClick`: resolve `target` via `GetTargetPath` (bail on null), then `await CopyToClipboardAsync(Vm.Solution.ToRelativePath(target));`.
- Delete the `_wtUnavailable` field and its comment.
- `OnCtxCopyAbsolutePathClick`, `GetTargetPath`, `CopyToClipboardAsync` stay as-is.

### 5. Prune usings
If `System.Diagnostics` (Process) is no longer referenced anywhere in `MainWindow.axaml.cs` after the move, remove the `using`. Leave `Avalonia.Input.Platform` (clipboard extension) intact — `CopyToClipboardAsync` still needs it.

## Test Plan
- [ ] Compile-check: `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds; only the pre-existing `CS0618` warning, no new warnings/errors.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`.
- [ ] Right-click a `.cs` file → **Open in Explorer** reveals-and-selects the file; right-click a folder node (e.g. `Views`) → Explorer opens *into* the folder; right-click the `MiniIde` project node → `.csproj` is pre-selected. (Regression: `RevealInExplorer` folder-vs-file parity.)
- [ ] Right-click the solution name → **Open with Claude Code** opens a Windows Terminal in the solution folder running `claude` (verify CWD with `pwd`). On a machine without `wt.exe`, a PowerShell window opens in the solution folder running `claude`, and a second invocation goes straight to PowerShell (fallback + `_wtUnavailable` now on the service).
- [ ] **Open with Claude Code** with no solution loaded sets status `"No solution open"` and launches nothing.
- [ ] **Copy relative path** on a nested file (e.g. `src/MiniIde/Program.cs`) copies `src\MiniIde\Program.cs`; on the solution name copies the bare `MiniIde.slnx`; **Copy absolute path** still copies the full path with a `Copied <path>` status. (Regression: `ToRelativePath` parity.)
- [ ] No exceptions in the Output pane throughout.

## Learnings

### Open Decisions — how they resolved
1. **Error propagation** → **throw** (the ticket default). `ShellService.RevealInExplorer` / `OpenTerminalWithClaude` let `Process.Start` failures propagate; each thin code-behind handler keeps its existing `try/catch → Vm.Status = "...: {ex.Message}"`. Keeps the service signature `void` and the status-message ownership in the view, exactly as before.
2. **Relative-path home** → **`SolutionService.ToRelativePath`** (the ticket default). The solution directory is the anchor and already lives on `SolutionService`. Implemented as a one-line expression-bodied member mirroring the old inline math verbatim (silent absolute fallback when `SolutionPath` is null; the solution file itself yields its bare filename via `Path.GetRelativePath`).
3. **Method names** → kept the ticket's defaults: `RevealInExplorer(string path, bool isFolder)` and `OpenTerminalWithClaude(string directory)`. The `isFolder` decision (`ctx is TreeNode { Kind: NodeKind.Folder }`) stays in the code-behind — `ShellService` never sees a `TreeNode`.

### Freshness delta from `output-as-file-tabs` (completed same day)
The ticket predated the fileless-output-tab work, so two things had shifted since it was written — both handled without changing the ticket's scope:
- **A third imperative-menu-state handler now exists: `OnTabHeaderCtxOpening`** (`MainWindow.axaml.cs`, wired at `MainWindow.axaml:132`). It disables the tab-header path actions when the tab under the menu has no `FilePath`. It is the same *kind* of handler the ticket's "Out of scope" note parks for the §1c imperative-menu-state ticket (alongside `OnSolutionCtxOpening` / `OnCodeCtxOpening`), so it was left untouched.
- **`TabViewModelBase.FilePath` is now nullable** (output tabs return null). This did **not** affect the extraction: `GetTargetPath` already returns null for a null `FilePath`, so the thinned `OnCtxOpenInExplorerClick` / `OnCtxCopyRelativePathClick` still bail correctly on output tabs. The delegations stayed null-safe with no extra guarding.

### Tidbits
- `System.Diagnostics` was **kept** in `MainWindow.axaml.cs` (ticket step 5 was conditional): `LaunchNewInstance` still builds a `ProcessStartInfo` / calls `Process.Start` (new-instance launch on opening a second solution), so the `using` is still load-bearing.
- Existing services in this codebase are declared `public class` (not `sealed`); `ShellService` follows the ticket's explicit `sealed` instruction — a minor, harmless divergence from the local convention, justified because nothing subclasses services here.
- `UseShellExecute = true` is the deliberate contrast with `RunService`/`NuGetService` (`= false`, windowless, piped). Explorer and the terminal launch need a visible window, so shell-execute is correct despite the rest of the codebase using the windowless form.

### Verification status
- Compile-check passed (temp-`-o` build): 0 errors; only the three pre-existing warnings (CS0618 `Workspace.WorkspaceFailed`, IL3000 in `SyntaxHighlightService`, AVLN5001 `TextBox.Watermark` at `MainWindow.axaml:210`) — none in touched code, no new warnings.
- The remaining Test Plan items are interactive Windows-GUI checks (context-menu Explorer reveal, `wt`/PowerShell launch, clipboard copy) that can't be automated. Because the extraction is a **verbatim code move** — the process-launch and relative-path bodies were relocated unchanged — runtime behavior is identical to pre-refactor; these were left for a human manual pass.
