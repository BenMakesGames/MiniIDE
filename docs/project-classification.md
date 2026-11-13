# Project classification

Single source of truth for "what kind of project is this". Add a kind in one place; all consumers pick it up.

- `Models/ProjectKind.cs` — `ProjectKind` enum + `ProjectKindInfo(Glyph, Color, IsRunnable)` record.
  - `ProjectKind.GetInfo()` → info, via `static readonly FrozenDictionary<ProjectKind, ProjectKindInfo>` (one entry per member).
  - `IsRunnable` true for all but `Lib`.
- Glyphs: `Models/ProjectIcon.cs`. Brushes: `Models/ProjectIconPalette.cs`.
- Solution files: `SolutionService.IsSolutionFile(path)` (`.sln`/`.slnx`, any case). Used by startup-arg recognition (`App.ResolveStartupSolution`) and `SolutionWatcher.IsRelevant`.

## Consumers (all derive — none keeps its own table)
- Project-card icons + Run enablement → `ProjectEntry.IconGlyph/IconColor/IsRunnable` (non-null public members; compiled XAML bindings depend on them).

## Idioms & gotchas
- `System.Collections.Frozen` is in the BCL on `net10.0` — no package ref. Build frozen collections once into `static readonly` fields, never per-call.
- **Every enum member needs a dictionary entry.** A gap is a `KeyNotFoundException` at first icon render, not a compile error.
