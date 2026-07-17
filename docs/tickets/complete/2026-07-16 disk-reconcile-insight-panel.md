# Disk Reconcile Insight Panel

## Context
**Current behavior**: The OS-driven disk reconcile (`SolutionWatcher` → `DiskChangeSignal` → the workspace pending-set + per-tab stale flags) runs entirely invisibly. Nothing surfaces whether the watch is even live — if `SolutionWatcher.Start` returns false, `WorkspaceService.DiskIsWatched` stays false and the app silently degrades to full-rescan polling forever, with no indication. Nothing surfaces whether the stamp gate is paying off, how often the pending-set drain is used versus the full-rescan fallback, whether the watcher's buffer ever overflows, or — when an external change collapses the solution tree — *which path* made the signal structural.

**New behavior**: A fourth bottom tab, **Disk**, showing that system live: whether the watch is running and over which root, a running log of the most recent debounced signals (with the path and reason that made each one structural), the reconcile funnel (documents stat'd → read → forked) and mode counts (drains vs. full rescans vs. structural reloads), and the current live dirty state (the pending-overlay set, the full-rescan flag, and which open tabs are stale). The panel is read-only observation — it changes no reconcile behavior — and repaints on a timer only while its tab is selected. The signal log, being event-driven, keeps accumulating regardless, so tabbing to it after a burst still shows what happened.

This exists because three decisions in the reconcile were made with no data (the 200 ms debounce window, the 64 KB `InternalBufferSize`, and how aggressively a change is classified structural) and two of its failure modes are silent (a dead watcher; structural churn collapsing the tree with no named cause).

## Prerequisites
- `docs/tickets/complete/2026-07-16 os-driven-disk-reconcile.md` — built the entire system this observes (`SolutionWatcher`, `DiskChangeSignal`, the stamp gate, the pending-set drain, the two dirty tracks, `DiskIsWatched`). Read its **Learnings** before coding: the stamp invariant, the mute↔pending-set coupling, and the tree-collapse limitation are all things this panel is meant to make visible.

## Scope
### In scope
- Carrying a **structural reason** (and a raise timestamp) on `DiskChangeSignal`, so a signal is self-describing about *why* it forces a rebuild.
- Read-only counters on `SolutionWatcher` (events seen / pruned / muted, signals raised, overflows) and `WorkspaceService` (the stat→read→fork funnel, drain vs. full-rescan vs. structural-reload counts), plus a live-state read (pending paths, full-rescan flag, watched root).
- A new `DiskInsightViewModel` owning a bounded signal log, a snapshot pull, and the timer lifecycle.
- A fourth `<TabItem Header="Disk">` in `BottomTabs`.
- Unit tests for the new counters — including the two invariants the previous ticket could only assert behaviorally (see Implementation step 8).

### Out of scope
- **Timing data** — reconcile/rescan durations, percentiles, debounce latency. The natural thing to reach for, deliberately deferred: the funnel and mode counts already imply where the cost is, and timings would mostly confirm it. Add them only once a counter points somewhere specific.
- **Top-N churning paths / any aggregation over history.** The signal log is a bounded ring buffer, not a queryable store.
- **Persisting counters across restarts.** Everything is since-app-start, in memory.
- **Any behavior change to the watcher or the reconcile.** This ticket observes; it must not tune. The debounce window, buffer size, and structural classification stay exactly as they are — changing them is what the *data* is for, in a later pass.
- **Fixing the tree collapse on a structural change** (`os-driven-disk-reconcile` Learnings, "Workarounds / limitations"). This panel is how we'd diagnose it; the fix (binding `TreeNode.IsExpanded`, or targeted node insert/remove) is its own ticket. Do not roll it in.
- **A general metrics/telemetry framework.** Plain counters and one snapshot per service; nothing reusable, nothing abstract.
- **A view-layer (Avalonia.Headless) test project.** Still none — the panel's rendering and timer lifecycle are manual items.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/disk-watching.md` — the feed's design: best-effort semantics, the two-dirty-tracks table, pruning, the mute's expiry-not-consumption rule. This is the system being measured; the panel's fields should map onto it.
  - `docs/roslyn.md §Disk reconcile` — the two reconcile modes, the two gates (stamp decides *read*, content decides *fork*), and the stamp invariant.
  - `docs/CLAUDE.md` — one topic per file, fragments over prose (for any doc edits).
- **Related tickets** (context and gotchas, not a structure to copy):
  - `docs/tickets/complete/2026-07-10 problems-panel.md` — the panel-add pattern is spelled out in its Relevant Docs ("there is no panel abstraction: a VM property + a hand-written `TabItem`"). Its Learnings carry the Avalonia gotchas that bit last time: tunnel `PointerPressed` over `DoubleTapped`, attach-once wiring for controls inside an unrealized `TabItem`, and `NotifyCanRefreshChanged` existing because `SolutionService.SolutionPath` isn't observable.
  - `docs/tickets/complete/2026-07-11 collapsible-bottom-panel.md` — `ShowBottomTab`/`ExpandBottomPanel` and why the toggle sits outside `BottomTabs`.
- **Code anchors** (symbols; verify against source):
  - `Services/SolutionWatcher.cs` — `Record` (the prune → `_gate` → mute → accumulate path, and where `_structural` is set), `Fire` (where the burst becomes a signal and the accumulators reset), `Schedule`, `IsPruned`, `IsMuted`, `IsProjectFile`, `Start`/`Stop`, `_gate`.
  - `Services/WorkspaceService.cs` — `OverlayDocumentAsync` (the stat/read/fork funnel, all three gates in one method), `ReconcileWithDiskAsync` (the drain-vs-full branch and the `LoadSolutionAsync` structural-reload call site), `DrainDirty`, `MarkPathsChanged`, `RequestFullRescan`, `DiskIsWatched`, `_stamps`, `_pending`, `_needsFullRescan`, `_dirtyGate`, `_lock`.
  - `Models/DiskChangeSignal.cs` — the record to extend.
  - `ViewModels/ProblemsViewModel.cs` — the panel-VM exemplar: `partial ViewModelBase`, `[ObservableProperty]` state, injected callbacks rather than a reference back to `MainWindowViewModel`, `Dispatcher.UIThread.Post` for off-thread mutation of bound state.
  - `ViewModels/MainWindowViewModel.cs` — `OnDiskChanged` (the existing `Watcher.Changed` subscriber), the ctor's service construction + `Problems`/`NuGetVm` wiring, `Tabs`, `Watcher`, `Workspace`.
  - `ViewModels/EditorTabViewModel.cs` — `IsStale` (the per-tab dirty track the panel surfaces).
  - `Views/MainWindow.axaml` — `BottomTabs`, and `<TabItem Header="Problems" x:Name="ProblemsTab">` as the shape to mirror (`DataContext="{Binding …}"` on the tab's root panel).
  - `Views/MainWindow.axaml.cs` — `ShowBottomTab`, `OnBottomTabsPointerPressed` (a **tunnel** handler for expand-on-click — not a selection hook; don't conflate the two), `OnProblemsTreeAttached` (the attach-once pattern).

## Constraints & Gotchas
- **Observation must not perturb what it measures.** Everything added here is read-only and allocation-light. No new locks on the event path, no new awaits in the reconcile, no behavior branches on whether the panel is open.
- **The prune check runs deliberately *outside* `SolutionWatcher._gate`** — that is what keeps a build's `obj/` churn off the lock entirely. Counting pruned events must not drag that work inside the lock, or the counter defeats the optimization it is measuring. Use `Interlocked` for the counters incremented before the lock (events seen, events pruned); the ones already inside `_gate` (muted, signals, overflows) can be plain increments read under the same lock.
- **Counters are written off the UI thread and read from it.** Watcher counters increment on threadpool threads; `WorkspaceService`'s funnel counters increment under `_lock` inside an async reconcile. The panel's timer reads them on the UI thread. Every counter needs a safe publish (`Interlocked`/`Volatile`, or a read under the owning lock) — a plain `int++`/read pair across threads is a torn-read bug, not a rounding error.
- **Don't let the services grow reporting logic.** Services here are single-concern; a debug panel must not become a dependency of `WorkspaceService`. Keep them to increments plus one snapshot read; put all aggregation, ratio maths, and formatting in the VM. Nothing in `Services/` should know a panel exists.
- **The signal log is event-driven; only the counter pull is timer-gated.** Tying the log to the timer would mean tabbing away during an agent burst and losing exactly the signals you wanted to see. The VM's `Watcher.Changed` subscription must be live for the app's lifetime, independent of tab selection.
- **The log must be bounded.** An agent rewriting a repo would grow an unbounded list forever. Ring buffer; drop oldest.
- **`Watcher.Changed` fires on a threadpool thread** — appending to a bound `ObservableCollection` must be marshalled (`Dispatcher.UIThread.Post`), mirroring `MainWindowViewModel.OnDiskChanged`.
- **`WorkspaceService.DiskIsWatched` and `SolutionService.SolutionPath` are not observable.** Don't bind them directly and expect updates — the panel learns them through the snapshot pull. This is the same trap that made `Problems.NotifyCanRefreshChanged()` necessary.
- **`DispatcherTimer` and `Interlocked` are both first uses in this codebase** (grep-confirmed: neither appears in `src/`). There's no local idiom to mirror — establish a clean one.
- **A second `MainWindowViewModel` subscriber to `Watcher.Changed`** joins the existing `OnDiskChanged`. Both must stay independent; the panel must never mutate the dirty tracks (it reads them).
- **Expected warnings**: the pre-existing set (IL3000 in `SyntaxHighlightService`; AVLN5001/`PlaceholderText`). Introduce no new ones.
- **Build lock**: a running MiniIde locks its output DLL — build to a temp `-o` dir.

## Open Decisions
1. **Snapshot shape** — one `record` per service assembled under its lock (e.g. `SolutionWatcher.Snapshot()`), vs. a property per counter. Default: a snapshot record — a single consistent read, so the panel can't show a torn view where the funnel's numbers disagree across a repaint. Implementer's call.
2. **Structural reason shape** — a small record carrying the path plus a kind (`Created`/`Deleted`/`Renamed`/`ProjectChanged`), vs. a preformatted string. Default: the record; the panel formats. A string in a Model is the thing that rots.
3. **Ring buffer size** — Default: 50 signals.
4. **Timer interval** — Default: 1 s. Fast enough to feel live, slow enough to be free.
5. **Counter reset** — cumulative since app start plus a Reset button, vs. rolling window. Default: cumulative + Reset (a Reset makes "watch this one burst" possible, which is most of the diagnostic value).
6. **Whether the timer also pauses while the bottom panel is collapsed** — the Disk tab can be the selected tab of a collapsed panel. Default: tie to tab selection only; a 1 s pull is cheap and collapse-tracking is extra state. Implementer's call.
7. **Tab header text** — Default: `Disk`. (`Watch`/`Diagnostics` also fine; `Disk` matches the domain language of `disk-watching.md`.)
8. **Where the signal log lives** — the VM accumulating from `Changed` (default, and why step 1 puts the reason on the signal), vs. a ring buffer inside `SolutionWatcher`. Default keeps the service free of history.

## Acceptance Criteria
- [ ] `BottomTabs` contains a fourth `TabItem` (header per Open Decision 7) bound to a `DiskInsightViewModel`-typed property on `MainWindowViewModel`, alongside Find / NuGet / Problems.
- [ ] `DiskChangeSignal` carries, in addition to its current members, the time the signal was raised and — when `Structural` is true — a reason identifying the path and what about it was structural (created / deleted / renamed / project-file change). When `Structural` is false the reason is absent.
- [ ] `SolutionWatcher` exposes read-only counts of: raw events seen, events dropped by pruning, events dropped by muting, signals raised, and overflows; plus whether a watch is currently live and the root it covers.
- [ ] `WorkspaceService` exposes read-only counts of: documents stat'd, documents read, documents forked, pending-set drains, full rescans, and structural reloads; plus a live read of the current pending-overlay paths and the full-rescan flag.
- [ ] The funnel counters are internally consistent for any sequence of reconciles: documents read ≤ documents stat'd, and documents forked ≤ documents read.
- [ ] Reconciling when nothing has changed on disk increments documents stat'd but leaves documents read unchanged — the stamp gate, asserted directly rather than inferred.
- [ ] Rewriting a file with byte-identical content and reconciling increments documents read but leaves documents forked unchanged — content is the fork arbiter, asserted directly.
- [ ] A write under a pruned directory (e.g. `obj/`) increments the pruned count and raises no signal.
- [ ] A muted self-write increments the muted count and raises no signal.
- [ ] The panel shows whether the watch is live and over which root; with no solution open it reads as not watching.
- [ ] The panel lists the most recent signals (bounded per Open Decision 3, newest first), each showing its timestamp, path count, and structural/overflow state; a structural entry names the path and reason that made it structural.
- [ ] The panel shows the current pending-overlay paths, the full-rescan flag, and each open editor tab's stale flag.
- [ ] The signal log records signals that arrive while the Disk tab is not selected (it is event-driven, not timer-driven).
- [ ] The timer pull runs only while the Disk tab is selected.
- [ ] No reconcile or watcher behavior changes: the existing `WorkspaceServiceTests` and `SolutionWatcherTests` pass unmodified except where a new counter assertion is added.

## Implementation

### 1. Carry the structural reason and a timestamp on the signal
The panel's single most valuable column is *what made this structural* — today a signal says `Structural: true` with no provenance, so "the tree just collapsed, why?" is unanswerable. Extend `DiskChangeSignal` (per Open Decision 2) with a raise timestamp and an optional structural reason. In `SolutionWatcher.Record`, where `_structural |= structural || IsProjectFile(fullPath)` sets the flag, also capture the reason from the **first** event in the burst that set it (subsequent ones don't overwrite — the first cause is the interesting one), and reset it in `Fire` alongside `_structural`. Stamp the time in `Fire`, not `Record` — the signal's identity is the burst, which ends there. `MainWindowViewModel.OnDiskChanged` needs no change; it reads only `.Structural`/`.Overflow`.

### 2. Add counters to `SolutionWatcher`
So the panel can distinguish "the feed is quiet" from "the feed is being filtered" — a silent watcher and a watcher whose every event is pruned look identical today. Count raw events seen and events dropped by pruning in `Record` **before** it takes `_gate` (see Constraints — the prune check is outside the lock on purpose; use `Interlocked` rather than moving it in), and count muted drops, signals raised, and overflows where those already happen under `_gate`. Expose the live/root state alongside them (per Open Decision 1). `Start`/`Stop` decide "live"; the root is already tracked in `_root`.

### 3. Add the funnel and mode counters to `WorkspaceService`
The funnel is the whole thesis of the reconcile made observable: `OverlayDocumentAsync` already has exactly three gates in a row — count a stat once past the `FilePath` guard, a read where it calls `File.ReadAllTextAsync`, and a fork at the `WithDocumentText` call. The read/stat ratio is the stamp gate's payoff; the fork/read ratio is the content gate's. In `ReconcileWithDiskAsync`, count the two modes at the `DrainDirty` branch (drain vs. full rescan) and count a structural reload at the `LoadSolutionAsync` call site inside the fallback — that one is the expensive event (an `MSBuildWorkspace` teardown + rebuild), so it is worth its own number rather than being inferred. These all run under `_lock`, but the panel reads them from the UI thread — publish them safely per Constraints.

### 4. Expose the live dirty state
Counters say what happened; the live state says what the system currently *owes*, which is what makes the two-track design legible ("this tab is stale, the snapshot has 3 paths pending, no rescan queued"). Add a read on `WorkspaceService` returning the current pending paths and the full-rescan flag, taken under `_dirtyGate` (a copy — never hand out the live set, or the panel could mutate a dirty track). `DiskIsWatched` is already public. The per-tab half is the VM's (step 5) — `EditorTabViewModel.IsStale` is already public.

### 5. Create `DiskInsightViewModel`
Model the shape on `ProblemsViewModel`: a `partial ViewModelBase` with `[ObservableProperty]` state and dependencies injected in the ctor rather than a reference back to `MainWindowViewModel`. It takes `WorkspaceService` and `SolutionWatcher`, plus a way to enumerate the open editor tabs — mirror how `ProblemsViewModel` takes injected callbacks (a `Func<>` over the tabs keeps the VM from knowing about `Tabs` mechanics). It owns:
- A subscription to `Watcher.Changed` that prepends a display row to a bounded ring buffer (Open Decision 3), marshalled onto the UI thread — it fires on a threadpool thread. **Subscribed for the app's lifetime, not tied to tab selection** (see Constraints).
- A pull method reading both services' snapshots plus the stale-tab list into observable properties, and computing the derived ratios for display.
- Start/stop for the timer, plus the Reset (Open Decision 5).

### 6. Add the timer lifecycle
A 1 s repaint for a panel nobody is looking at is pure background cost. Hook `BottomTabs`' selection in `MainWindow.axaml.cs` and start/stop the VM's pull accordingly — note the existing `OnBottomTabsPointerPressed` is a *tunnel* handler for expand-on-click and is **not** a selection hook; this needs its own. `DispatcherTimer` is a first use here (Constraints); pull once immediately on select so the panel isn't blank for a second.

### 7. Own the VM and add the tab UI
Add a `DiskInsightViewModel`-typed property to `MainWindowViewModel`, constructed in the ctor after `Problems` (it must exist from startup so its `Changed` subscription is live before any solution opens). Then add the `TabItem` to `BottomTabs` in `MainWindow.axaml` after Problems, with `DataContext` bound to it, mirroring the Problems tab's shape. Lay out four regions, text-dense and no charts: a status line (watching / not, and the root); the feed counters; the reconcile funnel + mode counts; the live state (pending paths, rescan flag, stale tabs); and the signal log as a newest-first list. The log is the part worth the most space.

### 8. Tests
Extend the existing harnesses rather than adding a project.
- `SolutionWatcherTests` (real temp dir + real `FileSystemWatcher`): a pruned write increments the pruned count and raises no signal; a muted write increments the muted count and raises no signal; a structural signal carries a reason naming the created path. Its existing cases already create/mute/prune — extend them or add siblings.
- `WorkspaceServiceTests` (MSBuild fixture): the funnel counters make two invariants **directly** assertable that the previous ticket could only observe behaviorally — reconciling with nothing changed leaves reads at zero while stats climb (the stamp gate), and a byte-identical rewrite increments reads but not forks (content as arbiter). The existing `Reconcile_SkipsAFileWhoseStampIsUnchanged` proves the first via a same-length/restored-mtime trick and `Reconcile_LeavesContentUnforkedWhenOnlyTheStampMoved` proves the second only by "the answer didn't change" — keep both (they assert the user-visible outcome) and add counter assertions for the mechanism. Also assert the funnel's consistency invariant (read ≤ stat, fork ≤ read) after a mixed sequence.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with only the pre-existing warnings (XAML compiles, so the new tab and its bindings resolve); `dotnet test MiniIde.slnx` passes, including the new counter cases and the 36 existing tests unchanged in behavior.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`, select the Disk tab → it reports the watch as live over the solution root, and the counters are populated rather than blank.
- [ ] **The stamp gate, made visible**: with nothing changed, alt-tab away and back a few times → documents stat'd climbs, documents read stays flat. This is the ticket's whole thesis on screen.
- [ ] **The content gate, made visible**: re-save a file externally without changing its content → documents read climbs by one, documents forked does not.
- [ ] Externally edit one file → a signal appears within ~1 s showing 1 path, not structural; the funnel's read/fork each climb by one.
- [ ] Externally add a file → the log shows a structural entry **naming that path** as the reason, and the structural-reload count climbs. (The tree also collapses — that is the known limitation this panel is meant to diagnose, not fix.)
- [ ] **Burst**: run an agent or a script rewriting many files → the log shows few signals carrying many paths each (coalescing working), not one signal per file. Note whether overflow ever trips — that number is the answer to "does the fallback poll earn its keep?"
- [ ] **Pruning**: run a `dotnet build` that writes into the tree's `obj/` → the pruned count climbs sharply while signals raised stays flat.
- [ ] Rename a symbol (safe-rename) touching several files → the muted count climbs and no signal is raised for the rename's own writes; then externally edit one of those files → a signal appears (the mute expired, scoped correctly).
- [ ] **Two tracks**: open two files, switch away from one, edit it externally → the live state shows that tab stale while the active one isn't; activate it → the stale flag clears.
- [ ] **Log is event-driven**: select the Find tab, externally edit a few files, then switch back to Disk → the signals from while you were away are in the log.
- [ ] **Timer is scoped**: with the Disk tab deselected, counters visibly stop updating; reselecting repaints immediately (no blank second).
- [ ] Regression: Find, NuGet, Problems, F5/run, and global search behave exactly as before; the panel changes no reconcile behavior (open the Disk tab and confirm live push / lazy activation still work as they did).

## Learnings

### Verification status
`dotnet build` (0 errors, **0** warnings — the pre-existing IL3000/AVLN5001 pair the ticket predicted no longer
appears; both are suppressed) and `dotnet test MiniIde.slnx` (41 passing, up from the 36 baseline: 2 new
`SolutionWatcherTests` + 3 new `WorkspaceServiceTests`, plus counter assertions grafted onto 4 existing cases)
both ran here. The app was launched via `scripts/run.ps1` and stays responsive.

**The GUI observations were not verified.** Everything in the Test Plan from "select the Disk tab" onward — the
panel's layout, the stamp/content gates visibly climbing, the burst/pruning/mute counters on screen, the
event-driven log surviving a tab-away, and the timer visibly stopping on deselect — needs eyes on the window.
Note the panel's content lives inside an **unrealized `TabItem` until first selected**, so a binding typo there
would not surface at startup: a clean launch proves the XAML compiled, not that the panel renders.
`docs/tickets/headless-view-tests.md` is the standing answer to this gap.

### Architectural decisions
- **Open Decisions 1, 2, 3, 4, 6, 7, 8 — all taken at their defaults**: a snapshot record per service; a
  `StructuralReason(Path, Kind)` record with the panel formatting; a 50-entry log; a 1 s pull; tied to tab
  selection only (not collapse); header `Disk`; the log living in the VM.
- **Open Decision 5 (counter reset) — cumulative + Reset, but the Reset is a *VM-side baseline*, not a
  `ResetCounters()` on the services.** The Constraints say nothing in `Services/` should know a panel exists; a
  reset method would have been the panel reaching in and *writing*. Subtracting a baseline the VM captured is
  behaviorally identical, keeps the services read-only from the panel's side, and cannot race a running
  reconcile. The services stay: increments + one snapshot read.
- **`WorkspaceService`'s counters get their own plain `_countersGate`, not `_lock`.** The obvious reading of
  "assemble the snapshot under its lock" is a trap here: `_lock` is a `SemaphoreSlim` an async reconcile holds
  across awaits, so a UI-thread reader taking it would block the window for the length of a reconcile — the
  observer perturbing exactly what it measures, which the Constraints forbid. A separate gate gives a
  consistent read that can never block. `SolutionWatcher` had no such problem: its `_gate` is a plain lock held
  briefly, so its snapshot rides it as the ticket suggested.
- **The funnel's consistency invariant is structural, not defensive.** Increments happen in funnel order
  (stat → read → fork) at the three gates, so `Read <= Statted` and `Forked <= Read` hold for *any* snapshot,
  including one taken between two increments. Nothing needs clamping.

### Problems encountered / gotchas
- **`SelectionChanged` bubbles, and `BottomTabs` contains four other selectors.** A naive
  `BottomTabs.SelectionChanged += ...` also fires for the Find results `ListBox` and the three NuGet lists, so
  picking a NuGet package would have stopped the Disk panel's timer. The handler guards on
  `ReferenceEquals(e.Source, BottomTabs)`. This is a sibling of the gotcha the problems-panel ticket flagged —
  `OnBottomTabsPointerPressed` is a *tunnel* handler for expand-on-click and is **not** a selection hook; the
  two must not be conflated, and now there is one of each on the same control.
- **Avalonia's `DispatcherTimer(TimeSpan, DispatcherPriority, EventHandler)` ctor starts the timer.** Using it
  would have polled from construction — for a panel never shown — quietly defeating the point of gating the
  pull on selection. The default ctor + `Interval` + `Tick` does not start. (`DispatcherTimer` and
  `Interlocked` were both first uses in this codebase; there was no local idiom to mirror.)
- **Counting inside a lock can silently swallow the work that follows it.** The first cut of
  `SolutionWatcher.Record` restructured the `_structural |= ...` line into an `if (structural is null) return;`
  *inside* `_gate` — which returned before the `Schedule()` call that follows the lock, killing the debounce for
  every content-only change. Caught before building. Generalizes: when adding an early-exit to a method whose
  *tail* does the real work, check the tail.
- **A dot indicator does not need a converter.** `Fill="{Binding IsWatching, Converter=...}"` wanted a converter
  that doesn't exist; two `Ellipse`es with `IsVisible="{Binding IsWatching}"` / `IsVisible="{Binding
  !IsWatching}"` (Avalonia's `!` binding negation) does it with no new type.

### Interesting tidbits
- **The panel's most valuable field is the one that costs the least.** `StructuralReason` is a path plus a
  four-case enum captured at the *first* event that set `_structural`, and it turns "the tree just collapsed,
  why?" from unanswerable into a log line. Everything else on the panel is arithmetic over counters.
- **`WatchStats.IsWatching` closes a real blind spot**: a quiet watcher and a watcher whose every event is
  pruned were indistinguishable from outside, and a watcher that never started (`Start` → false →
  `DiskIsWatched` false → full-rescan polling forever) had no surface at all. `Pull` spells that third case out
  — "Not watching — the reconcile is full-rescan polling" — rather than showing a dark dot and leaving the
  inference to the reader.
- **The signal log reads the root via `_watcher.Snapshot().Root` per signal, not from a field cached by
  `Pull`.** Otherwise a burst arriving before the panel's first-ever pull would log full paths — exactly the
  "tab over after the burst" case the log exists for. Once per debounced burst is nothing; the
  allocation-light rule is about `Record` (per OS event), which is untouched.

### Workarounds / limitations
- **The tree still collapses on a structural change.** Untouched, per Out of scope — this panel is how you now
  *diagnose* it (the log names the path and reason), not the fix. `os-driven-disk-reconcile`'s Learnings carry
  the two candidate fixes (bind `TreeNode.IsExpanded`, or targeted node insert/remove).
- **Everything is since-app-start and in memory**, per Out of scope. Reset rebases; nothing persists.
- **No timing data**, per Out of scope. The funnel and mode counts imply where the cost is; add timings only
  once a counter points somewhere specific.

### Related areas affected
- `DiskChangeSignal` gained two members. `MainWindowViewModel.OnDiskChanged` needed no change (it reads only
  `.Structural`/`.Overflow`), exactly as the ticket predicted. `SolutionWatcher.Record`'s signature changed from
  `bool structural` to `StructuralKind? kind` — null now means "a plain content change", which reads better at
  the four call sites than the boolean did.
- `MainWindowViewModel` now has **two** independent `Watcher.Changed` subscribers: `OnDiskChanged` (owns the
  dirty tracks) and the panel's log (reads only, never mutates them). They share nothing.
- The panel is the **first non-tab consumer of `EditorTabViewModel.IsStale`** — via an injected
  `Func<IEnumerable<EditorTabViewModel>>`, mirroring `ProblemsViewModel`'s injected callbacks, so it reads the
  per-tab dirty track without knowing how tabs are kept.

### Rejected alternatives
- **`ResetCounters()` on each service** — rejected; see Open Decision 5 above. The panel must not be able to
  write to a service.
- **Assembling `WorkspaceService`'s snapshot under `_lock`** — rejected; it would block the UI thread for the
  length of a reconcile. See the `_countersGate` decision.
- **`Interlocked` for the whole `WorkspaceService` funnel** — rejected: `Interlocked.Read` per field gives a
  *torn* snapshot (stat from one instant, fork from another), which is what Open Decision 1 exists to avoid. It
  stays the right tool in `SolutionWatcher.Record`, where the counters sit outside the lock on purpose.
- **A ring buffer inside `SolutionWatcher`** (Open Decision 8's alternative) — rejected per the default: it
  would give the service a history and a display concern it has no other use for.
- **Binding `WorkspaceService.DiskIsWatched` / the watch root directly** — rejected, and worth restating: they
  are not observable, so the binding would show the startup value forever. Same trap that made
  `Problems.NotifyCanRefreshChanged()` necessary. The panel learns both through the snapshot pull.
