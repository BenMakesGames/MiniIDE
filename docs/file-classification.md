# File & project classification

Single source of truth for "what kind of thing is this file/project/directory". Add a file type in one place; all consumers pick it up.

- `Models/FileKind.cs` — `FileKind` enum + `FileKindInfo(Glyph, Color, Highlight, OpensAsImageTab)` record.
  - `FileKind.GetInfo()` → info, via `static readonly FrozenDictionary<FileKind, FileKindInfo>` (one entry per member).
  - `ext.ToFileKind()` → `FileKind`, via `static readonly FrozenDictionary<string, FileKind>` built `OrdinalIgnoreCase`; unmapped → `FileKind.Unknown`. Takes a bare extension (`Path.GetExtension(path)`, nullable; `null` → `Unknown`).
- `Models/ProjectKind.cs` — same shape: `ProjectKind.GetInfo()` → `ProjectKindInfo(Glyph, Color, IsRunnable)`. `IsRunnable` true for all but `Lib`.
- `Models/IdeDirectories.cs` — `Pruned` `FrozenSet<string>` (`OrdinalIgnoreCase`) of dirs the IDE never descends into. Consulted by both the solution-tree walk (`SolutionService.BuildFolder`) and global find (`SearchService`).

## Consumers (all derive — none keeps its own table)
- Tree icons → `FileIconMap.From(TreeNode)` (NodeKind orchestrator; `File`/`Project` arms call `GetInfo()`).
- Dropdown icons + startup-enable → `ProjectEntry.IconGlyph/IconColor/IsRunnable` (non-null public members; compiled XAML bindings depend on them).
- Editor syntax highlight → `EditorTabViewModel.Mode = ext.ToFileKind().GetInfo().Highlight`.
- Editor-vs-image tab → `TabViewModelBase.CreateForFile` checks `.OpensAsImageTab`.
- Startup-solution recognition → `App.ResolveStartupSolution`: `ext.ToFileKind() == FileKind.Solution`.

## Idioms & gotchas
- `System.Collections.Frozen` is in the BCL on `net10.0` — no package ref. Build frozen collections once into `static readonly` fields, never per-call.
- **Every enum member needs a dictionary entry.** A gap is a `KeyNotFoundException` at first icon render, not a compile error. Loading any solution builds the whole tree, so a launch smoke test surfaces gaps.
- Extension lookup is case-insensitive (`.PNG` etc.) — build the string dictionary/set with `StringComparer.OrdinalIgnoreCase`.
- `.svg`/`.ico` = `VectorImage`: image *icon*, but open as a *text* tab (Skia can't decode ICO; SVG needs `Avalonia.Svg.Skia`).
- `.sln`/`.slnx` share one `FileKind.Solution` (VS glyph, `Highlight = Xml` — correct for `.slnx`, cosmetic for legacy `.sln`).
- Keep NodeKind logic out of `FileKind` — `Folder`/`Solution` node glyphs resolve in `FileIconMap.From`, not the classifier.
