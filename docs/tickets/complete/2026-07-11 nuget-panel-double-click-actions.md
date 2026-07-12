# NuGet Panel Double-Click Actions

## Context
**Current behavior**: The NuGet tab's three ListBoxes (Projects, Packages, Versions) support single-click selection only. Opening the underlying `.csproj`, seeing package metadata, or switching to a version all require multi-step interactions (open the file from the explorer, browse to nuget.org in a browser, or select-version + click Apply).

**New behavior**: Each ListBox gains a double-click action:
1. Double-clicking a **project** opens its `.csproj` in a new editor tab (existing `OpenFileAsync` dedup + `.csproj → XML` highlight).
2. Double-clicking a **package** opens (or refreshes) a read-only text tab showing metadata for the currently-installed version — id, authors, description, project URL, license, tags, published date, dependencies per TFM.
3. Double-clicking a **version** invokes the existing Apply flow (rewrite `<PackageReference>` version, run `dotnet restore`) — same effect as select-version + click Apply, no confirmation dialog.

## Prerequisites
- None. Builds on the existing NuGet tab, `OpenFileAsync` dedup, `OutputTabViewModel` shell, and the codebase-wide tunnel-`PointerPressed` + `ClickCount == 2` double-click pattern.

## Scope
### In scope
- `Views/MainWindow.axaml` — no markup changes required (handlers attach imperatively from code-behind, matching the `SolutionTree` pattern).
- `Views/MainWindow.axaml.cs` — hook tunnel `PointerPressed` on the three NuGet ListBoxes once realized (they live inside a non-selected `TabItem` at ctor time — attach on first realization, mirroring `OnProblemsTreeAttached`).
- `Services/NuGetService.cs` — add `GetMetadataAsync(packageId, version, ct)` returning package metadata plus a small formatter.
- `ViewModels/NuGetViewModel.cs` — optionally expose a small helper to fetch + format metadata; wire from the view is also acceptable (see Open Decisions).

### Out of scope
- Rendering markdown / rich text in the metadata tab. Plain text only.
- Fetching / embedding package README from the nupkg. `IPackageSearchMetadata.ReadmeUrl` may be surfaced as a link line but is not fetched.
- Gating `ApplyCommand` on `IsBusy`. Pre-existing Apply-button behavior is unchanged; double-click inherits it verbatim.
- Confirmation dialogs for version-switch double-click.
- Adding a new tab kind. The metadata tab reuses `OutputTabViewModel`.
- Refreshing an open metadata tab automatically after Apply from elsewhere — the tab only refreshes when the user re-double-clicks the package.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml.cs` — `OnTreePointerPressed` (canonical tunnel `PointerPressed` + `ClickCount == 2` shape) and `OnProblemsTreeAttached` (once-guard attach for a control inside a non-realized `TabItem`).
  - `Views/MainWindow.axaml.cs` — `MainWindow` constructor's `SolutionTree.AddHandler(..., RoutingStrategies.Tunnel)` calls.
  - `ViewModels/MainWindowViewModel.OpenFileAsync` — dedups by `FileId(path)`; opens or re-activates.
  - `ViewModels/MainWindowViewModel.GetOrCreateOutputTab` — dedups by `TabId`; reuse for the metadata tab.
  - `ViewModels/NuGetViewModel.ApplyAsync` (has `[RelayCommand]` → `ApplyCommand` property) — the exact flow version-double-click invokes.
  - `ViewModels/OutputTabViewModel` — read-only, no `FilePath`, streamable — the shell for the metadata tab.
  - `Services/NuGetService` — has `_repo` (`SourceRepository` for `nuget.org`), `_cache`, `_log`. Reuse them for the new metadata call.
  - `Models/PackageEntry` — `record PackageEntry(string ProjectPath, string Id, string CurrentVersion)`. `CurrentVersion` is the "installed" version for #2.
  - `Models/ProjectEntry` — `Path` is the `.csproj` full path for #1.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 treeview-double-click-row-deadzone.md` — establishes the tunnel `PointerPressed` + `ClickCount == 2` pattern and *why* `DoubleTapped` is unusable.
  - `docs/tickets/complete/2026-07-05 open-solution-file-on-name-doubleclick.md` — the `OpenFileAsync` dedup contract.
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — output tab shell reuse.
  - `docs/tickets/complete/2026-07-10 problems-panel.md` — the `AttachedToVisualTree` once-guard pattern for controls inside a non-realized `TabItem`.
- **Local docs**:
  - `docs/avalonia.md` — `DoubleTapped` gotcha, tunnel-`PointerPressed`, "control inside a non-selected `TabItem`" attach pattern.
  - `docs/nuget.md` — approach for NuGet integration (`NuGet.Protocol` for feed queries).

## Constraints & Gotchas
- **Use tunnel `PointerPressed` + `ClickCount == 2`, not `DoubleTapped`.** `DoubleTapped` drops presses near row edges / right of text because both presses must resolve to the same source element. Do **not** set `e.Handled` — selection must still commit on the same press (first press selects, second press has `ClickCount == 2` and triggers the action).
- **The NuGet `TabItem` is not realized at window-ctor time** — attaching handlers directly in the `MainWindow` constructor via `FindControl<ListBox>(...)` will silently miss. Mirror `OnProblemsTreeAttached`: give each of the three ListBoxes an `x:Name` and an `AttachedToVisualTree="..."` handler with a `bool` once-guard. Alternative: single `AttachedToVisualTree` on any one ListBox, hook all three there (they realize together with the tab).
- **Selection resolves via the ListBox's `SelectedItem`.** First press commits selection; the tunnel handler on the second press reads the committed selection. Same reasoning as `OnTreePointerPressed`.
- **`NuGetService._repo`, `_cache`, `_log` are instance fields** — the existing `GetVersionsAsync` is instance, `ReadReferences`/`SetVersion`/`RestoreAsync` are static (they don't hit the feed). The new metadata call needs the feed — make it instance.
- **`PackageMetadataResource.GetMetadataAsync(PackageIdentity, ...)` (not the `string id` overload)** returns a single `IPackageSearchMetadata?` matching the installed version. If the feed returns null (private-feed package, or a version pulled from the feed later), report via `Status` and do not open an empty tab.
- **Always-refresh on double-click.** The metadata tab is dedup'd by `nuget-meta:{id-lower}`, so re-double-clicking after an Apply reactivates the same tab — but the *content* is stale. On every double-click: `Clear()` then `Append(formatted)`. `OutputTabViewModel.Append` line-caps at 5000 lines (unchanged); one metadata blob is far under that.
- **Don't cache metadata in the VM.** A stale-content-vs-cache-hit tradeoff isn't worth it here; the on-demand fetch runs in the background, and the user's next double-click is the natural refresh trigger.
- **`ApplyCommand.ExecuteAsync(null)` is `async`.** The version-double-click handler is `async void` (event-handler idiom, matches the rest of the file) and awaits it. `IsBusy` is not gated at the Apply-button level today — the double-click may reentrance-fire, matching current behavior.
- **`NuGetViewModel.ApplyAsync` needs `SelectedVersion` set** — the first press of a version-list double-click sets `SelectedVersion` via the `ListBox` binding, which fires before the second press's handler. Do not manually re-assign `SelectedVersion` in the handler; the binding has already done it.
- **Package IDs are lowercased for the `TabId`** — matches nuget.org normalization (`nuget-meta:serilog`, not `nuget-meta:Serilog`). Header keeps original casing.
- **Metadata fetch runs on a background thread via `NuGet.Protocol`; formatter runs on the fetch's continuation.** Marshal the tab `Clear()` + `Append(...)` back to the UI thread (`OutputTabViewModel` already `Dispatcher.UIThread.Post`s internally — no extra work needed).

## Open Decisions
1. **Handler wiring depth** — thin view handler that calls `Vm.OpenFileAsync` / `Vm.NuGetVm.ApplyCommand` / a new `Vm.NuGetVm.ShowMetadataAsync(package)` VM method, vs. inline view code that constructs and appends the tab directly. Default: give #2 a `NuGetViewModel.OpenMetadataAsync(package)` (or similar) that takes a `Func<OutputTabViewModel>` for tab acquisition (mirroring the existing `_resolveOutput` field) — keeps the VM the owner of the fetch and tab identity. #1 and #3 stay as thin view handlers (they call existing VM surfaces).
2. **Attach pattern** — `AttachedToVisualTree` on each ListBox with its own once-guard, vs. a single once-guard driven by whichever realizes first. Default: single once-guard, all three attached together (they share a realization moment).
3. **Formatter location** — inside `NuGetService` alongside the fetch, vs. a small helper on the VM. Default: inside `NuGetService` (`private static string FormatMetadata(IPackageSearchMetadata)`) so the fetch + formatting live together and the VM just receives a string.
4. **Field selection for the metadata blob** — which of Description / Summary, DependencySets (all TFMs vs. truncated), LicenseMetadata vs. LicenseUrl to include. Default: Id + installed Version, Authors, Description (fall back to Summary if Description empty), ProjectUrl, License (prefer `LicenseMetadata.License` string, else `LicenseUrl`), Tags, Published (if non-null), then a "Dependencies" section grouped by TFM with each dep on one line as `id (version-range)`. Truncate nothing; if a package has 30 TFMs the blob is long — fine.

## Acceptance Criteria
- [ ] Double-clicking a row in the NuGet **Projects** ListBox opens the project's `.csproj` in an editor tab. If the tab already exists it reactivates (no duplicate). `.csproj` renders with XML highlighting (existing mapping).
- [ ] Double-clicking a row in the NuGet **Packages** ListBox opens (or reactivates) a read-only tab with `TabId == "nuget-meta:{packageId-lowercased}"` and header `"{packageId} — NuGet"` (or equivalent). The tab's content is the formatted metadata blob for the package's *installed* version.
- [ ] Re-double-clicking a package after any content change (e.g., after Apply changed the version) shows refreshed content in the same tab — the handler clears and re-appends on every double-click.
- [ ] Double-clicking a row in the NuGet **Versions** ListBox invokes the existing Apply flow — the `.csproj`'s `<PackageReference Version="..."/>` for the selected package updates to the double-clicked version and `dotnet restore` runs. Status bar reports outcome identically to clicking the Apply button.
- [ ] With no project selected, the Packages/Versions ListBoxes are empty and no double-click can fire (existing UI state).
- [ ] Double-click actions fire reliably at row top edge, bottom edge, and the empty area to the right of the text — same reliability as the solution `TreeView` after the row-deadzone fix (mechanism is the same: tunnel `PointerPressed` + `ClickCount == 2`).
- [ ] No `DoubleTapped` handler is introduced anywhere in the NuGet panel.
- [ ] Single-click selection on each of the three ListBoxes still works (selection commit is not suppressed by the double-click handlers).
- [ ] A metadata fetch that returns null / errors (missing on nuget.org, network failure) reports to `NuGetViewModel.Status` (which flows to the panel's status TextBlock) and does **not** create an empty tab.

## Implementation

### 1. Name the three NuGet ListBoxes and give one an `AttachedToVisualTree` hook
`Views/MainWindow.axaml`, inside the NuGet `TabItem`: add `x:Name` to each of the three `ListBox`es (Projects / Packages / Versions). On the one that realizes first (any of them — they attach together), add `AttachedToVisualTree="OnNuGetListsAttached"`. Mirrors `ProblemsTree`'s `AttachedToVisualTree="OnProblemsTreeAttached"` shape.

### 2. Attach three tunnel `PointerPressed` handlers on first realization
`Views/MainWindow.axaml.cs`: add `private bool _nugetListsHooked;` and `OnNuGetListsAttached(object? sender, VisualTreeAttachmentEventArgs e)`. Body: once-guarded — resolve the three ListBoxes via `this.FindControl<ListBox>(...)` on their `x:Name`s (they are in the window's namescope now — hand-authored `TabItem`, not `ItemsSource`), then `AddHandler(PointerPressedEvent, ..., RoutingStrategies.Tunnel)` on each with its own handler. Mirror `MainWindow` ctor's `SolutionTree.AddHandler(...)` call site for shape.

### 3. Handler for Projects list — open `.csproj`
`Views/MainWindow.axaml.cs`: `OnNuGetProjectsPointerPressed`. Guard `e.ClickCount != 2` returns. Resolve `ProjectEntry` from `((ListBox)sender).SelectedItem`. Call `await Vm.OpenFileAsync(entry.Path)`. Do not set `e.Handled`. Mirror `OnTreePointerPressed`'s shape.

### 4. Handler for Packages list — surface metadata tab
`Views/MainWindow.axaml.cs`: `OnNuGetPackagesPointerPressed`. Guard `e.ClickCount != 2`. Resolve `PackageEntry`. Call `await Vm.NuGetVm.OpenMetadataAsync(entry)` (added in step 6). Do not set `e.Handled`.

### 5. Handler for Versions list — invoke Apply
`Views/MainWindow.axaml.cs`: `OnNuGetVersionsPointerPressed`. Guard `e.ClickCount != 2`. First press already set `NuGetVm.SelectedVersion` via the two-way binding — do not re-assign. Invoke `await Vm.NuGetVm.ApplyCommand.ExecuteAsync(null)`. Do not set `e.Handled`. `ApplyAsync` already no-ops if `SelectedPackage` or `SelectedVersion` is null.

### 6. Extend `NuGetService` with a metadata fetch and formatter
`Services/NuGetService.cs`: add
- an instance `GetMetadataAsync(string packageId, string version, CancellationToken ct)` that resolves a `PackageMetadataResource` from `_repo`, calls `GetMetadataAsync(new PackageIdentity(packageId, NuGetVersion.Parse(version)), includePrerelease: true, includeUnlisted: true, _cache, _log, ct)`, and returns the `IPackageSearchMetadata?` (nullable — feed miss);
- a private static `FormatMetadata(IPackageSearchMetadata md)` returning a plain-text blob per Open Decision 4's default. Idiom: `StringBuilder`, one `AppendLine` per line, headers as `"Section"` on their own line with indented body lines. Truncate no fields.

The version string in `PackageEntry.CurrentVersion` may include `-*` floating markers or MSBuild property placeholders — if `NuGetVersion.Parse` throws, surface the error via the caller's `Status` path. Do not silently fall back to "latest."

### 7. Add `NuGetViewModel.OpenMetadataAsync(PackageEntry package)`
`ViewModels/NuGetViewModel.cs`: mirror `_resolveOutput`'s shape — add a second constructor delegate `Func<string tabId, string header, OutputTabViewModel>` (call it `_resolveMetadataTab`) that gets-or-creates a tab keyed by an arbitrary `tabId`/`header`. Then `OpenMetadataAsync` does:
1. `IsBusy = true`, `Status = $"Loading {package.Id}..."`.
2. Fetch via `await _svc.GetMetadataAsync(package.Id, package.CurrentVersion, ct)`. On null, set `Status = "No metadata found for {id} {version}"` and return without opening a tab.
3. Format the result (`NuGetService.FormatMetadata(md)`).
4. Acquire the tab: `var tab = _resolveMetadataTab($"nuget-meta:{package.Id.ToLowerInvariant()}", $"{package.Id} — NuGet");`.
5. `tab.Clear();` then `tab.Append(formatted);` — always-refresh.
6. `Status = "";` (or `$"{package.Id} metadata loaded"` — implementer's call).
7. `finally { IsBusy = false; }`. Wrap the whole body in a `try/catch (Exception ex) { Status = ex.Message; }` for network/parse failures.

### 8. Wire the new VM delegate in `MainWindowViewModel`
`ViewModels/MainWindowViewModel.cs`: in the `NuGetVm` construction site, pass a second lambda `(tabId, header) => GetOrCreateOutputTabAndActivate(tabId, header)` (either add a small helper that mirrors `ResolveNuGetOutput`'s "get-or-create + activate" pattern, or inline `GetOrCreateOutputTab(...)` + `ActiveTab = tab; return tab;` at the lambda site). The activation matches the existing NuGet-Output tab surfacing behavior — double-clicking a package should bring the metadata tab to the front.

### 9. (Optional) `Cursor="Hand"` on the three ListBox rows
Defer to Open Decision territory; no default requires it. Skip unless manual testing shows the ListBoxes feel un-clickable.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`. Switch to the NuGet bottom tab.
- [ ] **Projects list**: double-click any project row — a new editor tab opens for that project's `.csproj`, XML-highlighted. Double-click the same row again — same tab reactivates, no duplicate.
- [ ] **Packages list**: select a project, then double-click a package. A read-only tab opens with header `"{id} — NuGet"` and text content showing Id, installed Version, Authors, Description, ProjectUrl, License, Tags, Published, Dependencies (grouped by TFM). Bring the tab to front automatically on double-click.
- [ ] Double-click the same package again — same tab reactivates and content re-populates (verify via a small visible change: after step next, do Apply-to-new-version then re-double-click; installed-version field should reflect the new version).
- [ ] **Versions list**: with a package selected and versions loaded, double-click a version. The `.csproj`'s `<PackageReference>` version updates and `dotnet restore` runs (`NuGet - Output` tab activates and shows restore output — existing behavior). Status bar reports "Restored" or `Restore failed (N)`.
- [ ] Re-select the package after the double-click — the "installed" column in the Packages list reflects the new version (this exercises `OnSelectedProjectChanged`'s re-read of the csproj, invoked at the tail of `ApplyAsync`).
- [ ] Edge reliability: double-click each list at the very top edge, bottom edge, and empty area to the right of the text — every action still fires.
- [ ] Single-click selection continues to work on all three ListBoxes.
- [ ] Failure paths:
  - Double-click a package whose `CurrentVersion` is not on nuget.org (e.g., edit a `.csproj` to reference a private/synthetic version like `9.9.9-fake`, reload the solution, then double-click) — status bar reports "No metadata found ..."; no empty tab appears.
  - Simulate no network (disconnect / block `api.nuget.org`) then double-click a package — status bar shows the exception message; no empty tab.
- [ ] No `DoubleTapped=` attribute exists anywhere in the NuGet panel markup (regression guard against reintroducing the deadzone bug).
- [ ] Regression: `TreeView` in the Solution / Problems panels still supports double-click open/expand (untouched).

## Learnings

### Architectural decisions
- **Open Decision 1 (handler wiring depth)** — resolved to the default: added a `NuGetViewModel.OpenMetadataAsync(PackageEntry)` that takes a second `Func<string, string, OutputTabViewModel>` delegate. Keeps tab identity/activation with the VM and mirrors the shape of the existing `_resolveOutput`. #1 and #3 stayed thin view handlers (`OpenFileAsync` / `ApplyCommand.ExecuteAsync`).
- **Open Decision 2 (attach pattern)** — resolved to the default: single once-guard (`_nugetListsHooked`), all three ListBoxes attached in one `OnNuGetListsAttached` fire. All three realize together with the NuGet `TabItem`, so a per-list guard would only add ceremony.
- **Open Decision 3 (formatter location)** — resolved to the default: `NuGetService.FormatMetadata(IPackageSearchMetadata)` static. Fetch + formatting live together; VM gets a plain string.
- **Open Decision 4 (field selection)** — resolved to the default. Blob format: `Id`, `Version`, `Authors`, indented `Description` (or fallback `Summary`) block, `Project URL`, `License` (prefer `LicenseMetadata.License`, fallback `LicenseUrl`), `Tags`, `Published` (yyyy-MM-dd), `Readme URL` (surfaced but not fetched — was called out as out-of-scope), then a `Dependencies` section grouped by TFM. No truncation.

### Problems encountered
- **`PackageMetadataResource.GetMetadataAsync` overload mismatch** — the ticket named a `PackageIdentity` overload with `includePrerelease` + `includeUnlisted` bools, but that shape belongs to the `string id` overload only. The `PackageIdentity` overload is `(PackageIdentity, SourceCacheContext, ILogger, CancellationToken)` — no bool flags (identity already pins version, so unlisted/prerelease filters are moot). Compiler caught it with `CS1503: cannot convert from PackageIdentity to string`. Ticket's constraint about `includeUnlisted: true` for unlisted installed versions is still satisfied — the identity-pinned call resolves unlisted versions by default.

### Interesting tidbits
- `_resolveOutput` predates this ticket and the shape composed cleanly — a second delegate slot on `NuGetViewModel` for the metadata tab needed no re-architecture. Both delegates return `OutputTabViewModel`; the difference is a fixed tab id vs. caller-supplied id. Reusing `OutputTabViewModel` for a plain-text metadata blob avoided introducing a new tab kind.
- `IPackageSearchMetadata.DependencySets` may return an empty package list on a per-TFM entry (a TFM that pulls in nothing from the package). Rendered as `(none)` under the TFM heading — a bare TFM line with nothing under it reads as unfinished.

### Related areas affected
- `MainWindowViewModel.ResolveNuGetMetadataTab(tabId, header)` — new sibling of `ResolveNuGetOutput`. Same "get-or-create + activate" contract. If the "NuGet - Output" tab ever gains persistence rules, the metadata tabs will want the same treatment.

### Rejected alternatives
- **Caching metadata in the VM** — considered and rejected in the ticket, held to in implementation. A cache would risk stale content after an out-of-band Apply, and the fetch is cheap enough (nuget.org caches aggressively via `SourceCacheContext`).
- **Adding a bespoke `PackageMetadataTabViewModel`** — the ticket says reuse `OutputTabViewModel`; done. A dedicated VM would only pay off if the metadata tab ever gains structured (non-text) content — no such requirement today.

### Verification gap
- Manual UI test-plan items (double-click at row edges, refresh-after-Apply, network failure) were not driven in this session — build is clean but interactive verification is on the user.
