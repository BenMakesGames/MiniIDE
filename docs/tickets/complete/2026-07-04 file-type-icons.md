# File-Type Icons in Solution Tree

## Context
**Current behavior**: `TreeView` items render name-only via a single `TextBlock` in the `TreeDataTemplate`. No icons for solution/project/folder/file distinction; no visual cue for file type.

**New behavior**: Each tree row shows a colored icon before the name. Icons come from Material Design Icons (MDI) rendered via an icon font — one glyph per category, foreground brush colors it. File icons dispatch on extension; folder nodes get a folder icon; project nodes get a per-`ProjectKind` icon (Exe / Lib / Web / Tst distinct).

## Prerequisites
None. Sibling to `xml-json-syntax-highlighting.md` — same extension-dispatch pattern, independent map.

## Scope
### In scope
- Bundle MDI TTF + Apache 2.0 LICENSE under `src/MiniIde/Assets/icons/`.
- Register `IconFont` `FontFamily` resource in `App.axaml`.
- New `Models/FileIcon.cs` — glyph constants + `FileIconMap.From(TreeNode)` dispatch.
- New `Models/FileIconPalette.cs` — one `IBrush` per icon category, single source of truth.
- Extend `TreeNode` (`Models/SolutionNode.cs`) — nullable `ProjectKind`, computed `IconGlyph` + `IconColor`.
- `SolutionService`: populate new `TreeNode.ProjectKind` when building project nodes; widen `BuildFolder` extension filter so audio/image/video files enter the tree (otherwise icons for those categories are dead code).
- `Views/MainWindow.axaml` `TreeDataTemplate` — icon `TextBlock` before name `TextBlock`.

### Out of scope
- Icons in top-bar startup `ComboBox`, editor tab headers, or NuGet panel.
- `.slnx` file icon — `.slnx` never appears in tree (solution root flattened away, see `2026-07-04 top-bar-and-explorer-flatten.md`).
- Runtime color theming or user-configurable palette. Hex values compile-in.
- Font subsetting. Ship full MDI TTF; trim later if size matters.
- Custom xshd or highlight changes.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Models/SolutionNode.cs` — `TreeNode`, `NodeKind`.
  - `Models/ProjectKind.cs` — `Exe / Lib / Web / Tst`.
  - `Models/ProjectEntry.cs` — `record ProjectEntry(string Path, string Display, ProjectKind Kind)`.
  - `Services/SolutionService.cs` — project node construction (`new TreeNode { Kind = NodeKind.Project }`) and `BuildFolder` extension filter.
  - `Views/MainWindow.axaml` — `TreeDataTemplate DataType="m:TreeNode"` (the `StackPanel` currently holding one `TextBlock`).
  - `App.axaml` — `Application.Resources` insertion site for `IconFont`.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 top-bar-and-explorer-flatten.md` — flat tree, solution root not rendered.
  - `docs/tickets/complete/2026-07-04 startup-dropdown-project-kinds.md` — `ProjectKind` classifier precedent.
  - `docs/tickets/xml-json-syntax-highlighting.md` (pending) — parallel extension-dispatch pattern; keep icon map and highlight map separate.
- **External**: MDI project at pictogrammers.com. Codepoint reference: `meta.json` in the release you pin.

## Constraints & Gotchas
- **`AvaloniaResource` glob**: `MiniIde.csproj` already has `<AvaloniaResource Include="Assets\**" />` — new files under `Assets/icons/` are picked up automatically; no csproj edit needed.
- **FontFamily URI**: Avalonia syntax is `avares://<AssemblyName>/<path>#<internal font name>`. The `#` suffix must match the font's internal Family Name, not the filename. For MDI desktop TTF the internal name is `Material Design Icons`. Verify by opening the TTF (Windows Font Viewer shows the family name) before wiring up.
- **MDI codepoints are outside BMP**: Glyphs live at U+F0000+ (Supplementary Private Use Area-A). C# `\uXXXX` escapes only reach U+FFFF; you must use `\U000FXXXX` (uppercase `\U`, 8 hex digits) which the compiler expands to a surrogate pair. In XAML `Text="..."`, paste the actual glyph or use `&#xF0XXX;` — XAML entities accept arbitrary Unicode.
- **Codepoint drift**: MDI has renumbered glyphs on major releases in the past. Pin the TTF version in-repo; do not fetch fresh at build. Note the pinned MDI version in a comment atop `FileIcon.cs`.
- **License compliance**: MDI is Apache 2.0. Copy its LICENSE into `Assets/icons/` alongside the TTF. Non-negotiable.
- **`BuildFolder` extension filter**: currently whitelists only `.cs .csproj .json .xml .axaml .xaml .md .txt .editorconfig`. If you don't widen it, audio/image/video files never render, and their palette entries are dead code. Widen per Acceptance Criteria list.
- **Case sensitivity**: mirror existing `Path.GetExtension(f).ToLowerInvariant()` convention already used in `BuildFolder`.
- **Row-height regression**: 16px icon at the same font metric as the name text fits current row height. If Avalonia grows the row visibly, reduce to 14px before restyling further.
- **Compiled bindings**: template uses `x:DataType="vm:MainWindowViewModel"` at window level and `DataType="m:TreeNode"` on the template. `IconGlyph` and `IconColor` must be real properties on `TreeNode` (not attached via converter) so the compiled binding sees them.

## Open Decisions
1. **Palette hex values** — starter values below in Implementation §3 are a starting point. Tune during the visual pass; final values are implementer's call.
2. **Icon size** — 16px default; drop to 14 if rows grow visibly.
3. **Unknown-extension fallback** — `file-outline` glyph in neutral gray, per palette below. Alternative: no icon at all (empty glyph). Default: render `file-outline` — consistent row layout.
4. **Cache computed `(glyph, color)` on `TreeNode`** — one lookup per node at construction vs. recomputing on every binding read. Default: compute on-demand via property getters; revisit only if tree feels sluggish at scale.
5. **`Csproj` icon** — use `microsoft-visual-studio` glyph (MDI has it) vs. sharing the C# glyph vs. sharing the XML glyph. Default: `microsoft-visual-studio` — distinct from `.cs` in the tree.

## Acceptance Criteria
- [ ] `src/MiniIde/Assets/icons/` contains the pinned MDI TTF and an Apache 2.0 `LICENSE` file.
- [ ] `App.axaml` declares a `FontFamily x:Key="IconFont"` resource pointing at the bundled TTF.
- [ ] `Models/FileIcon.cs` exists with named glyph constants and a static `FileIconMap.From(TreeNode) → (string Glyph, IBrush Color)`.
- [ ] `Models/FileIconPalette.cs` exists with `IBrush` fields for: `CSharp`, `Json`, `Xml`, `Csproj`, `Folder`, `Text`, `Audio`, `Image`, `Video`, `ProjectExe`, `ProjectLib`, `ProjectWeb`, `ProjectTst`, `Unknown`.
- [ ] Extension mapping (case-insensitive, ordinal):
  - `.cs` → CSharp
  - `.json` → Json
  - `.csproj` → Csproj
  - `.xml`, `.axaml`, `.xaml`, `.config`, `.props`, `.targets` → Xml
  - `.txt`, `.md`, `.editorconfig` → Text
  - `.wav`, `.mp3`, `.ogg`, `.flac` → Audio
  - `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.webp`, `.svg`, `.ico` → Image
  - `.mp4`, `.mov`, `.avi`, `.webm`, `.mkv` → Video
  - anything else → Unknown
- [ ] `TreeNode.Kind == NodeKind.Folder` → Folder glyph + Folder brush.
- [ ] `TreeNode.Kind == NodeKind.Project` → glyph + brush chosen by that node's `ProjectKind` (Exe / Lib / Web / Tst each distinct).
- [ ] `TreeNode` exposes non-null `IconGlyph` (string) and `IconColor` (`IBrush`) usable by compiled bindings.
- [ ] `SolutionService`, when building project nodes, sets `TreeNode.ProjectKind` from the classified `ProjectEntry.Kind`.
- [ ] `SolutionService.BuildFolder` extension whitelist covers every extension listed above (so file-type icons actually render for audio/image/video files in-repo, not just theoretically).
- [ ] `MainWindow.axaml` `TreeDataTemplate` renders icon left of name, both vertically centered, small horizontal gap.
- [ ] Opening `MiniIde.slnx` shows a colored project icon per project, folder icon on each folder, per-extension icons on files.

## Implementation

### 1. Add MDI font asset + license
Download the pinned MDI Desktop TTF from Pictogrammers' current stable release. Drop the file (e.g. `MaterialDesignIconsDesktop.ttf`) into `src/MiniIde/Assets/icons/`. Copy the Apache 2.0 `LICENSE` file alongside. `AvaloniaResource` glob in the csproj already includes `Assets/**` — no csproj change. Add a short `README.md` in `Assets/icons/` noting the pinned MDI version so future updates are traceable.

### 2. Register `IconFont` in `App.axaml`
Add `<Application.Resources>` block (currently absent) with a `FontFamily` resource:

```xml
<Application.Resources>
    <FontFamily x:Key="IconFont">avares://MiniIde/Assets/icons/MaterialDesignIconsDesktop.ttf#Material Design Icons</FontFamily>
</Application.Resources>
```

Verify the `#` suffix matches the TTF's internal family name (see Constraints).

### 3. Add `FileIconPalette`
`Models/FileIconPalette.cs`: static class holding one `IBrush` per category. Use `SolidColorBrush` constructed from `Color.Parse("#RRGGBB")`. Starting values (tune during visual pass):

- CSharp `#68217A`
- Json `#F5B301`
- Xml `#E37933`
- Csproj `#68217A` (share C# family purple)
- Folder `#DCB67A`
- Text `#B0B0B0`
- Audio `#4EC9B0`
- Image `#C586C0`
- Video `#569CD6`
- ProjectExe `#569CD6`
- ProjectLib `#B0B0B0`
- ProjectWeb `#4EC9B0`
- ProjectTst `#98C379`
- Unknown `#808080`

### 4. Add `FileIcon` glyph constants + `FileIconMap`
`Models/FileIcon.cs`: static class with `const string` per category, encoded via `\U000FXXXX` escapes (see Constraints — MDI codepoints are outside the BMP). Suggested MDI glyph names (verify codepoints against pinned `meta.json`):

- CSharp — `language-csharp`
- Json — `code-json`
- Xml — `xml`
- Csproj — `microsoft-visual-studio`
- Folder — `folder`
- Text — `file-document`
- Audio — `music-note` (or `file-music`)
- Image — `file-image`
- Video — `file-video`
- ProjectExe — `application`
- ProjectLib — `library`
- ProjectWeb — `web`
- ProjectTst — `test-tube`
- Unknown — `file-outline`

In the same file, `public static class FileIconMap` with `public static (string Glyph, IBrush Color) From(TreeNode node)`:
- Switch on `node.Kind`.
- `NodeKind.Folder` → Folder glyph + Folder brush.
- `NodeKind.Project` → dispatch on `node.ProjectKind` (nullable — if null, fall back to `ProjectExe` defensively).
- `NodeKind.File` → `Path.GetExtension(node.Path).ToLowerInvariant()` switch matching the Acceptance Criteria table; default `Unknown`.
- `NodeKind.Solution` → Folder (defensive; never rendered post-flatten).

Note pinned MDI version at the top of the file in a comment.

### 5. Extend `TreeNode`
`Models/SolutionNode.cs`:
- Add `public ProjectKind? ProjectKind { get; init; }` — nullable, zero cost for non-project nodes.
- Add computed properties `public string IconGlyph => FileIconMap.From(this).Glyph;` and `public IBrush IconColor => FileIconMap.From(this).Color;`. Simple getters — see Open Decision #4 if perf matters later.
- Add `using Avalonia.Media;` for `IBrush`.

### 6. Populate `ProjectKind` in `SolutionService`
`Services/SolutionService.cs` — the `new TreeNode { Kind = NodeKind.Project }` block inside `LoadAsync`. Add `ProjectKind = kind,` (the local `kind` variable already exists from `ProjectClassifier.Classify(abs)`).

### 7. Widen `BuildFolder` extension filter
`Services/SolutionService.cs` `BuildFolder`: replace the current whitelist with a filter that admits every extension listed in Acceptance Criteria, plus preserves current inclusions (`.md`, `.editorconfig`). Keep the existing `bin/obj/.vs/.git` folder skip. Extension check remains `Path.GetExtension(f).ToLowerInvariant()`.

Consider: extract the allow-list into a `HashSet<string>` field on `SolutionService` — cleaner than a growing `is or or or` expression once the list crosses ~15 entries. Local taste; implementer decides.

### 8. Update tree template
`Views/MainWindow.axaml` `TreeDataTemplate DataType="m:TreeNode"` — the inner `StackPanel Orientation="Horizontal"`:
- Prepend a `TextBlock` bound to the icon: `FontFamily="{StaticResource IconFont}"`, `FontSize="16"`, `Text="{Binding IconGlyph}"`, `Foreground="{Binding IconColor}"`, `Margin="0,0,6,0"`, `VerticalAlignment="Center"`.
- Existing name `TextBlock` unchanged apart from adding `VerticalAlignment="Center"` for row consistency.

### 9. Visual pass and palette tune
Launch via `scripts/run.ps1`. Open `MiniIde.slnx`. Expand each project. Confirm:
- Each project shows its `ProjectKind` icon in the palette color.
- Folders show tan folder icon; files show per-extension icon + color.
- Icons are readable against `#1E1E22` background.
- Row height unchanged from pre-icon baseline (compare against `git stash` if unsure).

Tune palette hex values in `FileIconPalette` only — no other churn. If any icon is unreadable on dark bg, that's the fix location.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`.
- [ ] Open `MiniIde.slnx`. Top-level project node renders with an exe-colored project icon (`MiniIde` is an Exe project per its csproj `OutputType`).
- [ ] Expand `MiniIde` project. `Models/`, `Views/`, `ViewModels/`, `Services/`, `Assets/` render with folder icon in tan.
- [ ] `.cs` files (e.g. `Program.cs`, `MainWindowViewModel.cs`) render with C# icon in purple.
- [ ] `MiniIde.csproj` renders with the csproj (VS) icon.
- [ ] `App.axaml` and `MainWindow.axaml` render with XML icon in orange.
- [ ] Create a scratch `test.png` (or drop any image) in the project directory. Reload. It renders with image icon.
- [ ] Same for a `test.mp3` and `test.mp4`. Both render with audio and video icons respectively.
- [ ] Create a scratch `test.foo` (unknown extension). It renders with generic `file-outline` in gray.
- [ ] `.md` and `.editorconfig` files render with the Text icon.
- [ ] Reopen a second, different solution — all icons re-render correctly on the new nodes; no stale bindings from prior solution.
- [ ] Row height comparable to pre-change; no jarring taller rows.
- [ ] No exceptions in the Output pane while browsing the tree.

## Learnings

### Architectural decisions
- **Open Decision #1 (palette values)**: tuned live with user during visual pass. Final values in `Models/FileIconPalette.cs` — not the ticket's starter values. Notably: user asked for a uniform blue (`#569CD6`) across all four `ProjectKind` slots so every csproj-level row reads the same at a glance; per-kind distinction is carried by the glyph, not the color.
- **Open Decision #2 (icon size)**: kept default 16px. Row height unchanged from baseline.
- **Open Decision #3 (unknown fallback)**: `file-outline` in gray, per ticket default.
- **Open Decision #4 (cache glyph/color)**: kept on-demand getters on `TreeNode` (`IconGlyph` / `IconColor` computed via `FileIconMap.From(this)`). No perf issue observed at the tree sizes MiniIde targets.
- **Open Decision #5 (csproj glyph)**: `microsoft-visual-studio` per default. User then requested a distinct magenta `#B026AD` so `.csproj` no longer visually shares the C# purple family; C# files ended up green (`#29A03F`).
- **Library glyph change**: `library` MDI glyph draws Greek building columns (real library building) which read wrong for "code library". Switched `ProjectLib` to `file-link` per user request.

### Problems encountered
- **TTF internal family name mismatch with ticket**: ticket said the font's internal family name is `Material Design Icons`. Actual name in the pinned 6.8.96 Desktop TTF is `Material Design Icons Desktop`. Verified via `System.Windows.Media.Fonts.GetFontFamilies(uri)` (WPF). `App.axaml` `FontFamily` URI updated to match. If the wrong suffix ships, Avalonia silently falls back to the system font and glyphs render as tofu — non-obvious failure mode.
- **No `meta.json` in `Templarian/MaterialDesign-Font` repo** (that lives in the sibling `MaterialDesign-Webfont` repo). The Desktop-Font repo does ship `cheatsheet.html`, which embeds all `name` / `hex` / `version` triples inline in a JS data blob — grep with a regex like `name:"<name>"[^}]*?hex:"([0-9A-F]+)"` to extract codepoints and `version:"X.Y.Z"` for the pinned release (max value = 6.8.96 as of pin).
- **App holds `MiniIde.exe` between rebuilds**: repeated `scripts/run.ps1` while the app is still open fails with MSB3021 (file in use). Use `scripts/stop.ps1` before re-running when iterating on visuals.

### Interesting tidbits
- **License**: Pictogrammers Free License. Fonts distributed under Apache 2.0 (icon set is a mix — some redistributed under their own licenses, rest Apache 2.0). Copy the repo's `LICENSE` verbatim rather than shipping a bare `Apache-2.0` file.
- `\U000FXXXX` (uppercase `\U`, 8 hex digits) is the only way to encode MDI codepoints in C# — the compiler expands it to a surrogate pair automatically; runtime string length is 2 chars per glyph, which Avalonia's `TextBlock.Text` handles transparently.
- MDI version pinned: **6.8.96**. Documented in `Assets/icons/README.md` and comment atop `Models/FileIcon.cs`.
- Extension whitelist in `SolutionService.BuildFolder` promoted from an `is or or or` expression to a `HashSet<string>` field (`IncludedExtensions`, `OrdinalIgnoreCase`). The list crossed the readability threshold once audio/image/video categories were folded in.

### Related areas affected
- `TreeNode` gained two computed properties (`IconGlyph`, `IconColor`) and a nullable `ProjectKind`. Compiled-binding template in `MainWindow.axaml` reads them directly — no converter, no attached property; the ticket flagged this constraint and it held.
- `SolutionService.LoadAsync` now sets `ProjectKind` on project tree nodes (previously only carried on `ProjectEntry`).

### Rejected alternatives
- **`library` glyph for `ProjectLib`** (ticket default). Rejected during visual pass — glyph reads as a public-library building, not a code library. `file-link` chosen.
- **Per-kind project palette** (ticket starter values had exe blue / lib gray / web teal / tst green). Rejected during visual pass — user preferred uniform csproj color to reduce visual noise at the project-list level.
- **`palette` glyph for `.ico`** (tried as an "icon painter" nod). Rejected — `palette` reads more like "a palette file format" than "an icon file". `.ico` stayed grouped with other raster image extensions.
- **`language-markdown` glyph for `.md`** (tried per user request). Rejected during visual pass — glyph too fiddly at 16px to read clearly against the tree background. `.md` stayed on `file-document`.

