# Centralize File & Project Classification (Enum + Info-Record + FrozenDictionary)

## Context
**Current behavior**: The knowledge of "what kind of thing is this file/project/directory" is hardcoded in six independent places, and they have already drifted out of sync:

| Site | Owns |
|---|---|
| `FileIconMap.FromExtension` (`Models/FileIcon.cs`) | extension → (icon glyph, color) |
| `FileIconMap.FromProjectKind` (`Models/FileIcon.cs`) | `ProjectKind` → (icon glyph, color) |
| `ProjectEntry.IsRunnable` (`Models/ProjectEntry.cs`) | `ProjectKind` → runnable-ness |
| `HighlightModeExtensions.FromExtension` (`Models/HighlightMode.cs`) | extension → `HighlightMode` |
| `TabViewModelBase.CreateForFile` (`ViewModels/TabViewModelBase.cs`) | extension → editor-vs-image tab |
| `SearchService.SkippedDirectories` + `SolutionService.BuildFolder` | directory basenames to prune |

Observed drift: the two skip-dir lists disagree (`SearchService` prunes `node_modules`/`packages`; `BuildFolder` does not — so the tree shows folders that global-find silently refuses to search); the image icon list (`.svg`/`.ico` = Image) and the image-tab list (excludes `.svg`/`.ico`) diverge; `.xaml` gets an XML icon but no XML highlight; `.slnx` gets XML highlight but an Unknown icon.

**New behavior**: A single classification layer. An extension maps to exactly one `FileKind`, and each `FileKind` carries its icon glyph, color, highlight mode, and tab-kind in one `FileKindInfo` record looked up via a `static readonly FrozenDictionary<FileKind, FileKindInfo>`. `ProjectKind` gets the same treatment (`ProjectKindInfo` with glyph, color, runnable-ness). Directory pruning has one shared set both the tree walker and the search service consult. Every consumer derives from these single sources; no consumer keeps its own extension/kind table. Adding a file type is a one-line table edit that all consumers pick up.

This is a pure refactor with a few deliberate, documented behavior reconciliations (see Acceptance Criteria) — no new user-facing features.

## Prerequisites
None.

## Scope
### In scope
- New `FileKind` enum + `FileKindInfo` record + `GetInfo()` extension backed by a `FrozenDictionary<FileKind, FileKindInfo>`, plus an extension→`FileKind` lookup backed by a `FrozenDictionary<string, FileKind>`.
- `ProjectKind` converted to the same pattern (`ProjectKindInfo` + `GetInfo()`).
- One shared pruned-directory set replacing the two drifted lists.
- Rewiring all six consumer sites to derive from the above; deleting their private tables.

### Out of scope
- The icon glyph constants (`FileIcon`) and the color brushes (`FileIconPalette`) — these stay as the *values* the info records point at; only the *dispatch logic* is centralized. Do not redefine glyphs/colors.
- The `RoslynColorizer.BrushFor` palette / brush-allocation cleanup (separate retro finding #3, separate ticket).
- Changing `HighlightMode`'s member set or the `RefreshAndRedraw` dispatch switch — the enum stays; only its extension-mapping helper moves.
- Content-based classification (extension remains the source of truth).
- Cross-platform directory-prune differences.

## Relevant Docs & Anchors
- **Code anchors** (read before coding):
  - `Models/FileIcon.cs` — `FileIcon` glyph constants (keep), `FileIconPalette` (separate file, keep), and `FileIconMap` with `From(TreeNode)` (NodeKind dispatcher — keep as orchestrator), `FromProjectKind` (public, to be folded into `ProjectKind.GetInfo`), `FromExtension` (private table, to be replaced).
  - `Models/HighlightMode.cs` — `HighlightMode` enum (keep) + `HighlightModeExtensions.FromExtension` (delete after moving its mapping into `FileKindInfo.Highlight`).
  - `Models/ProjectKind.cs` — the `ProjectKind` enum (extend with the info-record pattern in this file).
  - `Models/ProjectEntry.cs` — `IconGlyph`/`IconColor`/`IsRunnable` computed props (rewire to `Kind.GetInfo()`).
  - `Models/SolutionNode.cs` — `TreeNode.IconGlyph`/`IconColor` (call `FileIconMap.From` — unchanged surface).
  - `ViewModels/TabViewModelBase.cs` — `CreateForFile` + its `ImageExtensions` array (replace with `FileKind` tab-kind lookup).
  - `ViewModels/EditorTabViewModel.cs` — ctor sets `Mode` from `HighlightModeExtensions.FromExtension` (rewire).
  - `Services/SearchService.cs` — `SkippedDirectories` (replace with shared set).
  - `Services/SolutionService.cs` — `BuildFolder`'s inline `if (name is "bin" or "obj" or ".vs" or ".git")` (replace with shared set).
  - `App.axaml.cs` — `ResolveStartupSolution`'s inline `.sln`/`.slnx` extension check (rewire to `FileKind`).
  - `ViewModels/MainWindowViewModel.cs` — `PickDefaultStartup` uses `e.Kind != ProjectKind.Lib` (switch to `e.IsRunnable`).
- **Related tickets** (context, not a shape to copy):
  - `docs/tickets/complete/2026-07-04 file-type-icons.md` — established `FileIcon`/`FileIconPalette`/`FileIconMap` and the icon extension table.
  - `docs/tickets/complete/2026-07-04 project-kind-icons-in-dropdowns.md` — promoted `FromProjectKind` to public, added `ProjectEntry.IsRunnable`; its Learnings cover the compiled-binding requirement on `ProjectEntry.IconGlyph`/`IconColor`/`IsRunnable`.
  - `docs/tickets/complete/2026-07-05 xml-json-syntax-highlighting.md` — created `HighlightMode` + `HighlightModeExtensions.FromExtension`; documents the highlight extension set.
  - `docs/tickets/complete/2026-07-05 image-preview-tabs.md` — created `CreateForFile`; its Open Decision #4 and Out-of-scope explain why `.svg`/`.ico` are image-*icon* but open as *text* (Skia can't decode ICO; SVG needs `Avalonia.Svg.Skia`).

## Constraints & Gotchas
- **`FrozenDictionary`/`FrozenSet` live in `System.Collections.Frozen`** — in the BCL on `net10.0` (the project's TFM per `MiniIde.csproj`); no package reference needed. Build the frozen collections once into `static readonly` fields (`.ToFrozenDictionary(...)` / `.ToFrozenSet(...)`), not per call.
- **Extension lookup must be case-insensitive.** Files like `.PNG` occur. Build the `FrozenDictionary<string, FileKind>` with `StringComparer.OrdinalIgnoreCase`, and the pruned-directory `FrozenSet<string>` likewise — this mirrors the current `ToLowerInvariant()` / `OrdinalIgnoreCase` behavior at the sites being replaced.
- **`ProjectEntry.IconGlyph`/`IconColor`/`IsRunnable` are consumed by compiled XAML bindings** (`ComboBoxItem` `IsEnabled="{Binding IsRunnable}"` in `MainWindow.axaml`, and the dropdown icon bindings). Keep them as non-null public instance members on `ProjectEntry`; only change their bodies to delegate to `Kind.GetInfo()`.
- **`FileIconMap.From(TreeNode)` stays the NodeKind-level orchestrator.** `Folder` and `Solution` node kinds still resolve to the folder glyph; only the `File` arm (→ `extension.ToFileKind().GetInfo()`) and the `Project` arm (→ `projectKind.GetInfo()`) change. Do not push NodeKind logic into `FileKind`.
- **Reconciling divergent cases changes some behavior** — this is intended, not a regression. See Acceptance Criteria for the exact target mapping. Call out the deliberate changes (`.xaml`/`.config`/`.props`/`.targets` now highlight as XML; the tree now prunes `node_modules`/`packages`) in the implementation so a reviewer doesn't read them as accidents.
- **Keep the `HighlightMode` enum.** `RefreshAndRedraw` in `MainWindow.axaml.cs` switches on it and `EditorTabViewModel.Mode` exposes it. Only the extension→mode *mapping* moves into `FileKindInfo`; delete `HighlightModeExtensions` once its sole caller is rewired.

## Open Decisions
1. **`.sln` vs `.slnx` highlight** — both are solution files (needed as one group by `ResolveStartupSolution`), but `.slnx` is XML and legacy `.sln` is not. Default: one `FileKind.Solution` for both with `Highlight = Xml` (`.slnx`-correct; `.sln` gets cosmetic XML coloring — rarely opened as text, `.slnx` is the modern default). Alternative: split into two kinds so `.sln` stays `Highlight = None`. Implementer's call; if split, give `ResolveStartupSolution` a small `IsSolution` helper spanning both.
2. **Tab-kind representation on `FileKindInfo`** — a `bool OpensAsImageTab` vs a small `TabKind { Editor, Image }` enum. Default: `bool` (only two tab types today). Enum if a third preview type feels imminent.
3. **`ToFileKind` parameter** — take the bare extension (`Path.GetExtension(path)`, with/without leading dot, case-insensitive) mirroring the old `HighlightModeExtensions.FromExtension`, vs. take a full path and extract internally. Default: bare extension; callers already hold `Path.GetExtension(...)`. Add a path overload only if it reads cleaner.
4. **Home for the pruned-directory set** — a new `Models/IdeDirectories.cs` static (no deps, shared by two services) vs. hanging it off one service. Default: standalone static in `Models/`.
5. **Solution-file glyph** — `.slnx`/`.sln` currently fall through to the Unknown icon as file nodes. Default: reuse the existing `FileIcon.Csproj` (Visual Studio) glyph + `FileIconPalette.Csproj` for `FileKind.Solution`. Add a dedicated glyph only if trivially available in the pinned MDI set.

## Acceptance Criteria
- [ ] `FileKind` enum exists with a member per distinct icon/highlight/tab category; every extension currently handled maps to exactly one member, with an `Unknown` fallback.
- [ ] `FileKindInfo` record exists carrying at least: icon glyph (referencing a `FileIcon` constant), color (referencing a `FileIconPalette` brush), `HighlightMode`, and tab-kind (per Open Decision #2).
- [ ] `FileKind.GetInfo()` extension returns the info via a `static readonly FrozenDictionary<FileKind, FileKindInfo>`; the dictionary has an entry for every enum member (no missing-key path at runtime).
- [ ] A `ToFileKind` extension maps an extension string → `FileKind` via a `static readonly FrozenDictionary<string, FileKind>` built with `StringComparer.OrdinalIgnoreCase`, returning `FileKind.Unknown` for unmapped extensions.
- [ ] `ProjectKind.GetInfo()` returns a `ProjectKindInfo(glyph, color, IsRunnable)` via its own `static readonly FrozenDictionary<ProjectKind, ProjectKindInfo>`, with an entry for all four kinds (`Exe`, `Lib`, `Web`, `Tst`); `IsRunnable` is `true` for all except `Lib`.
- [ ] A single pruned-directory `FrozenSet<string>` (case-insensitive) contains `.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages`; both `SearchService` and `SolutionService.BuildFolder` consult it, and neither retains its own literal list.
- [ ] No consumer keeps a private extension/kind/skip table: `FileIconMap.FromExtension`, `HighlightModeExtensions`, `TabViewModelBase.ImageExtensions`, `SearchService.SkippedDirectories`, `BuildFolder`'s inline check, and `ResolveStartupSolution`'s inline `.sln`/`.slnx` check are all gone or reduced to a single call into the new layer.
- [ ] Icon regression preserved for the previously-correct cases: `.cs`→C# glyph, `.json`→Json glyph, `.csproj`→Csproj glyph, `.xml`/`.axaml`/`.xaml`/`.config`/`.props`/`.targets`→Xml glyph, image/audio/video/text extensions→their existing glyphs, unknown→Unknown glyph.
- [ ] Highlight regression preserved: `.cs`→`CSharp`, `.csproj`/`.slnx`/`.xml`/`.axaml`→`Xml`, `.json`→`Json`, plain/unknown→`None`.
- [ ] **Deliberate reconciliation** — `.xaml`, `.config`, `.props`, `.targets` now resolve to `HighlightMode.Xml` (previously `None` while already showing the XML icon).
- [ ] **Deliberate reconciliation** — a project directory containing `node_modules` and/or `packages` no longer shows those folders in the solution tree (previously shown but never searched).
- [ ] Tab-kind preserved: `.png`/`.jpg`/`.jpeg`/`.bmp`/`.webp`/`.gif` open in an image tab; `.svg`/`.ico` open in an editor (text) tab despite showing the image icon; all other extensions open in an editor tab.
- [ ] `MiniIde.exe "<path>.slnx"` and `"<path>.sln"` still auto-load at startup; a non-solution argument still starts empty.
- [ ] `ProjectEntry.IconGlyph`, `IconColor`, `IsRunnable` remain non-null public members usable by compiled bindings; the startup-project ComboBox still disables `Lib` projects and `PickDefaultStartup` still selects the first runnable project.

## Implementation

### 1. Add the `FileKind` classification layer
New file `Models/FileKind.cs`. Define the `FileKind` enum, the `FileKindInfo` record, and a static extensions class holding two frozen lookups and the `GetInfo()` / `ToFileKind()` methods. The `FileKindInfo` for each member references existing `FileIcon.*` glyph constants and `FileIconPalette.*` brushes — do not introduce new glyph/color values. Build both `FrozenDictionary`s once into `static readonly` fields; the extension→kind dictionary uses `StringComparer.OrdinalIgnoreCase`. Target mapping (the load-bearing data — implement exactly, subject to Open Decisions #1/#2/#5):

| FileKind | Extensions | Glyph | Color | Highlight | Image tab? |
|---|---|---|---|---|---|
| `CSharp` | `.cs` | `CSharp` | `CSharp` | `CSharp` | no |
| `Json` | `.json` | `Json` | `Json` | `Json` | no |
| `Xml` | `.xml .axaml .xaml .config .props .targets` | `Xml` | `Xml` | `Xml` | no |
| `Csproj` | `.csproj` | `Csproj` | `Csproj` | `Xml` | no |
| `Solution` | `.slnx .sln` | `Csproj` | `Csproj` | `Xml` | no |
| `Image` | `.png .jpg .jpeg .bmp .webp .gif` | `Image` | `Image` | `None` | **yes** |
| `VectorImage` | `.svg .ico` | `Image` | `Image` | `None` | no |
| `Audio` | `.wav .mp3 .ogg .flac` | `Audio` | `Audio` | `None` | no |
| `Video` | `.mp4 .mov .avi .webm .mkv` | `Video` | `Video` | `None` | no |
| `Text` | `.txt .md .editorconfig` | `Text` | `Text` | `None` | no |
| `Unknown` | (fallback) | `Unknown` | `Unknown` | `None` | no |

`GetInfo()` indexes the `FileKind`→`FileKindInfo` dictionary; `ToFileKind(extension)` indexes the extension dictionary with an `Unknown` fallback (`TryGetValue` / `GetValueOrDefault`).

### 2. Convert `ProjectKind` to the same pattern
In `Models/ProjectKind.cs`, alongside the enum, add a `ProjectKindInfo(string Glyph, IBrush Color, bool IsRunnable)` record and a `GetInfo()` extension backed by a `static readonly FrozenDictionary<ProjectKind, ProjectKindInfo>`. Populate from the current `FileIconMap.FromProjectKind` table + the `!= Lib` runnable rule: `Exe`→(`ProjectExe`, runnable), `Lib`→(`ProjectLib`, not runnable), `Web`→(`ProjectWeb`, runnable), `Tst`→(`ProjectTst`, runnable), each with the matching `FileIconPalette` brush.

### 3. Rewire `FileIconMap`
In `Models/FileIcon.cs`, keep `From(TreeNode)` as the NodeKind orchestrator but replace its `File` arm to resolve via `Path.GetExtension(node.Path).ToFileKind().GetInfo()` and its `Project` arm via `(node.ProjectKind ?? ProjectKind.Exe).GetInfo()`. Delete the private `FromExtension` table. Remove `FromProjectKind` (its only callers are this dispatcher and `ProjectEntry`, both moving to `GetInfo()`); if keeping the public surface is easier for bindings, leave a one-line shim delegating to `GetInfo()` — implementer's call.

### 4. Rewire `ProjectEntry`
In `Models/ProjectEntry.cs`, change `IconGlyph`, `IconColor`, and `IsRunnable` to read from `Kind.GetInfo()`. Keep them as public non-null instance members (compiled bindings depend on them).

### 5. Rewire highlight-mode derivation
In `ViewModels/EditorTabViewModel.cs`, set `Mode` from `Path.GetExtension(filePath).ToFileKind().GetInfo().Highlight`. In `Models/HighlightMode.cs`, delete `HighlightModeExtensions.FromExtension` (and the class if it's now empty); keep the `HighlightMode` enum.

### 6. Rewire tab-kind dispatch
In `ViewModels/TabViewModelBase.cs`, replace the `ImageExtensions` array and the loop in `CreateForFile` with a check on `Path.GetExtension(path).ToFileKind().GetInfo()`'s tab-kind: image-tab → `new ImageTabViewModel(path)`, else `new EditorTabViewModel(path)`. Delete the `ImageExtensions` field.

### 7. Rewire startup-solution recognition
In `App.axaml.cs`, replace the inline `.sln`/`.slnx` extension comparison in `ResolveStartupSolution` with `Path.GetExtension(arg).ToFileKind() == FileKind.Solution` (per Open Decision #1; if `.sln`/`.slnx` were split, use the `IsSolution` helper). Keep the `File.Exists` + `Path.GetFullPath` guards unchanged.

### 8. Unify the pruned-directory set
Create the shared set (per Open Decision #4, default `Models/IdeDirectories.cs`): a `static readonly FrozenSet<string>` (case-insensitive) of `.git bin obj node_modules .vs packages`. In `SearchService`, replace `SkippedDirectories` with it (the `SkipDirectory` predicate still keys off `Path.GetFileName(path)`). In `SolutionService.BuildFolder`, replace the inline `if (name is "bin" or "obj" or ".vs" or ".git") continue;` with a `.Contains(name)` check against the shared set.

### 9. Final consistency sweep
- In `MainWindowViewModel.PickDefaultStartup`, replace `e.Kind != ProjectKind.Lib` with `e.IsRunnable` (now the single source of runnable-ness).
- Grep for `FromExtension`, `FromProjectKind`, `SkippedDirectories`, `ImageExtensions`, `HighlightModeExtensions` and confirm no stragglers remain outside the new layer.
- Confirm every `FileKind` and `ProjectKind` member has a dictionary entry (a missing entry is a latent `KeyNotFoundException`).

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`.
- [ ] Tree icons unchanged for `.cs`, `.csproj`, `.json`, `.xml`/`.axaml`, image/text files (spot-check against pre-change appearance).
- [ ] Open a `.cs` file → Roslyn colorization; `.csproj`/`.slnx`/`.json` → their existing highlighting (regression).
- [ ] Open an `.axaml` and (create if needed) a `.props`/`.config`/`.targets` file → now XML-highlighted (the deliberate reconciliation).
- [ ] Open a `.png` → image tab renders; open a `.svg` and a `.ico` → editor (text) tab, no crash (image icon in tree, text content).
- [ ] Global find: `Ctrl+Shift+F`, search a common token — results appear and `bin`/`obj`/`.git`/`node_modules`/`packages` are absent from hits.
- [ ] If a project dir contains `node_modules` or `packages`, confirm the tree no longer lists them (deliberate reconciliation); otherwise note as not-reachable in this repo.
- [ ] Startup-project ComboBox: `Lib` projects appear disabled; a runnable project is preselected; F5 runs it.
- [ ] Launch `MiniIde.exe "$(Resolve-Path MiniIde.slnx)"` → solution auto-loads; launch with a bogus/non-solution argument → starts empty, no crash.
- [ ] Regression: right-click tree/tab/solution-name context menus and path actions still work (untouched, but they read `TreeNode`/`ProjectEntry` which changed internally).
