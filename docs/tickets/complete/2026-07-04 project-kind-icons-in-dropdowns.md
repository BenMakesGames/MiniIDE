# Project-Kind Icons in Startup Dropdown + NuGet Project List

## Context
**Current behavior**: Startup ComboBox and NuGet tab's project ListBox render `ProjectEntry.Display`, formatted as `"{kind} {name}"` (e.g. `exe MiniIde`, `lib Foo`, `tst Bar`). Kind conveyed as lowercase text prefix.

**New behavior**: Both surfaces render `[icon] {name}` — same icon glyph + brush the solution tree already uses for project nodes. Text prefix removed. `ProjectEntry.Display` renamed to `Name` and holds bare project name (no kind prefix).

## Prerequisites
- `docs/tickets/complete/2026-07-04 file-type-icons.md` — establishes `FileIcon`, `FileIconPalette`, `FileIconMap`, `IconFont` resource in `App.axaml`.

## Scope
### In scope
- Rename `ProjectEntry.Display` → `Name`; drop kind prefix at construction site.
- Expose project-kind icon dispatch as public API on `FileIconMap` (currently private).
- Retemplate two XAML surfaces to `[icon] {name}`: startup ComboBox item template + NuGet tab project ListBox item template.

### Out of scope
- Palette differentiation per kind. All 4 `FileIconPalette.ProjectXxx` slots stay uniform `#569CD6` (as set in file-type-icons ticket). Glyph shape carries the distinction.
- `ProjectKind.Tst` → `Test` rename. Cosmetic; not surfaced in UI after this change. Spin off separately if desired.
- Collapsing the 4 identical `ProjectXxx` palette entries into a single const. Keep them as differentiation seams for future per-kind theming.
- Editor tab headers, output panel, status bar. Only the two listed surfaces change.
- `ProjectKind` enum, `RunService`, `PickDefaultStartup`, `PlayAsync` — behavior logic keyed on `.Kind` stays untouched.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Models/ProjectEntry.cs` — `record ProjectEntry(string Path, string Display, ProjectKind Kind)`.
  - `Services/SolutionService.LoadAsync` — where `Display` string is built from `kind.ToString().ToLowerInvariant()`.
  - `Models/FileIcon.cs` — `FileIconMap.From(TreeNode)` public dispatcher; `FromProjectKind(ProjectKind)` private helper to expose.
  - `Views/MainWindow.axaml` — startup `ComboBox` item template (near `Binding StartupProject`) + NuGet `ListBox` item template (inside `TabItem Header="NuGet"`, bound to `NuGetVm.Projects`).
  - `ViewModels/MainWindowViewModel.Projects` and `NuGetViewModel.Projects` — consumers of `ProjectEntry`; no logic change, only field-rename fallout.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 file-type-icons.md` — established icon plumbing; tree template is the exemplar to mirror.
  - `docs/tickets/complete/2026-07-04 startup-dropdown-project-kinds.md` — introduced `Display` field and its `"{kind} {name}"` format. This ticket unwinds that format.

## Constraints & Gotchas
- **`FileIconMap.FromProjectKind` currently private**. Promote to `public static (string Glyph, IBrush Color) FromProjectKind(ProjectKind kind)` so XAML compiled bindings can reach the glyph/color without threading through `TreeNode`.
- **Compiled bindings need real properties**. `DataType="m:ProjectEntry"` templates cannot call a static method inline. Add computed `IconGlyph` / `IconColor` on `ProjectEntry` (mirroring the pattern on `TreeNode`) so the template binds to properties, not method calls.
- **`ProjectEntry` is a `record`**. Renaming `Display` → `Name` changes the positional signature. Update the `new ProjectEntry(abs, name, kind)` call site in `SolutionService.LoadAsync`. Grep for any other constructor usage.
- **NuGet ListBox column width**. Middle grid column is `*`-sized; adding a ~22px icon fits without layout tweaks. No column change needed.
- **Icon size**. Use 16px to match the tree template. Row height should stay comparable.
- **Font resource**. `IconFont` is already an application-level static resource (from file-type-icons ticket). Both templates reference `{StaticResource IconFont}` the same way the tree template does.

## Open Decisions
1. **Location of computed `IconGlyph` / `IconColor` on `ProjectEntry`** — inline computed properties on the record vs. a small extension method vs. a value converter. Default: computed properties on the record (mirrors `TreeNode` pattern; simplest for compiled bindings). Implementer's call.

## Acceptance Criteria
- [ ] `ProjectEntry` positional record renamed: `Display` → `Name`. `Name` holds the bare filename-without-extension (no kind prefix).
- [ ] `SolutionService.LoadAsync` no longer concatenates `kind.ToString().ToLowerInvariant()` into the entry's name field.
- [ ] `FileIconMap.FromProjectKind(ProjectKind)` is `public static` and returns `(string Glyph, IBrush Color)`.
- [ ] `ProjectEntry` exposes non-null `IconGlyph` (string) and `IconColor` (`IBrush`) usable by compiled bindings, derived from `FileIconMap.FromProjectKind(Kind)`.
- [ ] Startup ComboBox item template renders an icon `TextBlock` (using `IconFont`) followed by the name `TextBlock`, both vertically centered with a small horizontal gap.
- [ ] NuGet tab project ListBox item template renders identically to startup ComboBox: icon + name.
- [ ] Neither surface displays the strings `exe`, `lib`, `web`, `tst` as prefixes.
- [ ] `MainWindowViewModel`, `NuGetViewModel`, and any other consumer of `ProjectEntry` compiles unchanged apart from the field rename.

## Implementation

### 1. Rename `ProjectEntry.Display` → `Name`
`Models/ProjectEntry.cs`: update positional record parameter. `record ProjectEntry(string Path, string Name, ProjectKind Kind)`. Grep for `.Display` on `ProjectEntry` — none expected outside the two XAML templates and the constructor.

### 2. Drop kind prefix at construction
`Services/SolutionService.LoadAsync`: replace `var display = $"{kind.ToString().ToLowerInvariant()} {name}";` with using `name` directly. `new ProjectEntry(abs, name, kind)`. `name` local already exists (`Path.GetFileNameWithoutExtension(p.FilePath)`).

### 3. Promote `FromProjectKind` to public
`Models/FileIcon.cs`: change `private static (string, IBrush) FromProjectKind(ProjectKind kind)` to `public static (string Glyph, IBrush Color) FromProjectKind(ProjectKind kind)`. Existing default-branch fallback stays. `From(TreeNode)` continues delegating to it — no behavior change.

### 4. Add icon properties on `ProjectEntry`
`Models/ProjectEntry.cs`: extend the record with computed properties mirroring `TreeNode.IconGlyph` / `IconColor`. Add `using Avalonia.Media;`. Getter body: `FileIconMap.FromProjectKind(Kind).Glyph` and `.Color`. Non-null for all four kinds.

### 5. Update startup ComboBox template
`Views/MainWindow.axaml`: the `ComboBox` bound to `Projects` / `StartupProject`. Replace the single `TextBlock Text="{Binding Display}"` inside `DataTemplate DataType="m:ProjectEntry"` with a `StackPanel Orientation="Horizontal"` mirroring the tree `TreeDataTemplate`: leading icon `TextBlock` (`FontFamily="{StaticResource IconFont}"`, `FontSize="16"`, `Text="{Binding IconGlyph}"`, `Foreground="{Binding IconColor}"`, `Margin="0,0,6,0"`, `VerticalAlignment="Center"`) followed by name `TextBlock` (`Text="{Binding Name}"`, `VerticalAlignment="Center"`).

### 6. Update NuGet project ListBox template
`Views/MainWindow.axaml`: inside `TabItem Header="NuGet"`, the `ListBox` whose `DataTemplate DataType="m:ProjectEntry"` currently renders `Binding Display`. Apply the same `StackPanel [icon, name]` shape as step 5. Templates should be visually identical.

### 7. Sweep for `Display` fallout
Grep `\bDisplay\b` under `src/MiniIde/` — expect zero remaining references tied to `ProjectEntry`. Update any compile errors surfaced by the rename (bindings, LINQ projections, tests if any). None expected outside the two axaml templates already retemplated.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`. Open `MiniIde.slnx`.
- [ ] Startup ComboBox shows `[icon] MiniIde` — icon matches the tree's project icon for the exe kind, no `exe ` prefix.
- [ ] Open NuGet tab. Project list shows `[icon] MiniIde` with the same glyph + color as the ComboBox.
- [ ] Open a multi-project solution containing exe + lib + tst (+ web if available). Each entry shows its per-kind glyph in the uniform project blue; no text prefix anywhere.
- [ ] ComboBox width unchanged; NuGet project column layout unchanged.
- [ ] Default startup selection still lands on first non-lib entry (`PickDefaultStartup` unaffected).
- [ ] Play still works for exe/web (runs), tst (tests), lib (status "Cannot run library project", no process spawned).
- [ ] Selecting a project in NuGet tab still loads its `PackageReference` list.
- [ ] No exceptions in Output panel during dropdown open / NuGet browse.

## Learnings

### Architectural decisions
- Open Decision 1 resolved: computed `IconGlyph` / `IconColor` inline on the record. Matches `TreeNode` pattern; compiled bindings resolve without extra converter machinery. Rejected alternatives — extension methods (would still need a wrapping property for `x:CompiledBindings`), value converters (extra XAML noise for a static dispatch).
- `FromProjectKind` kept as a small standalone dispatch on `FileIconMap` rather than hanging it off `ProjectEntry` directly. Keeps icon knowledge centralized so future palette-per-kind changes only touch `FileIcon.cs` — record just delegates.

### Scope grew mid-implementation
- User requested (after core acceptance criteria passed): disable non-runnable (`Lib`) entries in the startup ComboBox, and disable Play + Stop when a non-runnable project is selected. Handled in-line rather than spinning a follow-up ticket because it fell naturally out of the same UI surface.
- Added `ProjectEntry.IsRunnable => Kind != ProjectKind.Lib`. Single source of truth for "can this project run" — reused by ComboBox item style and by `PlayCommand` / `StopCommand` `CanExecute`.
- Play/Stop wired via `[RelayCommand(CanExecute = nameof(CanPlay))]` + `partial void OnStartupProjectChanged` calling `NotifyCanExecuteChanged()` on both. Removed the now-redundant `if (Kind == Lib)` guard inside `PlayAsync` — button can't fire when disabled.
- ComboBox disable done via scoped `<Style Selector="ComboBoxItem">` inside `<ComboBox.Styles>`, binding `IsEnabled` to `IsRunnable` (typed via `x:DataType="m:ProjectEntry"` on the Style — required for compiled bindings against item DataContext).

### Interesting tidbits
- Avalonia disabled visual state on `ComboBoxItem` did *not* automatically dim the icon `TextBlock` because its `Foreground` was bound to an explicit `IBrush` (`IconColor`), which overrides the theme's disabled foreground brush. Fix: a second scoped style `Selector="ComboBoxItem:disabled" → Opacity=0.4` on the whole item. Cleaner than per-child opacity bindings.
- `[RelayCommand]` source-generated commands expose `NotifyCanExecuteChanged()`; wiring is one partial method on the observable property.

### Problems encountered
- `dotnet build` fails with `MSB3021` when the exe is already running (file lock on `bin/.../MiniIde.exe`). Not a code issue — just close the running instance. Happens every debug cycle; scripts/run.ps1 doesn't auto-kill prior instance.

### Related areas affected
- None outside the two dropdown surfaces + `MainWindowViewModel` command wiring. `RunService`, `PickDefaultStartup`, tree view template all untouched.

### Rejected alternatives
- Filtering `Projects` collection to hide Lib entries entirely — rejected. User explicitly wanted Lib entries visible-but-disabled so the user can see they exist.
- Per-element `Opacity` binding on icon TextBlock via a `IsRunnable → double` converter — rejected. The `ComboBoxItem:disabled` style dims both icon and name uniformly with no converter overhead.

