# Startup Dropdown: Classify + Format Projects by Kind

## Context
**Current behavior**: Startup dropdown shows absolute csproj paths from `SolutionService.ProjectPaths`. Any project selectable. Play always runs `dotnet run --project <path>` — fails on test projects, meaningless on libraries. NuGet tab's project dropdown shows same raw paths.

**New behavior**: Startup dropdown items render `"{kind} {filename-no-ext}"` where kind ∈ `exe`, `lib`, `web`, `tst`. Libraries stay selectable but Play acts on kind: `dotnet run` for exe/web, `dotnet test` for tst, no-op status for lib. NuGet dropdown gets same display. Default `StartupProject` picks first non-lib entry when available.

## Scope
### In scope
- New `ProjectEntry` model + csproj classification in `SolutionService`.
- Startup ComboBox display via templated item.
- Play command branches on kind.
- NuGet tab project dropdown adopts same entry type + display.

### Out of scope
- Disabling library items in ComboBox (user opted to leave selectable; Play just no-ops).
- Handling filename collisions across projects.
- Passing extra args to `dotnet test` (filters, loggers, etc).
- Detecting AOT/WASM/other SDK variants beyond the four kinds.

## Relevant Docs & Anchors
- **Code anchors**:
  - `SolutionService.LoadAsync` (`src/MiniIde/Services/SolutionService.cs`) — where project list is built.
  - `MainWindowViewModel.OpenSolutionAsync`, `PlayAsync`, `StartupProject` property (`src/MiniIde/ViewModels/MainWindowViewModel.cs`).
  - `RunService.RunAsync` (`src/MiniIde/Services/RunService.cs`) — dispatch site.
  - Startup ComboBox markup + NuGet project ComboBox in `src/MiniIde/Views/MainWindow.axaml`.
  - `NuGetViewModel.SetProjects`, `Projects`, `SelectedProject`, `OnSelectedProjectChanged` (`src/MiniIde/ViewModels/NuGetViewModel.cs`).
- **Stack notes**: `docs/stack.md`, `docs/quickstart.md`.

## Constraints & Gotchas
- csproj parse: read as XML directly, no MSBuild eval. Ignore imports; only `PropertyGroup` children + `ItemGroup/PackageReference` needed.
- SDK attribute lives on root `<Project Sdk="...">`. Web SDK = `Microsoft.NET.Sdk.Web` → treat as `exe`-defaulted OutputType.
- Test detection: presence of `<PackageReference Include="Microsoft.NET.Test.Sdk" ... />` (any version). `<IsTestProject>true</IsTestProject>` also valid but rarely used alone — accept either, PackageReference wins if both present.
- Classification precedence: **Tst > Web > Exe > Lib**. Test SDK reference beats OutputType.
- `dotnet test` writes to stdout/stderr the same way — existing `RunService` log plumbing works unchanged.
- `ProjectPaths` public surface on `SolutionService` renamed to `Projects` — update all call sites (`MainWindowViewModel.OpenSolutionAsync`, `NuGetViewModel.SetProjects`).

## Open Decisions
1. **Classifier location** — inline in `SolutionService.LoadAsync` vs. new `ProjectClassifier` static helper. Default: static helper in `Services/ProjectClassifier.cs`, keeps `SolutionService` focused on tree building.
2. **Status text for lib Play** — `"Cannot run library"` vs. `"Nothing to run"` vs. silent. Default: `"Cannot run library project"`.
3. **`ProjectKind` casing in display** — always lowercase (`exe`) matches user spec verbatim. Default: lowercase.

## Acceptance Criteria
- [ ] `Models/ProjectEntry.cs` exists exposing `Path`, `Display`, `Kind`.
- [ ] `ProjectKind` enum has exactly `Exe`, `Lib`, `Web`, `Tst`.
- [ ] For each solution project, classification follows precedence Tst > Web > Exe > Lib as defined in Constraints.
- [ ] `Display` equals `"{kind} {Path.GetFileNameWithoutExtension(csproj)}"` with kind lowercased (e.g., `exe MiniIde`).
- [ ] Startup ComboBox renders `Display` (no absolute path visible).
- [ ] NuGet tab's project ComboBox renders `Display` for the same entries.
- [ ] Default `StartupProject` after `OpenSolutionAsync` = first entry whose `Kind != Lib`, else first entry, else null.
- [ ] Play command on kind Exe/Web invokes `dotnet run --project <path>`.
- [ ] Play command on kind Tst invokes `dotnet test --project <path>`.
- [ ] Play command on kind Lib does not spawn a process; sets status per Open Decision #2 default.
- [ ] `SolutionService.ProjectPaths` no longer exists; replaced by `Projects: IReadOnlyList<ProjectEntry>`.

## Implementation

### 1. Add `ProjectKind` + `ProjectEntry`
Create `src/MiniIde/Models/ProjectKind.cs` with enum `Exe, Lib, Web, Tst`. Create `src/MiniIde/Models/ProjectEntry.cs` — record with `string Path`, `string Display`, `ProjectKind Kind`. Immutable; built once at solution load.

### 2. Classify csproj
Add `src/MiniIde/Services/ProjectClassifier.cs`. Single static method `Classify(string csprojPath) -> ProjectKind`. Load csproj via `XDocument.Load`. Read root `Sdk` attribute + first `<OutputType>` element (case-insensitive) + any `<PackageReference Include="Microsoft.NET.Test.Sdk">`. Apply precedence: test SDK ref → Tst; else Sdk == `Microsoft.NET.Sdk.Web` → Web; else OutputType `Exe`/`WinExe` → Exe; else Lib. Missing OutputType with non-web SDK = Lib.

### 3. Build `ProjectEntry` list in `SolutionService`
Rename `ProjectPaths` property to `Projects: IReadOnlyList<ProjectEntry>`. In `LoadAsync`, replace the `projPaths` list with a `List<ProjectEntry>`. For each `SolutionProject`, resolve absolute path (existing logic), call `ProjectClassifier.Classify`, format Display as `$"{kind.ToString().ToLowerInvariant()} {Path.GetFileNameWithoutExtension(p.FilePath)}"`, append entry. Tree-node construction unchanged.

### 4. Migrate `MainWindowViewModel`
Replace `ObservableCollection<string> ProjectPaths` with `ObservableCollection<ProjectEntry> Projects`. Change `StartupProject` from `string?` to `ProjectEntry?`. In `OpenSolutionAsync`, populate `Projects` from `Solution.Projects`, then set `StartupProject` per Acceptance default rule (first non-Lib, else first, else null). Update `NuGetVm.SetProjects` call to pass the new list.

### 5. Play dispatch by kind
Refactor `RunService.RunAsync` signature to accept `ProjectEntry entry` (or `string csprojPath, ProjectKind kind` — implementer's call). For Tst: swap `run` for `test` in `ArgumentList`. Rest of process wiring unchanged. `MainWindowViewModel.PlayAsync`: if `StartupProject is null` → existing "No startup project" status; if `Kind == Lib` → set lib-noop status (Open Decision #2), return without spawning; else call `RunService.RunAsync(StartupProject, Output.Append)`. Status prefix per kind: `"Running X..."` for Exe/Web, `"Testing X..."` for Tst.

### 6. Migrate `NuGetViewModel`
Change `ObservableCollection<string> Projects` to `ObservableCollection<ProjectEntry> Projects`. Change `SelectedProject` to `ProjectEntry?`. `SetProjects` param type → `IEnumerable<ProjectEntry>`. In `OnSelectedProjectChanged`, use `value.Path` for `NuGetService.ReadReferences`. `ApplyAsync` uses `SelectedPackage.ProjectPath` already (unchanged) — but the re-select at end passes `SelectedProject` which is now an entry; wire correctly.

### 7. Update XAML
`Views/MainWindow.axaml` Startup ComboBox: bind `ItemsSource="{Binding Projects}"`, `SelectedItem="{Binding StartupProject}"`, add `DisplayMemberBinding` or inline `ItemTemplate` rendering `{Binding Display}`. Same for NuGet tab's project ComboBox — find it by the `SelectedProject`/`Projects` binding pair on `NuGetViewModel`.

## Test Plan
- [ ] Build succeeds: `dotnet build src/MiniIde/MiniIde.csproj`.
- [ ] Launch app (`scripts/run.ps1`); open a solution containing at least one exe, one lib, one test project.
- [ ] Confirm Startup dropdown items render `exe Foo`, `lib Bar`, `tst Baz` (no paths).
- [ ] Confirm default selection is the first non-lib entry.
- [ ] Select the exe entry, click Play — process runs, output streams into Output panel, Stop kills it.
- [ ] Select the test entry, click Play — status shows `Testing ...`, `dotnet test` output streams in.
- [ ] Select the lib entry, click Play — no process spawned; status shows lib-noop message.
- [ ] Open NuGet tab — project dropdown shows same `{kind} {name}` display; selecting a project loads its PackageReferences as before.
- [ ] Load a solution containing a web project (Sdk `Microsoft.NET.Sdk.Web`) with no explicit `<OutputType>` — confirm it classifies as `web` and Play runs it.

## Learnings

### Architectural decisions
- **Classifier as static helper** (Open Decision #1). `Services/ProjectClassifier.cs`, one method `Classify(path)`. Keeps `SolutionService.LoadAsync` narrow; testable in isolation without solution model. Default chosen.
- **Lib Play status** (Open Decision #2): `"Cannot run library project"`. Distinct from empty-startup `"No startup project"`.
- **Display casing** (Open Decision #3): lowercase per spec — `kind.ToString().ToLowerInvariant()`.
- **RunService kind-aware**: takes `ProjectEntry`, switches verb `run`/`test`. Path + streaming unchanged.
- **StartupProject picker** hoisted to private `PickDefaultStartup` — kept `OpenSolutionAsync` scannable.

### Problems encountered
- LSP diagnostics flooded during edits ("System namespace not found") — false; `dotnet build` clean. LSP state stale between rapid writes; ignore, trust build.
- csproj XML: descendant scan (not root children only) — properties/refs can sit in nested `PropertyGroup`/`ItemGroup`. Namespace-agnostic (`LocalName`) since SDK-style has no default xmlns but tolerate anyway.

### Interesting tidbits
- csproj parse via `XDocument.Load` bypasses MSBuild eval entirely — no imports, no props, no SDK resolution. Sufficient for the 4-way classification.
- `Microsoft.NET.Test.Sdk` `PackageReference` is authoritative test signal; `<IsTestProject>` accepted as fallback.
- `partial void OnSelectedProjectChanged` generator hook still works with reference-type change (`string?` → `ProjectEntry?`) — signature just tracks property type.

### Related areas affected
- `RunService.RunAsync` signature changed — only caller is `MainWindowViewModel.PlayAsync`.
- `NuGetViewModel.SetProjects` param type changed — only caller is `MainWindowViewModel.OpenSolutionAsync`.
- XAML: two ComboBox/ListBox bindings retemplated to render `Display`.

### Rejected alternatives
- Inline classification in `SolutionService.LoadAsync` — rejected; muddies solution-tree code with csproj parsing.
- Disabling lib items in ComboBox — out of scope per ticket; Play no-ops instead.
- Passing separate `(string path, ProjectKind kind)` to `RunService.RunAsync` — rejected; `ProjectEntry` already carries both.

