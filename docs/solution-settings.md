# Solution settings (`.miniide/`)

Per-solution state MiniIDE keeps: `.miniide/<Solution>.local.json`, beside the `.sln`/`.slnx`.

## Layout

- `*.local.json` = personal, never committed (same convention as `.claude/settings.local.json`, `.env.local`).
- A shared `<Solution>.json` may sit beside it later — committed, team-wide. None exists yet; don't add one
  until there's a setting that needs it.
- `.miniide/.gitignore` (`*.local.json`) is written on first save so personal files stay out of git **without
  touching the repo's own `.gitignore`**. Created once, never rewritten — a user edit to it is theirs.
- Folder, not a single file: two solutions in one directory each get their own file, and shared vs personal
  splits by filename.

## Why not elsewhere

- `.vs/` — Visual Studio's. `.idea/` — Rider's.
- `*.csproj.user` — MSBuild imports it into every build; UI state must not leak into evaluation.
- `%LOCALAPPDATA%` — stays out of every repo, but lost when the checkout moves, and invisible.

## Rules

- Paths inside are **solution-relative**, so the file survives the checkout moving.
- Unreadable file (bad hand-edit) ⇒ load reports it and **saving is refused** — never overwrite what we
  couldn't read.
- Writes go temp-file + `File.Move(overwrite)`, so a crash can't leave it truncated.
- `SolutionWatcher` watches `.miniide/*.json`, so a hand-edit or another MiniIDE window's star shows up live.
  Our own saves trigger one reload too; re-reading what we just wrote is a no-op, so they aren't filtered.

## Contents today

- `StarredProjects` — pinned to the top of the project list (then runnable, then by name).
