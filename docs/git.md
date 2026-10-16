# Git (pull / push / init)

Three buttons in the solution bar and one shared output tab. Not a VCS integration — no status, branch,
stage, commit, diff, blame, or ahead/behind anywhere.

## Repository discovery

- `GitService.FindRepositoryRoot(startDirectory)` — walk parents from the solution's own directory, first
  one holding a `.git` wins, null at the filesystem root.
- **Filesystem walk, not `git rev-parse --show-toplevel`.** Runs on every window activation, so it must cost
  no process launch — and it still answers correctly on a machine with no git installed.
- **A `.git` *file* counts as much as a directory.** Linked worktrees and submodules store a `gitdir:`
  pointer file; rejecting those blanks the buttons inside a valid worktree.
- Nearest ancestor wins, so a nested repository is its own, not its parent's.
- The root is the working directory for pull/push. A solution in `<repo>/src` pulls the outer repository —
  the solution directory is only ever used by `git init`, which is offered only when no ancestor is a repo
  (so it cannot nest one).

## Why the state is polled, never signalled

- `SolutionWatcher` only reacts to solution/project/launch-settings files (`docs/disk-watching.md`), so no
  repository creation or deletion can ever arrive as a signal. Anything subscribing to `Watcher.Changed` to
  learn repo state is dead code.
- Don't widen it to `.git`: a `git pull` writes hundreds of files there (objects, refs, index, logs).
- So `MainWindowViewModel.RefreshGitState` is called at exactly three moments: solution open, window
  activation (`RefreshFromDiskAsync`), and our own successful `git init`.
- Focus-time is the agreed granularity for external changes — a `git init` in a terminal shows up on the next
  alt-tab back, not live. A dedicated `FileSystemWatcher` on `.git` was rejected: it would only ever see the
  solution root and miss the ancestor-repo case the walk handles.

## Bar state — one slot, two states

`OffersGitInit = solution open && not in a repo`.

| | pull/push | +Git |
|---|---|---|
| no solution | shown, **disabled** | hidden |
| in a repo | shown, enabled | hidden |
| solution, no repo | hidden | shown |

No solution falls through to a disabled pair, not to **+Git** — offering to initialize a repository nowhere
is worse than a greyed button. Every disabled state carries a reason-specific tooltip.

## Running a command

- `GitService.RunAsync` mirrors `NuGetService.RestoreAsync`: `UseShellExecute = false`, `CreateNoWindow`,
  both streams pumped line-by-line into an `Action<string> log`, `await WaitForExitAsync`, returns the exit
  code. Throws on launch failure (git not on `PATH`) — the caller reports that to the status bar.
- Arguments via `ArgumentList`, never a joined string.
- **`GIT_TERMINAL_PROMPT=0`** in `psi.Environment`. No console is attached, so a credential prompt would hang
  forever — and with all three buttons disabled for the operation's duration, that is an unrecoverable UI
  state. This makes git fail fast into the tab instead. (`Environment` requires `UseShellExecute = false`,
  which the redirects need anyway.) Git Credential Manager may still raise its own GUI dialog for an HTTPS
  remote — a window the user can answer or dismiss, not a hang.
- There is no cancel path and no Stop button; the env var is the mitigation, not a kill.

## The Git tab

- One shared `git:`-namespaced tab, header `Git - Output`, via `GetOrCreateOutputTab` — same shape as
  `ResolveNuGetOutput`. Dedup keys on the id, so a project named "Git" can't collide with it.
- Constructed **without a starter** ⇒ `HasRun` false ⇒ no run strip, matching the NuGet tabs.
- **Appended to, never cleared.** A pull followed by a push shows both transcripts; each is legible because
  its own echoed `> git pull` sits above it, with a blank line before every echo but the first.
- One `IsGitBusy` flag shared by all three commands, cleared in a `finally` — a throw must not leave the
  buttons permanently dead.

## Side effects

A pull that changes a solution or project file reaches the watcher and reloads the project list — cards
update in place, so selections and run buttons survive it.
