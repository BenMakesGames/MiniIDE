# Git Pull / Push Buttons in the Solution Bar

## Context
**Current behavior**: MiniIde knows nothing about git. `.git` appears in `IdeDirectories.Pruned`, so the solution-tree walk, the global search, and the `SolutionWatcher` feed all skip it entirely — it is the one directory the IDE has deliberately never looked at. Pulling or pushing means leaving the app for a terminal (the solution-name context menu's *Open in Claude Code* is the closest thing to a hand-off).

**New behavior**: The top bar, immediately right of the solution name, gains a pull (↓) / push (↑) pair when the open solution lives inside a git repository. Clicking either opens — or re-activates — a single shared **Git - Output** tab in the file-tab area, echoes the command it is about to run, and streams `git pull` / `git push` into it. Both buttons are disabled for the duration of whichever ran. When the solution is *not* inside a repository, the pair is replaced by a **+Git** button that runs `git init` into the same tab and flips the bar to pull/push on success. Repository membership is re-checked whenever the window regains focus, so a `git init` (or a `rm -rf .git`) done outside the IDE is picked up on the next alt-tab back.

## Prerequisites
None.

## Scope
### In scope
- A new `Services/GitService.cs`: repository discovery (nearest ancestor holding a `.git`) plus a piped, windowless git-command runner.
- `MainWindowViewModel`: the repo-root/busy state, the three commands, their reason-specific disabled tooltips, and a `git:` output-tab resolver.
- Re-evaluating repo state on solution open, on window activation (`RefreshFromDiskAsync`), and immediately after our own `git init`.
- `Views/MainWindow.axaml`: the button group in the top bar's column 0, beside the solution-name `TextBlock`.
- `Models/ActionIcon.cs`: the two arrow glyphs, plus the mandatory font-subset regeneration.
- Unit tests for repository discovery.
- A new `docs/git.md` recording the design, and a one-line cross-reference from `docs/disk-watching.md`.

### Out of scope
- **Un-pruning `.git` from `IdeDirectories.Pruned`.** It stays pruned. A `git pull` writes hundreds of files under `.git` (objects, refs, index, logs); letting those reach the watcher would turn every fetch into a structural-signal storm and a full tree rebuild per burst. The repo-state check is deliberately *not* watcher-driven — see Constraints.
- **A dedicated `FileSystemWatcher` for `.git`.** Considered and rejected with the requester: it would only ever see the solution root, missing the ancestor-repo case the discovery walk handles, in exchange for a second watcher and live-while-foregrounded updates nobody asked for. Focus-time is the agreed trigger.
- **Any other git operation** — commit, stage, fetch, branch, status, log, diff, remote management, conflict resolution, a git panel, gutter blame, or file-level history. This ticket is exactly pull, push, and init.
- **A branch indicator, ahead/behind counts, or a dirty-working-tree marker** in the top bar or anywhere else.
- **Cancelling an in-flight git operation.** There is no Stop button on the Git tab; a wedged operation is mitigated by `GIT_TERMINAL_PROMPT=0` (see Constraints) rather than by a kill path.
- **Fixing the tree collapse a `git pull` causes.** A pull's file burst is a structural signal, and `LoadTreeAsync` still does `Tree.Clear()` — so pull resets expansion and scroll today. That is `docs/tickets/incremental-solution-tree-refresh.md`; do not solve it here.
- **Muting our own git writes** via `SolutionWatcher.MuteWrites`. A pull's incoming files *must* reach the workspace and the tree; muting is reserved for writes that refresh the view themselves (only `RenameService.ApplyToDisk`).
- **Credential entry, SSH key management, or a first-run "configure git" flow.** Whatever the machine's git is already configured to do is what happens; failures land in the tab as git's own stderr.
- **Non-Windows behavior** and a bundled git. `git` is resolved from `PATH`; if it isn't there, the launch failure surfaces on the status bar like any other.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/disk-watching.md` — the feed's contract, the pruning rule, and the `MuteWrites` policy this ticket deliberately does not use. Add the cross-reference here.
  - `docs/CLAUDE.md` — one topic per file, fragments over prose, one idea per line (for `docs/git.md`).
  - `src/MiniIde/Assets/icons/README.md` — the pinned MDI version and the codepoint-verification rule.
  - `tools/README.md` — the font-subset workflow.
- **Related tickets** (context and gotchas, not a structure to copy):
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — why streamed output lives in the file-tab area as an `OutputTabViewModel` rather than a bottom panel, and the namespaced-`TabId` dedup rule.
  - `docs/tickets/complete/2026-08-18 per-run-output-tabs.md` — the get-or-create-and-activate resolver shape (`ResolveNuGetOutput`) and why a shared tab keys on a fixed namespace prefix instead of its header.
  - `docs/tickets/complete/2026-07-16 os-driven-disk-reconcile.md` — `RefreshFromDiskAsync` as the window-`Activated` safety net, which this ticket extends with one more responsibility.
  - `docs/tickets/complete/2026-07-10 context-menu-icons.md` — the `ActionIcon` glyph convention.
  - `docs/tickets/run-tab-restart-strip.md` (**open, not implemented**) — it rewrites the `OutputTabViewModel` strip and its `HasRun` gating. This ticket's Git tab is constructed with no starter, so it answers `HasRun` false and shows no strip either way; keep it that way so the two don't collide.
- **Code anchors** (symbols; verify against source):
  - `NuGetService.RestoreAsync` — the exemplar for the git runner: `UseShellExecute = false`, both streams redirected and pumped into an `Action<string> log`, `CreateNoWindow`, `await WaitForExitAsync`, returns the exit code.
  - `MainWindowViewModel.ResolveNuGetOutput` and `GetOrCreateOutputTab` — the tab resolver to mirror.
  - `MainWindowViewModel.OpenSolutionAsync`, `LoadTreeAsync`, `RefreshFromDiskAsync` — the three points where repo state must be recomputed.
  - `OutputTabViewModel.Append` / `HasRun` — appending is already UI-thread-marshalled; a starter-less tab has no run strip.
  - `IdeDirectories.Pruned` and `SolutionWatcher.IsPruned` — read both to see *why* `.git` produces no signal (the prune tests every segment of the relative path, so the `.git` node itself is pruned, not merely its contents).
  - `SolutionService.SolutionPath` — the only source for the solution directory; there is no solution-root property, so callers take `Path.GetDirectoryName` of it.
  - The top bar in `Views/MainWindow.axaml` — the `Grid Margin="8,4" ColumnDefinitions="Auto,*,Auto"` inside the `Grid.Row="1"` `Border`; the solution-name `TextBlock` with its `ContextMenu` occupies column 0.
  - `OutputTabViewModelTests` — the existing VM-level test style for this area.

## Constraints & Gotchas
- **`.git` is invisible to the disk feed, by design.** `SolutionWatcher.IsPruned` walks every segment of the path relative to the solution root and prunes on a match, so `<root>\.git` is pruned as well as everything beneath it. No repo creation or deletion will ever arrive as a `DiskChangeSignal`. Any attempt to detect repo state by subscribing to `Watcher.Changed` is dead code — the check must be polled at the moments named in Implementation.
- **`git pull` can block forever on a credential prompt**, and with both buttons disabled for the operation's duration that is an unrecoverable UI state. Set `GIT_TERMINAL_PROMPT=0` in the child's environment so git fails fast with a message into the tab instead of waiting on a console that isn't attached. (`ProcessStartInfo.Environment` is only usable with `UseShellExecute = false`, which the runner already needs.) Git Credential Manager may still raise its own GUI dialog for HTTPS remotes — that is a window the user can answer or dismiss, not a hang, and is acceptable.
- **Adding an `ActionIcon` glyph obliges a font-subset re-run**: `cd tools && npm run subset`. The shipped TTF carries only the glyphs referenced by `\U000FXXXX` escapes under `src/MiniIde`; skip the re-run and the button renders as a blank tofu box. `IconFontCoverageTests` fails the run if you forget.
- **The codepoints are verified against pinned MDI 6.8.96**: `mdi-arrow-down` = `\U000F0045`, `mdi-arrow-up` = `\U000F005D`. Use these; do not re-derive from memory.
- **A `.git` entry can be a file, not a directory.** Linked worktrees and submodules store a `gitdir:` pointer file. Discovery must accept either, or the buttons vanish inside a perfectly valid worktree.
- **Commands must not stay busy for the operation's lifetime in a way that wedges the UI thread.** Follow the app's existing rule (see `MainWindowViewModel.Launch`'s comment): the await is fine, but the process pump and wait belong off the UI thread, and failures report to `Status` rather than throwing out of a command.
- **A pull's incoming files reach the watcher and rebuild the tree.** Expected and correct; just be aware that expansion state resets, and don't mistake it for a bug introduced here.

## Open Decisions
1. **Name of the "show +Git instead of the pair" flag** — one VM boolean drives the swap (see Acceptance Criteria for its truth table). Default: a single computed property; name it to taste.
2. **Whether the tooltip text lives in VM properties or as literals in XAML** — the pull/push tips are state-dependent (busy vs. no solution) and so must be VM-computed; the `+Git` tip may be static. Default: mirror `OutputTabViewModel.StopTip`, VM-side, for all three.
3. **How the echoed command header is separated from previous output** — a leading blank line, a rule, or nothing. Default: a blank line before every echo but the first.
4. **Whether `GitService` command entry points are one `RunAsync(args…)` or three named methods** (`PullAsync`/`PushAsync`/`InitAsync`). Default: one runner plus thin call sites in the VM; three named methods are fine if they read better.

## Acceptance Criteria
- [ ] With a solution open inside a git repository, the top bar shows an enabled pull button and an enabled push button immediately to the right of the solution name, using MDI `\U000F0045` (down) and `\U000F005D` (up).
- [ ] With a solution open that has no `.git` in its directory or any ancestor, the pair is replaced by a single **+Git** button.
- [ ] With **no** solution open, the pull/push pair is shown *disabled* — not hidden, and not replaced by **+Git** — each carrying a tooltip naming the reason.
- [ ] Repository discovery returns the nearest ancestor directory (starting at the solution's own directory) that contains a `.git` entry, accepting either a directory or a file, and returns "no repository" when none exists up to the filesystem root.
- [ ] Pull and push run their command with that discovered repository root as the working directory — not the solution directory, when the two differ.
- [ ] Clicking pull or push activates a tab whose `TabId` is a fixed `git:`-namespaced key and whose header is `Git - Output`; a second click of either reuses that same tab rather than opening another.
- [ ] The Git tab's buffer is **appended** to across operations, never cleared: a pull followed by a push shows both transcripts, each preceded by an echo of the command that produced it.
- [ ] While any of the three git operations is in flight, all three commands report `CanExecute` false, and each disabled button's tooltip states that an operation is running.
- [ ] `IdeDirectories.Pruned` still contains `.git`, and `SolutionWatcher` gains no `.git`-related special case.
- [ ] A `git init` performed outside the IDE while MiniIde is unfocused causes the bar to show pull/push once the window is next activated; deleting `.git` externally likewise reverts it to **+Git** on the next activation.
- [ ] A successful in-app **+Git** flips the bar to pull/push without requiring the window to lose and regain focus.
- [ ] The git child process runs with `GIT_TERMINAL_PROMPT=0`.
- [ ] `git` being absent from `PATH` surfaces as a message on the status bar and leaves the app usable — no unhandled exception.
- [ ] The shipped icon font contains both new glyphs (`IconFontCoverageTests` passes).

## Implementation

### 1. Add `Services/GitService.cs` with repository discovery
The bar's whole state depends on one question — *is this solution inside a repo, and where is its root* — and the answer must be cheap enough to ask on every window activation. Answer it from the filesystem alone rather than shelling out to `git rev-parse`, so the check costs no process launch and still works on a machine with no git installed. Expose a static discovery method taking a start directory and walking parents until it finds an entry named `.git` (accepting a file *or* a directory, per Constraints), returning the containing directory or null at the root. Keep it pure and static — it is the one part of this feature with real unit tests.

### 2. Add the git command runner to the same service
Mirror `NuGetService.RestoreAsync` closely: `UseShellExecute = false`, `CreateNoWindow`, both standard streams redirected and pumped line-by-line into an `Action<string> log`, `await WaitForExitAsync(ct)`, return the exit code. Differences from the exemplar: the file name is `git`, the working directory is the repository root the caller passes, arguments come in via `ArgumentList` (never a joined string), and `GIT_TERMINAL_PROMPT=0` goes into `psi.Environment`. Like `RestoreAsync`, it throws on launch failure and returns a non-zero code for a command that ran and failed — the caller distinguishes those.

### 3. Give `MainWindowViewModel` the repo/busy state
Add an observable repository-root property (null when there is no solution or no repo) and an observable "a git operation is running" flag. Both must re-notify the three commands' `CanExecute` and the tooltip properties — use the CommunityToolkit `NotifyCanExecuteChangedFor` / `NotifyPropertyChangedFor` attributes the file already leans on for `StartupProject` and `IsRunLive`. Derive from the repo-root property the booleans the view binds: "we are in a repo", and the one that decides whether **+Git** replaces the pair. That second one is true only when a solution is open *and* it is not in a repo — which is what makes the no-solution case fall through to the disabled pair rather than to **+Git**.

### 4. Add a `RefreshGitState` recomputation and call it from the three moments that matter
One private method: take `Path.GetDirectoryName(Solution.SolutionPath)`, run discovery, assign the result (assign null outright when no solution is open). Call it from `OpenSolutionAsync` after the tree loads, from `RefreshFromDiskAsync` (the window-`Activated` safety net — this is what satisfies the externally-created/deleted-repo criterion), and at the end of a successful in-app `git init`. Do **not** wire it to `Watcher.Changed`; see Constraints.

### 5. Add the Git output-tab resolver
Copy the shape of `ResolveNuGetOutput` exactly: get-or-create through `GetOrCreateOutputTab` on a fixed `git:` id with header `Git - Output`, set it as `ActiveTab`, return it. Passing no starter is deliberate — that leaves `HasRun` false, so the tab shows no run strip, matching the NuGet tabs.

### 6. Add the three commands and their shared execution path
Write one private async helper that every command funnels through: resolve-and-activate the tab, append the echoed command line (`> git pull`, etc.) with the separator from Open Decisions, set the busy flag, await the runner, write an outcome line to `Status` naming the command and the exit code, and clear the busy flag in a `finally` so a throw can't leave the buttons dead. Wrap the launch in a try/catch that reports a missing-`git` failure to `Status`.

Pull and push pass the discovered repository root; **+Git** passes the *solution* directory (it can only be offered when no ancestor is a repo, so there is no nesting hazard) and, on exit code zero, calls `RefreshGitState` from step 4.

Guard each command's `CanExecute`: pull and push need a repo and no operation in flight; **+Git** needs a solution, no repo, and no operation in flight. Add the reason-specific tooltip properties alongside, following `OutputTabViewModel.StopTip` — a disabled button with no reason on it is a dead control.

### 7. Add the two glyphs to `ActionIcon` and regenerate the font
Add the pull and push constants with the codepoints named in Constraints, keeping the existing one-line `// mdi-name` comment convention. Then run `cd tools && npm run subset`. The subset script and the guard test both scan `src/MiniIde/**/*.cs` for `\U000FXXXX` escapes, so the constants must land before the script runs.

### 8. Put the buttons in the top bar
In `Views/MainWindow.axaml`, column 0 of the solution bar's grid currently holds the solution-name `TextBlock` and its `ContextMenu`. Wrap that `TextBlock` and a new button group in a horizontal `StackPanel` so the buttons sit immediately to its right — leave the `ContextMenu`, the `DoubleTapped` handler, and the font size on the `TextBlock` itself, not on the new parent, or the solution-name context menu and double-click-to-open both change target.

The pull/push pair and the **+Git** button are two mutually exclusive states of one slot — the same "one slot, two states, never both" rule the output tab's Stop/Run pair already documents — so drive their `IsVisible` from the single boolean of step 3 and its negation. Bind each button's `Command` and `ToolTip.Tip` to the VM; render the arrows as `TextBlock`s with `FontFamily="{StaticResource IconFont}"`, as the context-menu icons do. Keep them visually subordinate to the solution name — small, flat, no heavy chrome.

### 9. Test the discovery walk
Add `GitServiceTests` in the style of the existing service tests (real temp directories, as `SolutionWatcherTests` does): a `.git` directory in the start directory; a `.git` directory two levels up; a `.git` *file* rather than a directory; and a tree with no `.git` anywhere. The command runner is not unit-tested — it is a process launch, and the manual plan covers it.

### 10. Write `docs/git.md` and cross-reference it
One topic per file, fragments over prose. Record: repository discovery is a filesystem walk, not `git rev-parse` (no process, works without git installed); `.git` stays in `IdeDirectories.Pruned` and therefore *cannot* signal, which is why the state is polled at solution-open / window-activation / post-init; `GIT_TERMINAL_PROMPT=0` and what it buys; the shared `git:` tab and its append-with-echo transcript; and that a pull's file burst rebuilds the tree by the ordinary structural path. Add a single cross-referencing line to `docs/disk-watching.md` under its pruning discussion so a reader there learns why `.git` has a second mechanism.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes, including `IconFontCoverageTests` and the new `GitServiceTests`.
- [ ] Open MiniIde with no solution: the pull and push buttons are visible and greyed; hover each and confirm the tooltip names the missing solution.
- [ ] Open this repository's own `MiniIde.slnx`: the pair becomes enabled. Click pull — the **Git - Output** tab opens and activates, shows the echoed `> git pull`, and streams git's output; both buttons are greyed for the duration and re-enable on exit.
- [ ] Immediately click push: the *same* tab is reused (no second Git tab in the strip), the pull transcript is still above, and the push echo and output are appended below it.
- [ ] Copy a solution into a folder with no repository anywhere above it and open it: the bar shows **+Git**. Click it, watch `git init` run in the Git tab, and confirm the bar switches to pull/push without alt-tabbing away.
- [ ] With that solution open, delete its `.git` folder from Explorer, alt-tab to another app and back: the bar reverts to **+Git**. Re-create it with `git init` from a terminal, alt-tab away and back: the bar shows pull/push again.
- [ ] Put a solution in a subdirectory of a repository (e.g. `<repo>/src/Thing.slnx`) and open it: the pair is shown, and a pull reports work done against the outer repository, not a new one.
- [ ] Point a repository at an HTTPS remote requiring credentials that the machine cannot supply, and pull: confirm the operation terminates with an error in the tab and the buttons re-enable, rather than hanging indefinitely.
- [ ] Temporarily rename `git.exe` off `PATH` (or run from a shell with a stripped `PATH`) and click pull: a message appears on the status bar, the app stays responsive, and the buttons re-enable.
- [ ] Pull a commit that adds and deletes files, and confirm the solution tree picks them up — the ordinary structural refresh, unchanged by this ticket.
- [ ] Regression: the solution-name context menu still opens on right-click and double-clicking the name still opens the solution file, both unaffected by the new `StackPanel` wrapper.
- [ ] Regression: a project run still opens its own numbered output tab, and the NuGet restore tab is still separate from the Git tab.

## Learnings

### Architectural decisions

- **Open Decisions, as resolved.**
  1. The swap flag is `MainWindowViewModel.OffersGitInit` — `SolutionDirectory is not null && RepositoryRoot is null`. Naming it after what it *offers* rather than after a state ("IsNotInRepo") is what makes the no-solution case read correctly at the binding site: `IsVisible="{Binding !OffersGitInit}"` on the pair.
  2. All three tooltips are VM-computed, per the default. `PullTip`/`PushTip` share one `GitTip(enabledText)` helper that walks the same reason ladder (busy → no solution → no repo → enabled); `InitTip` only has the busy branch. One ladder means the two arrows can't disagree about why they're greyed.
  3. Blank line before every echo but the first, per the default.
  4. One `GitService.RunAsync(root, arguments, log, ct)` plus three thin `[RelayCommand]` call sites, per the default.
- **`SolutionDirectory` had to become an observable VM property.** The truth table needs to distinguish "no solution" from "solution, no repo", and `SolutionService.SolutionPath` raises no change notification — so a computed property reading it would never re-evaluate. `RefreshGitState` assigns both `SolutionDirectory` and `RepositoryRoot`, and the two carry the `[NotifyPropertyChangedFor]` / `[NotifyCanExecuteChangedFor]` attributes between them.
- **`RefreshGitState` runs *first* in `RefreshFromDiskAsync`, before the awaits.** That method's body is wrapped in one try/catch that swallows to `Status`; putting the git poll after `Workspace.ReconcileWithDiskAsync()` would mean a failing reconcile silently stops the bar from ever noticing an external `git init`. It is a cheap synchronous `Directory.Exists` walk, so there is no cost to going first.
- **The blank-line separator reads `tab.Document.TextLength`, not a VM bool.** A `_gitOutputWritten` field would be wrong across a tab close-and-reopen (it would put a leading blank line at the top of a fresh tab); the document's own length is the honest source of truth. `Append` posts to the UI thread, but the echo of one operation is always drained before the user can start the next (the buttons are disabled in between), so the read is never torn.
- **`RunGitAsync` returns `int?`** — the exit code, or null when git could not be launched. Only `GitInitAsync` consults it (a non-zero init must leave **+Git** on the bar); pull and push discard it. Collapsing it to `bool` would have lost the "ran and failed" vs. "never started" distinction that `git init` needs.

### Problems encountered

- **The acceptance criterion "*`git` being absent from `PATH` surfaces as a message on the status bar*" cannot be met as written: the app has no status bar.** `MainWindowViewModel.Status` is written from ~20 places (`OpenSolutionAsync`, `Launch`, `RenameSymbolAsync`, every context-menu handler in `MainWindow.axaml.cs`) and is rendered **nowhere**. The three `{Binding Status}` in `MainWindow.axaml` (lines 397 / 448 / 492) all sit inside `DataContext="{Binding Find}"` / `{Binding NuGetVm}` / `{Binding Problems}` scopes and resolve to those panels' own `Status` properties. The root grid is `RowDefinitions="Auto,Auto,*"` with row 0 unused — there is no status row. Pre-existing, not introduced here; it silently swallows every failure message the app already writes. **Worth its own ticket.** Mitigated here by also appending the launch-failure message into the Git tab (`tab.Append($"[{ex.Message}]")`), which is what actually makes the missing-git case visible — verified: the tab shows `[An error occurred trying to start process 'git' ...]`, both buttons re-enable, and the app stays responsive.
- **A scripted focus swap does not exercise `Window.Activated`.** `SetForegroundWindow` from a separate process is ignored under Windows' foreground lock, so the first attempt at the "delete `.git` externally, alt-tab away and back" test showed the bar *not* reverting — which looked exactly like broken `Activated` wiring. `ShowWindow(SW_MINIMIZE)` + `SW_RESTORE` forces a real cycle, and both directions then worked immediately. Recorded in `docs/avalonia.md`.

### Interesting tidbits

- **`GIT_TERMINAL_PROMPT=0` was verified end-to-end, not assumed.** Pointing a throwaway repo at an auth-requiring HTTPS remote *with `credential.helper` set to empty* isolates the test to exactly this variable — no Git Credential Manager in the way, so git must fall back to a terminal prompt. The tab showed `fatal: could not read Username for 'https://github.com': terminal prompts disabled` and the operation ended in seconds. That empty-helper trick is the way to test this deterministically without a network hang or a GCM dialog.
- **The codepoints were re-verified against the pinned font's `post` table** per `tools/README.md`, not taken on faith from the ticket: `arrow-down` → `U+F0045`, `arrow-up` → `U+F005D`. The cross-check that proves the lookup reads the same table the app renders from is `folder` → `U+F024B`, which matches the existing `ActionIcon.Explorer`.
- **A pull's deletions do reach the tree.** Confirmed by pushing a commit that deleted `GrepHit.cs` and pulling from the app: the node disappeared on the ordinary structural refresh, with no git-specific tree code. Files a pull adds *outside* any project (e.g. at the solution root) correctly do not appear — the tree is project-rooted.
- **Wrapping the solution-name `TextBlock` in a `StackPanel` is safe** because the `ContextMenu` and `DoubleTapped` stay on the `TextBlock` and the new buttons are its *siblings*, not its children — so neither bubbles up from a button. Verified by hand: the context menu still opens with all items enabled, and double-clicking the name still opens the `.slnx`.

### Workarounds / limitations

- **Repo state is focus-granular, permanently.** A `git init` in a terminal while MiniIde is focused is invisible until the window next loses and regains focus. That is the agreed design (`.git` is pruned; a second watcher was rejected in Scope), not a defect to fix later.
- **No cancel path.** A wedged git operation leaves all three buttons disabled with no way out but restarting the app. `GIT_TERMINAL_PROMPT=0` removes the one realistic cause; Git Credential Manager's own GUI dialog is still possible on an HTTPS remote, but that is a window the user can answer or dismiss.

### Related areas affected

- `docs/disk-watching.md` gained one line under its pruning bullet noting that pruning is by *segment* (so the `.git` node itself is pruned) and pointing at `docs/git.md`.
- `docs/avalonia.md` gained the `SetForegroundWindow` / `Activated` bullet.
- `IdeDirectories.Pruned` and `SolutionWatcher` are **untouched**, as required.
- `docs/tickets/run-tab-restart-strip.md` (still open) rewrites the `OutputTabViewModel` strip. The Git tab is constructed with no starter, so it answers `HasRun` false and shows no strip either way — no collision.

### Rejected alternatives

- **`git rev-parse --show-toplevel` for discovery.** Correct, but it costs a process launch on every window activation and returns nothing useful on a machine with no git installed. The filesystem walk is a handful of `Directory.Exists` calls and answers both cases.
- **A `_gitOutputWritten` bool for the blank-line separator** — wrong across a tab close-and-reopen; see Architectural decisions.
- **Adding a real status bar as part of this ticket.** In scope for the app, out of scope for this ticket; noted above as its own follow-up instead.
