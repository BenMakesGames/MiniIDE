# Defer Visible Disk Reconcile While the Window Is Unfocused

## Context
**Current behavior**: The OS change feed (`SolutionWatcher` → `DiskChangeSignal` → `MainWindowViewModel.OnDiskChanged`) reacts to every debounced burst *regardless of window focus*. Two of its surfaces are **eager and visible**: on a structural/overflow signal `ApplyDiskChangeToViewAsync` rebuilds the whole solution tree (`LoadTreeAsync`, which `Tree.Clear()`s and resets expansion), and on any signal it live-pushes the active tab (`RevalidateFromDiskAsync`). So when an external tool — an agent, `git`, a bulk file move — churns the same files repeatedly while MiniIde sits in the background, the tree collapses and the active buffer reloads once *per burst*, even though nobody is looking. The other two tracks are already lazy: the per-tab `IsStale` flag is consumed only on tab activation, and the workspace pending-set is drained only by the next semantic query (which can't happen without focus). Those already coalesce N external edits into one catch-up; only the two eager surfaces don't.

**New behavior**: While the window is inactive, the two eager surfaces go quiet — disk changes only mark the lazy tracks — and the catch-up happens **once** when the window regains focus. When the window is active, behavior is unchanged (live push into the active tab and real-time tree refresh, the read-alongside-an-agent payoff). Net: alt-tab away from MiniIde while an agent rewrites files, and on return you get a single tree rebuild (if the file set changed) and a single active-tab reload, not one of each per burst.

## Prerequisites
- `docs/tickets/complete/2026-07-16 os-driven-disk-reconcile.md` — built the entire system this modifies (`SolutionWatcher`, `DiskChangeSignal`, the two dirty tracks, the eager/lazy split, `RefreshFromDiskAsync` as the focus safety-net). This ticket **revises its Open Decision 5** (see Constraints).

## Scope
### In scope
- A window-active signal reaching `MainWindowViewModel`, fed by the window's `Activated` (already wired) plus a new `Deactivated` handler.
- Gating the two eager surfaces in `ApplyDiskChangeToViewAsync` (tree rebuild, active-tab live push) on window-active; the lazy dirty-track pushes stay unconditional.
- A "a structural change was skipped while unfocused" flag, drained by the focus catch-up so the tree rebuilds once on return.
- Extending `RefreshFromDiskAsync` to rebuild the tree when that flag is set (today it never touches the tree — it has relied on the eager path having already done so).

### Out of scope
- **Fixing the tree collapse itself.** `LoadTreeAsync` still `Tree.Clear()`s and resets `TreeNode.IsExpanded`; this ticket only reduces *how often* that happens (once per focus vs. once per burst). Preserving expansion across a rebuild (bind `IsExpanded`, or targeted node insert/remove via the rename feature's `TryReplaceFileNode`) remains the separate follow-up already noted in `os-driven-disk-reconcile`'s Learnings and tracked by `docs/tickets/incremental-solution-tree-refresh.md`.
- **Changing the lazy tracks.** The per-tab `IsStale` flag and the workspace pending-set already defer correctly without focus; leave them, and leave the pre-op/query-time reconcile funnel (`EnsureWorkspaceReadyAsync`) untouched.
- **A "changes pending while away" badge / count in the UI.** The Disk insight panel already surfaces pending paths and stale tabs; no new indicator here.
- **A view-layer (Avalonia.Headless) test project.** Still none — see Constraints on where the testable seam should live.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/disk-watching.md` — the feed's design (best-effort semantics, the two-dirty-tracks model, the eager-vs-lazy split this ticket adjusts). Update it to record that the eager surfaces are now focus-gated.
  - `docs/CLAUDE.md` — one topic per file, fragments over prose (for the doc edit).
- **Related tickets** (context, not a structure to copy): the Prerequisite above — read its Open Decision 5 (live-push-even-when-unfocused) and its "Workarounds / limitations" (the tree-collapse note) before coding.
- **Code anchors** (symbols; verify against source):
  - `ViewModels/MainWindowViewModel.cs` — `OnDiskChanged` (threadpool entry; pushes the lazy tracks then posts to the UI thread), `ApplyDiskChangeToViewAsync` (the two eager surfaces: the `LoadTreeAsync` call guarded on `signal.Structural || signal.Overflow`, and the `ActiveTab … RevalidateFromDiskAsync` live push), `RefreshFromDiskAsync` (the `Activated` catch-up — today marks all tabs stale + `RequestFullRescan` + reloads the active tab + reconciles, but **does not** rebuild the tree), `LoadTreeAsync`, `MarkPathsChanged`/`RequestFullRescan`.
  - `Views/MainWindow.axaml.cs` — the ctor's `Activated += OnWindowActivated;` wiring and `OnWindowActivated` (`await vm.RefreshFromDiskAsync()`). There is currently **no** `Deactivated`/`IsActive`/`WindowState` handling anywhere in the app.
  - `ViewModels/EditorTabViewModel.cs` — `IsStale`, `RevalidateFromDiskAsync` (stat-gated read + `ReloadFromDisk`), consumed lazily by `MainWindowViewModel.OnActiveTabChanged`.
  - `Models/DiskChangeSignal.cs` — `Structural`, `Overflow`, `Paths`, `Reason` (read-only; unchanged).

## Constraints & Gotchas
- **This deliberately revises Open Decision 5 of `os-driven-disk-reconcile`, which chose live-push-even-when-unfocused ("read alongside an agent").** State the reversal explicitly in the doc/comment so it doesn't read as a regression: the payoff only materialises when the user is actually looking at MiniIde, which means it is the active window; unfocused, the same push is pure churn (an agent hits one file many times). The live push and real-time tree survive **for the active-window case** — that is the whole "read alongside an agent" scenario, just correctly gated.
- **The focus catch-up must now own the tree.** Today `RefreshFromDiskAsync` never rebuilds the tree; it works only because the eager path already did while unfocused. Gating the eager path off leaves a hole: a structural change that lands while inactive would otherwise never reach the tree. The skipped-structural flag → `LoadTreeAsync`-on-focus is the fill; without it the tree silently goes stale.
- **Overflow is structural for this purpose.** An overflow while unfocused may have hidden a structural change, so it must set the skipped-structural flag too (mirroring the existing `signal.Structural || signal.Overflow` tree guard). The all-tabs-stale marking on overflow is a lazy-track write and stays unconditional.
- **Threading.** `OnDiskChanged` runs on a threadpool thread and posts the view work to the UI thread; window-active state is read on that UI-thread continuation. Read/write the active-state flag and the skipped-structural flag on the UI thread (both the `Activated`/`Deactivated` handlers and `ApplyDiskChangeToViewAsync`'s continuation run there) — no new lock needed if both live UI-thread-only. Do not read Avalonia `Window.IsActive` off the threadpool.
- **`Activated` can fire before the `DataContext` is wired** (the existing `OnWindowActivated` already guards for this). The new `Deactivated` handler needs the same guard.
- **Expected warnings**: the pre-existing set only (IL3000 in `SyntaxHighlightService`; AVLN5001/`PlaceholderText` if it resurfaces). Introduce no new ones.
- **Build lock**: a running MiniIde locks its output DLL — build to a temp `-o` dir.

## Open Decisions
1. **Where the window-active state lives / the testable seam** — a `bool` property on `MainWindowViewModel` set from the view's `Activated`/`Deactivated`, vs. passing the active-state into `ApplyDiskChangeToViewAsync` as a parameter or `Func<bool>`. Default: a `bool` on the VM (mirrors the existing `Activated → RefreshFromDiskAsync` wiring). Note the parameter/func form is what would make the eager-vs-lazy routing decision unit-testable without a view (there is still no headless-view test project) — worth doing if it's cheap. Implementer's call.
2. **Startup default of the active flag** — `true` (assume focused until told otherwise) vs. `false` (defer until the first `Activated`). Default: `false` is safe — a window launched into the background stays quiet until first activation, and the first `Activated` fires the catch-up anyway. Implementer's call.
3. **Flag naming / shape for the skipped-structural signal** — a plain `bool _structuralMissedWhileUnfocused`, vs. folding it into a broader "pending focus catch-up" concept. Default: a single bool; YAGNI. Implementer's call.

## Acceptance Criteria
- [ ] While the window is inactive, an external structural change (add/remove/rename a `.cs`, or a `.csproj` edit) does **not** rebuild the solution tree; the tree rebuilds exactly once when the window is next activated.
- [ ] While the window is inactive, an external edit to the file shown in the active tab does **not** reload that tab's buffer; the tab shows the new content once the window is next activated.
- [ ] While the window is active, an external structural change rebuilds the tree in real time and an external edit to the active tab's file live-pushes into the buffer — unchanged from today.
- [ ] The lazy tracks are unaffected by focus: an external edit to a file open only in a background tab marks it `IsStale` regardless of window-active state, and it reloads when that tab is next activated; the workspace pending-set / full-rescan flag is marked on every burst regardless of focus.
- [ ] N external edits to the same file while the window is inactive result in a single reload/rebuild on focus-return, not N.
- [ ] After a focus-return with nothing actually changed on disk, no tree rebuild occurs and no buffer is reloaded (the skipped-structural flag was never set; the stamp gate makes the rescan zero reads).
- [ ] A watcher `Overflow` while inactive causes a tree rebuild on the next activation (it is treated as structural for the deferral).

## Implementation

### 1. Feed window-active state into the view model
The eager surfaces need to know whether MiniIde is the active window, and the app currently tracks only `Activated`. In `MainWindow.axaml.cs`, alongside the existing `Activated += OnWindowActivated;`, add a `Deactivated` handler (guarding for an unset `DataContext` like `OnWindowActivated` does) that flips the VM's window-active state off; have `OnWindowActivated` set it on (before or as part of `RefreshFromDiskAsync`). Expose the state per Open Decision 1 — default a `bool` on `MainWindowViewModel`, written UI-thread-only.

### 2. Gate the two eager surfaces on window-active
In `ApplyDiskChangeToViewAsync`, keep the lazy-track writes unconditional (the `IsStale` marking loop, which also covers the overflow "mark every tab" case). Wrap the two eager surfaces so they run only when the window is active: the `LoadTreeAsync` call in the `signal.Structural || signal.Overflow` branch, and the active-tab `RevalidateFromDiskAsync` live push. When the window is **inactive** and the signal is structural or overflow, instead of rebuilding the tree, set the skipped-structural flag (Open Decision 3). Leave `OnDiskChanged`'s lazy-track pushes (`MarkPathsChanged`/`RequestFullRescan`) exactly as they are — they run before the UI post and must stay focus-independent.

### 3. Make the focus catch-up rebuild the tree when a structural change was deferred
`RefreshFromDiskAsync` (wired to `Activated`) already re-arms both dirty tracks and reloads the active tab; it must now also close the tree hole opened by step 2. When the skipped-structural flag is set, call `LoadTreeAsync(Solution.SolutionPath)` (guarding `SolutionPath is not null`, as the eager path does) and clear the flag. Order it so the tree and the active tab are both current before the method returns; keep the existing `RequestFullRescan` + `ReconcileWithDiskAsync`-if-loaded behavior. This is the one-shot catch-up.

### 4. Update the design doc
In `docs/disk-watching.md`, record that the two eager surfaces (tree rebuild, active-tab live push) are gated on window-active, that the lazy tracks are focus-independent, and that the focus catch-up now owns the deferred tree rebuild. Note the revision of `os-driven-disk-reconcile`'s Open Decision 5 (one fragment line, per `docs/CLAUDE.md` style).

### 5. Tests
If Open Decision 1 lands on a testable seam (active-state as a parameter/func into the routing), add coverage that a structural signal with active=false marks the skipped flag and does not invoke the tree rebuild, while active=true does — mirroring the existing test style. If the seam stays view-only (VM bool set by the window), the gating is a manual-GUI item; note it in the Test Plan and lean on the `WorkspaceService`/`SolutionWatcher` tests remaining green (the reconcile mechanics are unchanged). Do not regress the existing `SolutionWatcherTests`/`WorkspaceServiceTests`.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with only the pre-existing warnings; `dotnet test MiniIde.slnx` passes (existing suite green; any new routing test passes).
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`. **Unfocused, active tab**: focus another window, externally edit the file shown in MiniIde's active tab several times → MiniIde's buffer does not change; click back into MiniIde → it now shows the latest content (one reload).
- [ ] **Unfocused, structural**: with MiniIde in the background, externally add a `.cs` file and rename another → the tree does not move; focus MiniIde → the tree rebuilds once and reflects both changes. Confirm it does not rebuild again on a second focus with nothing further changed.
- [ ] **Focused, unchanged behavior**: with MiniIde as the active window, externally edit the active file and add a file → the buffer live-updates and the tree refreshes in real time, exactly as before.
- [ ] **Background tab (lazy track)**: open two files, switch away from one, unfocus MiniIde, edit that background file externally, refocus, then activate the tab → it shows the new content; a third unchanged tab shows no reload/flicker.
- [ ] **Burst while away**: run an agent (or a script) rewriting many files while MiniIde is unfocused → on refocus the app catches up once and lands in a correct state (spot-check a few files, the tree, and the active buffer); no per-burst churn was visible while away.
- [ ] **No-op focus**: alt-tab out and back with nothing changed on disk → no tree rebuild, no buffer reload (Disk panel's documents-read stays flat, documents-stat'd may climb).
- [ ] Regression: F12/Shift+F12/Problems/rename, global search (Ctrl+Shift+F), and "Reload solution" behave as before; the Disk insight panel still logs signals and shows pending/stale state while unfocused.
