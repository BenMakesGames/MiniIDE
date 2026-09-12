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

- `.git` is in `IdeDirectories.Pruned` and **stays there**. `SolutionWatcher.IsPruned` tests every segment of
  the relative path, so the `.git` node itself is pruned, not merely its contents.
- ⇒ no repository creation or deletion can ever arrive as a `DiskChangeSignal`. Anything subscribing to
  `Watcher.Changed` to learn repo state is dead code.
- It stays pruned because a `git pull` writes hundreds of files under `.git` (objects, refs, index, logs);
  feeding those to the watcher turns every fetch into a structural-signal storm and a tree rebuild per burst.
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

A pull's incoming files reach the watcher as an ordinary structural signal and rebuild the tree — expected,
and `LoadTreeAsync`'s `Tree.Clear()` resets expansion and scroll along with it. That is
`docs/tickets/incremental-solution-tree-refresh.md`, not a git bug. The pull is deliberately **not** muted via
`SolutionWatcher.MuteWrites`: muting is only for writes that refresh the view themselves.
