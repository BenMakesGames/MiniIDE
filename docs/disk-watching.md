# Disk watching (the OS change feed)

`SolutionWatcher` = one `FileSystemWatcher` over the solution root → debounced `DiskChangeSignal(Paths,
Structural, Overflow)`. Drives reconciliation instead of polling. See `roslyn.md §Disk reconcile` for the
consumer.

## FileSystemWatcher

- **Best-effort. Never delete the poll — only make it rare.** Internal buffer overruns under a burst (agent
  rewriting hundreds of files, `git checkout`) → `Error`; events also drop under load. Anything built on it
  must stay correct if a signal never arrives.
- `InternalBufferSize` defaults to 8 KB. Raise it (64 KB) — recovery from overflow costs far more than buffer.
- `NotifyFilter`: `FileName | DirectoryName` (create/delete/rename) + `LastWrite | Size` (content). Size
  alongside LastWrite so a same-mtime resize still reports.
- **No subtree exclusion exists.** `Filters` only includes name patterns. Prune `IdeDirectories` yourself by
  walking the relative path's segments, or a build writing `obj/` is a reconcile storm.
- Events arrive on **threadpool** threads. Marshal UI-thread mutations; don't throw out of a handler.
- A rename raises one `Renamed` carrying **both** `OldFullPath` and `FullPath` — record both.
- `Timer.Change` after `Dispose` throws `ObjectDisposedException` — a debounce timer must swallow that race.

## Signal shape

- Carries a **raise timestamp** (stamped in `Fire`, not `Record` — the signal's identity is the burst, which
  ends there) and, when structural, a **`StructuralReason(Path, Kind)`**: the *first* event in the burst that
  set the flag. First cause, not last — that's the one that explains the rebuild.
- Reason is a record, not a preformatted string; the panel formats. A display string in a Model rots.
- **Structural** = created / deleted / renamed, **or** a `.csproj` changed. `WithDocumentText` can't add or
  remove documents, so the consumer rebuilds instead of overlaying — and refreshes the tree, which learns of a
  new file no other way.
- **Overflow** ⇒ paths are meaningless; rescan everything. Also treat as structural (it may have hidden one).
- **Debounce trailing-edge** (~200 ms): each event pushes the deadline out, so a burst = one signal carrying
  many paths.

## Two dirty tracks, never one

One signal feeds **two independent** consumers:

| Track | Lives on | Consumed by |
|---|---|---|
| pending-overlay set | `WorkspaceService` | the next reconcile (semantic query) |
| `IsStale` | `EditorTabViewModel` | that tab becoming active |

A single shared set is a bug: whichever consumer drains first erases the mark the other hasn't acted on, so an
unopened tab silently stays stale.

Laziness split: **visible surfaces react on the event** (active tab live-push, tree on structural);
**everything else revalidates when asked** (background tab on activation, snapshot on next query). Don't
reconcile the snapshot on every tick — it pays the expensive part for edits no query asks about.

## Muting self-writes

Mute *only* first-party writes that refresh the view themselves (today: `RenameService.ApplyToDisk`) — else
the watcher queues a redundant reconcile racing that refresh. **Not** a blanket first-party mute: NuGet's
`.csproj` write is structural and nothing else would tell the workspace to rebuild.

- **Expiry, not consumption.** One write can raise several `Changed` events, so a consume-once mute leaks the
  second one. A bounded window (~3 s) also guarantees a later *external* edit to the same file is caught.
- A muted write means nothing marked those paths pending — the follow-up refresh must `RequestFullRescan()`
  itself, or it drains an empty set and skips the very writes it just made.

## Observing it (the Disk panel)

`SolutionWatcher.Snapshot()` / `WorkspaceService.Stats()` + `.Dirty()` → `DiskInsightViewModel`. Read-only;
the services carry increments plus one snapshot read and know nothing about a panel.

- **Two silent failure modes are why it exists**: a watch that never started (`Start` → false →
  `DiskIsWatched` false → full-rescan polling forever, nothing on screen), and structural churn collapsing the
  tree with no named cause. A quiet feed and a fully-pruned feed also look identical without counters.
- **Count where the work already is.** Pruning runs *outside* `_gate` on purpose (a build's `obj/` churn never
  touches the lock) — count it with `Interlocked` rather than moving it in, or the counter defeats the
  optimization it measures.
- **Snapshot record, not a property per counter** — one consistent read, so a repaint can't show a torn view.
  Per-field `Interlocked.Read` gives exactly that torn view; it's the right tool only where the counter already
  lives outside a lock.
- **Never assemble a snapshot under a `SemaphoreSlim` an async operation holds across awaits** (e.g.
  `WorkspaceService._lock`): a UI-thread reader would block for the length of a reconcile. Give the counters
  their own plain gate. Observation must not perturb what it measures.
- **Cumulative since start; "reset" is the reader's baseline to subtract**, not a service method. Keeps the
  panel unable to write to a service at all.
- **Timer-gate the counter pull to the panel being visible; never the event subscription.** The log has to be
  live for the app's lifetime or tabbing away during a burst loses exactly the signals you wanted.

The funnel (`documents stat'd → read → forked`) is the reconcile's thesis made observable: read/stat is the
stamp gate's payoff, fork/read is the content gate's. Increments in funnel order make `read <= stat` and
`fork <= read` hold structurally.
