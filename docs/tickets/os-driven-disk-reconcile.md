# OS-Driven Disk Reconcile (FileSystemWatcher + Stamp-Gated Fallback)

## Context
**Current behavior**: The view is kept in sync with disk by *polling*. `WorkspaceService.ReconcileWithDiskAsync` runs on every window focus (via `MainWindowViewModel.RefreshFromDiskAsync`, wired to the window's `Activated`) and before every semantic query (via `EnsureWorkspaceReadyAsync`). Each run: `ComputeManifestFingerprintAsync` walks every project directory and content-hashes every `.csproj` to detect structural drift, then — if unchanged — `OverlayDiskTextAsync` reads **every solution document in full** off disk and `ContentEquals`-compares it against the snapshot. Separately, `ReloadDriftedTabsAsync` re-reads **every open editor tab** from disk. So each focus/op is O(bytes in the solution) of disk reads even when nothing changed. The read-only ticket's own "Cost / limitations" Learnings flag this and name a `FileSystemWatcher`-driven incremental cache as the intended future optimization — this ticket is that optimization.

**New behavior**: An OS file-change feed drives reconciliation instead of polling. A `FileSystemWatcher` over the solution root reports exactly which paths changed; those feed two **independent** dirty tracks — a per-tab "stale" flag and the snapshot's pending-overlay set. Visible surfaces (the active tab, the solution tree) reflect a change on the event; everything else revalidates lazily — a background tab when it's next activated, the semantic snapshot when the next query drains its pending-set. A per-path `(LastWriteTimeUtc, Length)` **stamp** gates every read, so an unchanged file costs a `stat`, not a full read; file *content* remains the arbiter of whether to actually fork the snapshot or reload a buffer. The existing whole-solution reconcile survives, stamp-gated, as the **cold-start and overflow-recovery fallback**. Net: idle alt-tab into a loaded solution drops from O(bytes) reads to O(changed) with a bounded `stat`-walk, and the common "nothing changed" case does zero reads. Global search is untouched — it stays on the grepper (see Out of scope).

## Prerequisites
- `docs/tickets/complete/2026-07-16 read-only-ide-over-authoritative-disk.md` — introduced `ReconcileWithDiskAsync`, `OverlayDiskTextAsync`, the `ComputeManifestFingerprintAsync` structural fingerprint, `ReloadDriftedTabsAsync`, and the "content-hash, never mtime" invariant this ticket deliberately refines (see Constraints). Its "Cost / limitations" Learnings scope this work.
- `docs/tickets/complete/2026-07-17 safe-rename.md` — the rename apply (`RenameService.ApplyToDisk`) is the app's one first-party multi-file *write*; this ticket must mute the watcher around it so the app's own writes don't churn the reconcile.

## Scope
### In scope
- A new watcher concern: a `FileSystemWatcher` over the solution root, pruned to `IdeDirectories.Pruned`, debounced, raising a coalesced "these paths changed / structural change / overflow" signal.
- Per-path `(LastWriteTimeUtc, Length)` stamps as a read pre-filter in the workspace reconcile and the open-tab reload; content stays the fork/reload arbiter.
- Reworking `WorkspaceService.ReconcileWithDiskAsync` to consume a pending-set (overlay only changed docs) with the current whole-solution pass demoted to cold-start/overflow fallback.
- Routing in `MainWindowViewModel`: watcher signal → per-tab stale flags + eager reload of the *visible* tab + tree-node refresh for structural changes + the snapshot pending-set; lazy reload of a background tab on activation.
- Muting the watcher around `RenameService.ApplyToDisk`.
- Tests for the stamp gate, the pending-set overlay, lazy tab revalidation, and overflow fallback.

### Out of scope
- **Moving global search onto the OS.** `SearchService`/`BenMakesGames.FileGrepper` stay exactly as they are. The Windows Search Index answers *find-my-documents* (word/phrase full-text, indexed-locations-only, eventually-consistent, its own scope rules) — not *code search* (exact + `NonBacktracking` regex, 1-based line+column, `IdeDirectories`-pruned, live, streamed). It cannot return line/column and cannot do regex/substring; do not route search through it. Note this explicitly so a future pass doesn't "optimize" it.
- **USN Change Journal.** The heavier, durable, survives-restart alternative (answers "what changed while MiniIDE was closed?"). Out of scope until a concrete need appears; `FileSystemWatcher` is the right altitude here.
- **Watching project directories that live outside the solution root.** The watcher roots at the solution directory (matching the manifest walk's assumption). Projects outside it are still caught by the fallback reconcile on the next pre-op; do not add multi-root watching now.
- **The structural-reload-vs-diagnostics race** noted as a follow-up in the read-only ticket's Learnings. More reload triggers make it marginally more reachable, but fixing it is a separate ticket (see Constraints).
- **A view-layer (Avalonia.Headless) test project.** Still none.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/roslyn.md §.slnx / §Cold-start strategy` — the cheap `SolutionPersistence` metadata parse the manifest fingerprint already uses; unchanged here.
  - `docs/CLAUDE.md` — one topic per file, fragments over prose (for any doc edits).
- **Related tickets** (context, not structure): the two Prerequisites above — read the read-only ticket's Constraints (content-hash-not-mtime) and Learnings (reconcile cost, structural-reload disposes `_ws`) before coding.
- **Code anchors** (symbols, verify against source):
  - `Services/WorkspaceService.cs` — `ReconcileWithDiskAsync` (the `_lock`-guarded poll entry; today: fingerprint → full-reload-or-`OverlayDiskTextAsync`), `OverlayDiskTextAsync` (the per-doc full read + `ContentEquals` + `WithDocumentText`), `ComputeManifestFingerprintAsync`/`EnumerateSourceFiles`/`HashFile`, `LoadSolutionAsync`, `ReloadIfLoadedAsync`, `IsLoaded`, `_manifestFingerprint`, `FindDocument`, `Dispose`.
  - `ViewModels/MainWindowViewModel.cs` — `RefreshFromDiskAsync` (the `Activated` handler), `ReloadDriftedTabsAsync` (re-reads every open tab), `ReloadWorkspaceAsync` ("Reload solution" snapshot half), `EnsureWorkspaceReadyAsync` (pre-op funnel), `RenameSymbolAsync` (its freshness gate reads the active file and string-compares to `viewText`), `OnActiveTabChanged`-style hook (the `[ObservableProperty] _activeTab` partial — where lazy tab reload-on-activate belongs), `OpenSolutionAsync`/`CloseTabAsync` (watcher start/stop lifecycle), `Tabs`.
  - `Views/MainWindow.axaml.cs` — the `MainWindow` constructor (`Activated += OnWindowActivated` is wired here) and `OnWindowActivated`.
  - `ViewModels/EditorTabViewModel.cs` — `ReloadFromDisk(diskText)` (posts to the UI thread, no-ops when `Document.Text == diskText`).
  - `Services/RenameService.cs` — `ApplyToDisk` (the multi-file write + `File.Move` to mute around).
  - `Models/IdeDirectories.cs` — `Pruned` (the watcher's exclusion set; already shared by the tree walk and search).
  - `Services/SearchService.cs`, `src/BenMakesGames.FileGrepper/` — the code-search path that stays untouched.

## Constraints & Gotchas
- **The stamp gate consciously refines the read-only ticket's "content-hash, never mtime" invariant — do not treat it as a silent regression.** That ticket rejected mtime because an operation-write can bump mtime without a meaningful change, and a same-size overwrite changes content the length can't reveal. The stamp here is a *pre-filter for whether to read*, **not** the drift decision: content (`ContentEquals` / `Document.Text ==`) still decides whether to fork/reload, so an mtime-bumped-but-unchanged file still forks nothing (read, compare, skip). The only behavior that changes: a content change that preserves **both** mtime **and** byte length would be missed *by the fallback poll*. Under normal operation the `FileSystemWatcher` fires on the write itself regardless of mtime/size, so this exotic double-coincidence is caught in the primary path — the relaxation is confined to cold-start/overflow. Keep `Length` in the stamp (not mtime alone) so same-size-different-mtime and different-size cases are both caught cheaply. This tradeoff is standard (every editor keys "changed on disk" on mtime+size); make it a deliberate, documented Open Decision, not an accident.
- **`FileSystemWatcher` is best-effort, so the poll cannot be deleted — only made rare.** Its internal buffer can overflow under a burst (an agent rewriting hundreds of files, a `git checkout`), raising `Error`; events can also be missed under load. On overflow/error, fall back to a full stamp-gated reconcile (i.e. "rescan everything"), which is exactly the demoted whole-solution pass. The watcher makes that pass rare; it does not replace it.
- **Two independent dirty tracks, not one shared set.** A single shared set that every consumer clears would let the snapshot's drain wipe the "this unopened tab is stale" mark. Keep them separate: a per-tab `stale` flag (consumed on tab activation) and the workspace pending-overlay set (consumed-and-cleared at the next reconcile). One event feeds both.
- **Mute the app's own writes.** `RenameService.ApplyToDisk` writes many files and moves one; each fires the watcher. `RenameSymbolAsync` already reconciles the view explicitly afterward, so the watcher events would be redundant *and* could fight that explicit refresh. Suppress watcher-driven reconciliation for the app's own writes (a path-set or a short time window around the apply). NuGet's `.csproj` write (`NuGetService`) is a structural change the watcher *should* catch — do not blanket-mute all first-party writes, only the rename apply which does its own follow-up refresh.
- **Threading / UI affinity.** `FileSystemWatcher` raises events on a threadpool thread. Debounce off the UI thread; marshal every `Document` mutation (tab reload) onto the UI thread (mirror `EditorTabViewModel.ReloadFromDisk` / `OutputTabViewModel.Append`). Guard the pending-set and stamp map with the existing `WorkspaceService._lock` (or the watcher's own lock) — the reconcile already runs under `_lock`.
- **Debounce/coalesce.** Agents and git write in bursts. Coalesce events over a short window before signalling, so one burst yields one reconcile, and a file touched N times is one pending entry.
- **Structural events.** Create/Delete/Rename events (and a `.csproj` change) are structural → the next reconcile must do a real `LoadSolutionAsync` rebuild (`WithDocumentText` cannot add/remove documents), and the tree must refresh. Changed-content events on existing files are the cheap overlay path. Reuse the existing structural-vs-content split; the watcher just supplies the trigger instead of the manifest walk. The manifest fingerprint may remain as the fallback's structural check.
- **Watcher lifecycle.** Start on solution open, tear down and restart on "Reload solution" and on opening a different solution, dispose on window close (extend `WorkspaceService.Dispose` or the new service's `Dispose`). A running MiniIDE already locks its own output DLL — unrelated, but build to a temp `-o` dir as before.
- **Pre-existing race (do not fix here, just don't worsen blindly).** The read-only ticket's Learnings note that a structural reload disposes and recreates `_ws`, which can trip a concurrent `GetDiagnosticsAsync`. More reload triggers make it marginally more reachable; leave the fix to its own follow-up but avoid adding gratuitous extra structural reloads (debounce covers most of this).
- **Expected warnings**: the three pre-existing ones (CS0618 `Workspace.WorkspaceFailed` — now suppressed via `RegisterWorkspaceFailedHandler`; IL3000 in `SyntaxHighlightService` — suppressed; AVLN5001/`PlaceholderText`). Introduce no new ones.

## Open Decisions
1. **Where the watcher lives** — a dedicated service (e.g. `SolutionWatcher`/`DiskWatchService`) raising a debounced `Changed(paths, structural, overflow)` event that `MainWindowViewModel` routes, vs. folding it into `WorkspaceService`. Default: a dedicated service — keeps the OS handle + background-thread lifetime out of `WorkspaceService`, and matches the app's one-concern-per-service style. Implementer's call.
2. **Debounce window** — how long to coalesce a burst before reconciling. Default: a short window (≈100–300 ms); eyeball responsiveness vs. thrash during a large agent write. Implementer's call.
3. **Self-write mute mechanism** — an expected-path set consumed as events arrive, vs. a time-window suppression around `ApplyToDisk`. Default: whichever is simpler to make correct; a path-set is more precise, a window is less code. Implementer's call.
4. **Per-tab stale flag home** — a `bool` on `EditorTabViewModel` consumed by an `OnActiveTabChanged` reload, vs. tracking stale tabs in a set on the VM. Default: a flag on the tab (the tab already owns `ReloadFromDisk`); reload it when it becomes `ActiveTab`. Implementer's call.
5. **Visible-tab liveness while unfocused** — push a reload to the active tab the instant its file changes (live, even without a focus change), vs. only on the next `Activated`. Default: push live to the active tab (that's the read-alongside-an-agent payoff); background tabs stay lazy. Implementer's call.

## Acceptance Criteria
- [ ] With a solution loaded and the watcher running, editing a **single** source file externally causes the next reconcile to read/overlay only that file (and its stamp) — not every document in the solution. (Observable in a test via the reconcile's read set or a seam; behaviorally: a large solution stays responsive on focus after a one-file change.)
- [ ] A file whose `(LastWriteTimeUtc, Length)` stamp is unchanged since it was last synced is **not re-read** by the reconcile or the tab reload (stat-only), and forces no `WithDocumentText`/buffer reload.
- [ ] Content remains the fork arbiter: an external write that leaves a watched file's content identical (same bytes) forks nothing and reloads no tab, even though the watcher fired.
- [ ] An external edit to the file shown in the **active** tab is reflected in that tab without requiring a window focus change (live push).
- [ ] An external edit to a file open only in a **background** tab is reflected when that tab is next activated, and not before; a background tab whose file did not change is never re-read on activation.
- [ ] A semantic query (F12/Shift+F12/Problems/rename) run after external edits resolves against all of them — the snapshot drains its full pending-set before the query, not just the file under the caret.
- [ ] A structural change (external add/remove/rename of a `.cs` file, or a `.csproj` edit) triggers a real workspace reload and a solution-tree refresh via the watcher — no manual "Reload solution" needed.
- [ ] On watcher overflow/error, the system falls back to a full stamp-gated reconcile and remains correct (no missed changes after the fallback runs); the watcher resumes afterward.
- [ ] The rename apply (`RenameService.ApplyToDisk`) does not cause a redundant watcher-driven reconcile to fight `RenameSymbolAsync`'s own post-apply refresh (self-writes are muted); a subsequent *external* edit is still caught.
- [ ] `SearchService`/`FileGrepper` are unchanged; global search still returns line/column-precise, regex-capable, `IdeDirectories`-pruned results.
- [ ] "Reload solution" and pre-operation reconcile still function when the watcher is unavailable (e.g. it never started): the fallback poll keeps the view correct.

## Implementation

### 1. Add per-path stamps as a read pre-filter
Introduce a `(LastWriteTimeUtc, Length)` stamp per tracked file path, captured when its content is last synced (at `LoadSolutionAsync` and whenever the overlay re-reads a file). In `OverlayDiskTextAsync`, before `File.ReadAllTextAsync`, `stat` the file (`FileInfo`/`File.GetLastWriteTimeUtc` + length) and skip untouched files without reading; on a stamp change, read + `ContentEquals` as today (content still decides the `WithDocumentText`), then update the stamp. Apply the same gate to `MainWindowViewModel.ReloadDriftedTabsAsync` so an unchanged open tab is a stat, not a read. Keep `Length` in the stamp per Constraints. This step alone is the cold-start/fallback path's cost reduction and is independent of the watcher.

### 2. Turn the reconcile into a pending-set drain with a full-rescan fallback
Give `WorkspaceService` a pending-overlay set (paths known-changed since the last reconcile) plus a `needsFullRescan`/structural flag. Rework `ReconcileWithDiskAsync` (still under `_lock`): if `needsFullRescan` is set (cold start, overflow, or a structural signal), run the existing whole-solution pass (manifest fingerprint → reload-or-full-`OverlayDiskTextAsync`), now stamp-gated per step 1, and clear the flags; otherwise overlay only the pending paths (stamp-gate each, `WithDocumentText` the genuinely-changed ones) and clear the set. The first-ever reconcile after `EnsureLoadedAsync` is a full rescan. Expose small methods for the watcher to push into these (e.g. `MarkPathsChanged(paths)`, `MarkStructural()`, `RequestFullRescan()`), all lock-guarded.

### 3. Add the FileSystemWatcher service
Per Open Decision 1, add a dedicated watcher (default) that, on solution open, watches the solution root recursively with `NotifyFilters` for name/size/lastwrite/dir changes, filtering out `IdeDirectories.Pruned` subtrees. Debounce events (Open Decision 2) into a coalesced signal carrying: the set of changed existing-file paths, whether any change was structural (create/delete/rename, or a `.csproj`), and whether an overflow/`Error` occurred. Raise it as an event (or callback) off the UI thread. Manage lifecycle — start on `OpenSolutionAsync`, restart on reload/other-solution, dispose on close — and handle the `Error` event by signalling overflow.

### 4. Route the watcher signal into the two dirty tracks + visible surfaces
In `MainWindowViewModel`, subscribe to the watcher signal and, on the UI thread where needed:
- **Snapshot**: call the workspace's `MarkPathsChanged`/`MarkStructural`/`RequestFullRescan` (step 2) so the *next* semantic query/reconcile drains them — do **not** eagerly reconcile the snapshot on every watcher tick (it stays lazy, query-driven).
- **Per-tab stale**: for each changed path with an open tab, set that tab's stale flag (Open Decision 4). If the changed path is the **active** tab, reload it now (live push, Open Decision 5) via `ReloadFromDisk`.
- **Tree**: for a structural change, refresh the affected tree node(s) — reuse the tree-node replace/refresh path the rename feature added (`ReplaceTreeFileNode`-style) or a lighter targeted refresh; a broad change can fall back to the existing tree reload.
- **Overflow**: call `RequestFullRescan()` and mark all open tabs stale (they'll revalidate lazily/live).

### 5. Lazy background-tab revalidation on activation
Add an `ActiveTab`-changed hook (the `[ObservableProperty] _activeTab` partial) that, when a tab becomes active and its stale flag is set, reloads it from disk (`ReloadFromDisk`, stamp-gated) and clears the flag. This is the lazy half: a background tab is read only when shown, and only if flagged. `RefreshFromDiskAsync` (focus) can be slimmed to a safety-net (reconcile drain + reload the active tab), since the watcher now handles the steady state — keep it as a fallback for events missed while unfocused.

### 6. Mute the watcher around the rename apply
Wrap `RenameService.ApplyToDisk` (called from `RenameSymbolAsync`) in a self-write suppression (Open Decision 3) so the writes/move it performs don't trigger a watcher reconcile that races `RenameSymbolAsync`'s own post-apply `ReconcileWithDiskAsync`/`ReloadDriftedTabsAsync`. Ensure suppression is scoped to just those paths/that window and lifted afterward, so a later *external* edit to the same files is still caught.

### 7. Keep the fallback poll wired
`EnsureWorkspaceReadyAsync` (pre-op) and `ReloadWorkspaceAsync` ("Reload solution") continue to call the reconcile, which now drains the pending-set (or full-rescans). The pre-op reconcile is the guarantee that a query is correct even if the watcher missed something — do not remove it. Confirm the system is correct with the watcher entirely absent (never started): reconcile then behaves as a full stamp-gated rescan each pre-op, i.e. the pre-watcher behavior plus the step-1 speedup.

### 8. Tests
Extend the existing MSBuild-fixture harness (`WorkspaceServiceTests` shape — real temp-dir `Lib.slnx`/`Lib.csproj`, `MSBuildLocator`, edit files on disk to simulate external tools):
- Stamp gate: reconciling when a file's stamp is unchanged performs no read/fork; changing content (bumping the stamp) does.
- Pending-set overlay: marking one path changed and reconciling overlays only that document (assert via a seam, or by go-to-def resolving the changed file's new content while an *unmarked but also-edited* file is not yet reflected until a full rescan — encoding the "drain only pending" behavior).
- Structural signal → full reload picks up an added/removed file (mirror the read-only ticket's `Reconcile_PicksUpAnExternallyAddedFile`).
- Overflow → full rescan restores correctness after a `MarkPathsChanged` was skipped.
- Content-still-arbiter: a stamp change whose content is identical forks nothing.
- (If feasible headless) a `SolutionWatcher` unit test that a create/change/delete under the root raises the right signal and prunes `IdeDirectories`; the actual `FileSystemWatcher`-driven tab-reload/live-push is a manual GUI item.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with only the three pre-existing warnings; `dotnet test MiniIde.slnx` passes including the new cases.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`. Externally edit the file shown in the **active** tab (agent or another editor) → the tab updates **without** clicking away and back (live push).
- [ ] Open a second file, switch away from it, edit it externally, switch back → it shows the new content on activation; switch to a third unchanged tab → no flicker/reload.
- [ ] Externally edit several files, then F12/Shift+F12 into one of them → navigation/references resolve against the latest content of all of them.
- [ ] Externally add a new `.cs` file and remove another → the tree updates and the new file's symbols resolve / the removed file's stop, with no manual reload.
- [ ] Burst test: run the agent (or a script) rewriting many files at once → the app stays responsive, coalesces into few reconciles, and ends in a correct state (spot-check a few files); confirm no exception in the status bar/debug output (overflow path, if hit, recovers).
- [ ] Rename a symbol (safe-rename) touching several files → the view updates once via the rename's own refresh, with no visible double-reconcile from the watcher; then externally edit one of the renamed files → it's still picked up (mute was scoped/lifted).
- [ ] Global search (Ctrl+Shift+F) with a regex still returns line/column-accurate, pruned results — unchanged.
- [ ] Sanity: with the watcher disabled/never-started (temporarily), focus + F12 still reconcile correctly via the fallback poll.
