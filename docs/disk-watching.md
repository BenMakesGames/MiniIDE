# Disk watching

`SolutionWatcher` = one `FileSystemWatcher` over the solution root, filtered to the files the **project list**
reads → one debounced, payload-free `Changed` → `MainWindowViewModel` reloads the list.

## What's watched

- `*.sln`, `*.slnx` — membership.
- `*.csproj` — project kind (icon, runnable or not) and NuGet packages (open NuGet tabs re-read).
- `launchSettings.json` — the card's launch-profile group.
- `.miniide/*.json` — stars (see `solution-settings.md`). The reload re-reads settings before the list.
- Nothing else. `SolutionWatcher.IsRelevant` is the whole filter; widen it only for a file the list reads.

## What's deliberately not watched

- **Source files.** Editor tabs revalidate on activation and window focus (stamp-gated: a `stat` when
  unchanged). The Roslyn snapshot reconciles on every semantic query — see `roslyn.md §Disk reconcile`.
- **`.git`.** Repo state is polled at focus; see `git.md`.

## Diagnostics

- `SolutionWatcher.Snapshot()` → `WatchStats`: counters (seen / relevant / reloads / overflows) + the last 50
  bursts with their paths. Observation only; `Changed` stays payload-free.
- Shown by the **File-watcher diagnostics** tab (top bar "?" menu). Polls 1 s while active; Reset subtracts a
  baseline rather than writing to the watcher.

## Why the signal carries nothing

- The response is always "reload the list": metadata-only parse + one XML read per project. Cheap and
  idempotent, so there's nothing to triage — no paths, no structural flag, no overflow flag.
- Overflow (`Error`) just schedules the same reload.
- The reload reconciles cards **by path** and updates them in place — a project-file save mustn't reset the
  card's configuration pick, drop its run buttons, or close an open flyout.

## FileSystemWatcher gotchas

- `NotifyFilter`: `FileName` (create/delete/rename) + `LastWrite | Size` (content). No `DirectoryName` — a
  directory event can't be a relevant file.
- A rename raises one `Renamed` carrying **both** `OldFullPath` and `FullPath` — check both (atomic saves
  write a temp file and rename it over the real one).
- Events arrive on **threadpool** threads. Marshal UI-thread work; never throw out of a handler.
- Debounce is trailing-edge (~250 ms). `Timer.Change` after `Dispose` throws `ObjectDisposedException` —
  swallow that race.
- `Start` returns false rather than throwing: a solution that can't be watched still opens; Reload solution
  is the manual fallback.
