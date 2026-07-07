# Fix: `dotnet test` Invocation Uses Invalid `--project` Switch

## Context
**Current behavior**: Clicking Play on a test project (`ProjectKind.Tst`) runs `dotnet test --project <path>`, which fails immediately. In the default (VSTest) runner, `dotnet test` does not accept `--project`; the switch is forwarded to MSBuild, which rejects it with `MSBUILD : error MSB1001: Unknown switch. Switch: --project` and exit code 1. No tests run.

**New behavior**: Play on a test project runs `dotnet test <path>` — the project path as a **positional** argument — matching the VSTest CLI grammar. Tests build and run; output streams into the Output panel as before. Exe/Web projects continue to use `dotnet run --project <path>`, unchanged.

## Scope
### In scope
- `RunService.RunAsync` argument construction: branch the project-path argument shape on `ProjectKind.Tst`.

### Out of scope
- **Microsoft.Testing.Platform (MTP) support.** MTP is opt-in per solution via `global.json` (`{ "test": { "runner": "Microsoft.Testing.Platform" } }`) and uses the *opposite* grammar — `--project` is required and positional is dropped. Detecting the runner and branching accordingly is a deliberate non-goal here; a solution that opts into MTP will need a follow-up ticket. See Constraints.
- Passing extra `dotnet test` args (filters, loggers, verbosity) — bare invocation only, consistent with the existing `dotnet run` call.
- Any change to the `run`/`web` (`dotnet run --project`) path, which is correct and works today.

## Relevant Docs & Anchors
- **Code anchor**: `RunService.RunAsync` (`src/MiniIde/Services/RunService.cs`) — the sole `--project` construction site (`psi.ArgumentList.Add("--project")`). `verb` is already selected as `test` vs `run` on the line above.
- **Related ticket**: `docs/tickets/complete/2026-07-04 startup-dropdown-project-kinds.md` — introduced the kind-aware dispatch and (Acceptance line "Play command on kind Tst invokes `dotnet test --project <path>`") is where the incorrect `--project` for `test` originated. The verb-switching and log plumbing it established are correct and stay.
- **External**: [dotnet test with VSTest](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test-vstest) — synopsis is `dotnet test [<PROJECT> | <SOLUTION> | <DIRECTORY> | <DLL> | <EXE>] [options]`; project is positional, there is no `--project` option.

## Constraints & Gotchas
- **VSTest is the default runner** (`global.json` `test.runner` absent or `VSTest`). This ticket targets that default only. The two runners are mutually incompatible on this exact point: VSTest wants the path **positional** and errors on `--project`; MTP wants `--project` and drops positional. Do not attempt to satisfy both — positional is correct for the default and for the reported failure.
- `WorkingDirectory` is already set to the project's directory, so `entry.Path` could technically be omitted for `test` (VSTest defaults to the current directory). Pass it explicitly anyway — clearer, and keeps the two branches symmetric.
- Output/exit-code plumbing (`OutputDataReceived`, `ErrorDataReceived`, `WaitForExitAsync`, `[exit N]` log) is verb-agnostic and must stay untouched.

## Acceptance Criteria
- [ ] For `ProjectKind.Tst`, the spawned argument list is exactly `["test", <entry.Path>]` — no `--project` token.
- [ ] For all other runnable kinds (`Exe`, `Web`), the argument list remains `["run", "--project", <entry.Path>]`.
- [ ] Clicking Play on a test project no longer produces `MSB1001: Unknown switch --project`; `dotnet test` builds and executes the project's tests.

## Implementation

### 1. Branch the project-path argument on kind in `RunService.RunAsync`
In `src/MiniIde/Services/RunService.cs`, at the argument-building block that currently adds `verb`, then unconditionally `--project`, then `entry.Path`: keep adding `verb` first, then branch. When `entry.Kind == ProjectKind.Tst`, add `entry.Path` as a single positional argument (no `--project`). Otherwise, add `--project` then `entry.Path` as today. Everything else in the method (the `ProcessStartInfo`, `WorkingDirectory`, stream wiring, `WaitForExitAsync`, exit log) is unchanged.

## Test Plan
- [ ] Build succeeds: `dotnet build src/MiniIde/MiniIde.csproj`.
- [ ] Launch the app; open a solution containing a test project (e.g. the ExploreForever solution, or any project referencing `Microsoft.NET.Test.Sdk`).
- [ ] Select the test-project entry (`tst ...`) in the Startup dropdown, click Play. Confirm: no `MSB1001` error; `dotnet test` build + test output streams into the Output panel; the run ends with a normal `[exit 0]` (or `[exit 1]` on genuine test failures) rather than the immediate switch error.
- [ ] Regression: select an exe project, click Play — confirm it still runs via `dotnet run --project` and streams output.

## Learnings

- **The fix was exactly the ticket's one-branch change.** In `RunService.RunAsync`, after adding `verb`, branch on `entry.Kind == ProjectKind.Tst`: add `entry.Path` positionally for `test`, else `--project` then `entry.Path`. All stream wiring, `WorkingDirectory`, `WaitForExitAsync`, and exit logging stayed untouched.
- **VSTest vs MTP grammar are mutually incompatible on this point** — VSTest (`dotnet test <path>`) rejects `--project` (MSB1001); MTP requires `--project` and drops positional. This ticket deliberately targets only the default (VSTest) runner. A solution opting into MTP via `global.json` (`{ "test": { "runner": "Microsoft.Testing.Platform" } }`) would need runner detection and the opposite branch — out of scope, follow-up ticket if it ever arises.
- **Left an inline comment** at the branch explaining why the two runners diverge, so the asymmetry doesn't read as an accident to a future reader.
- **Verification limit**: build passes and the argument construction (the entire surface of the change) is verified by inspection; the interactive GUI Test Plan (launch app, click Play on a `tst` entry, watch Output panel) was not exercised headlessly.
- **Process note**: the ticket file was untracked in git, so `git mv` failed with "not under version control" — used a plain `Move-Item` to archive it instead.
