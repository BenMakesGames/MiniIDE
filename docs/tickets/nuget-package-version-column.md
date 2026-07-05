# NuGet Package Version Column

## Context
**Current behavior**: NuGet tab's "Package (current)" `ListBox` renders each `PackageEntry` as a single `TextBlock` — `Id` + space + italic `CurrentVersion` inline.

**New behavior**: Version renders in its own left-aligned column beside the Id. No italic. No headers.

## Scope
### In scope
- `Views/MainWindow.axaml` — the `DataTemplate DataType="m:PackageEntry"` inside the NuGet tab's Packages `ListBox`.

### Out of scope
- `PackageEntry` model.
- Other panels or `ListBox` templates.
- Column headers above the list.
- Sort / filter / column-resize behavior.

## Relevant Docs & Anchors
- `Views/MainWindow.axaml` — NuGet `TabItem` → Packages `ListBox` → `ItemTemplate` (currently the two-`Run` `TextBlock`).
- `Models/PackageEntry.cs` — `record PackageEntry(string ProjectPath, string Id, string CurrentVersion)`.

## Acceptance Criteria
- [ ] `PackageEntry` `DataTemplate` uses a two-column layout (`Grid ColumnDefinitions="*,Auto"` or equivalent).
- [ ] Column 0 shows `Id`, left-aligned.
- [ ] Column 1 shows `CurrentVersion`, left-aligned, non-italic, with a small left margin separating it from `Id`.
- [ ] No column header row above the `ListBox`.
- [ ] Selecting a row still populates the Versions list (existing binding intact).

## Implementation

### 1. Replace `PackageEntry` template
`Views/MainWindow.axaml`, inside the NuGet `TabItem`'s Packages `ListBox`: replace the current single-`TextBlock` template (two `Run`s concatenating `Id` and `CurrentVersion`) with a `Grid ColumnDefinitions="*,Auto"` holding two `TextBlock`s — `Id` in column 0, `CurrentVersion` in column 1 with `Margin="8,0,0,0"` and no `FontStyle="Italic"`.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`. Open `MiniIde.slnx`.
- [ ] Open NuGet tab. Select a project with packages.
- [ ] Package rows show `Id` on the left and `CurrentVersion` as a separate column to its right, both non-italic.
- [ ] Versions column edge is consistent down the list (Auto-sizes to widest version).
- [ ] Selecting a package row still populates the Versions panel.
