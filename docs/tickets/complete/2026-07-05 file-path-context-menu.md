# File Path Context Menu (Tree + Tab Header)

## Context
**Current behavior**: Right-clicking a `TreeView` item (project, folder, file) or an editor tab header produces no context menu. Users have no in-app way to reveal a file in Explorer or copy its path.

**New behavior**: Right-click on any solution-tree node (file / folder / project) or on any editor tab header opens a context menu with three items — **Open in Explorer**, **Copy absolute path**, **Copy path relative to solution root**. Files and projects (`.csproj`) reveal via `explorer.exe /select,`; folders open the folder directly.

## Prerequisites
None.

## Scope
### In scope
- Single reusable `ContextMenu` template embedded in the `TreeDataTemplate` root panel and in the tab-header `StackPanel` inside `TabControl.ItemTemplate` (`Views/MainWindow.axaml`).
- Three `MenuItem`s with `Click` handlers in `Views/MainWindow.axaml.cs` that resolve the target path from the invoking `MenuItem.DataContext` (`TreeNode` for tree, `EditorTabViewModel` for tabs).
- Helper on the `MainWindow` code-behind that computes solution-relative paths via `Path.GetRelativePath` using `Vm.Solution.SolutionPath`'s parent directory as the anchor.
- Explorer launch via `Process.Start("explorer.exe", …)`.
- Clipboard write via `TopLevel.GetTopLevel(this).Clipboard!.SetTextAsync(...)`.

### Out of scope
- Menu on the `NuGet` project list, the Find results list, or any other list-like control.
- Additional actions (rename, delete, reveal in terminal, copy filename-only, etc.). Follow-up tickets if wanted.
- Multi-select. Menu operates on the single item the user right-clicked.
- Cross-platform Explorer equivalent (`open` on macOS, `xdg-open` on Linux). App is Windows-only per current `RuntimeIdentifier` (`win-x64`).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — `TreeDataTemplate DataType="m:TreeNode"` (`StackPanel` holding icon + name); `TabControl.ItemTemplate DataType="vm:EditorTabViewModel"` (`StackPanel` holding header text + close button).
  - `Views/MainWindow.axaml.cs` — existing `Click` handlers (`OnCloseTabClick`, `OnOpenSolutionClick`) are the pattern to mirror; `Vm` accessor.
  - `Models/SolutionNode.cs` — `TreeNode.Path` (nullable string), `TreeNode.Kind` (`NodeKind` enum).
  - `ViewModels/EditorTabViewModel.cs` — `FilePath` (always absolute; set from tree open or find navigation).
  - `Services/SolutionService.cs` — `SolutionPath` property (absolute path to loaded `.slnx`/`.sln`); parent directory is the solution root anchor.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 file-type-icons.md` — established the tree template shape (icon + name in `StackPanel`); context menu attaches to that same panel.

## Constraints & Gotchas
- **Clipboard requires a `TopLevel`.** Use `TopLevel.GetTopLevel(this)` from inside `MainWindow`. `Clipboard` is nullable on `TopLevel`; the `!` is fine here because the menu is only reachable from a rendered `MainWindow`.
- **`ContextMenu` `DataContext` propagation.** Menus embedded in the template inherit the item's `DataContext` (`TreeNode` or `EditorTabViewModel`). Each `MenuItem`'s `DataContext` inside the `ContextMenu` will therefore be that same object. Handlers cast `((MenuItem)sender).DataContext` — do not use `PlacementTarget` gymnastics.
- **Tab-header menu attach point.** The `ContextMenu` inside the tab `ItemTemplate` `StackPanel` attaches to the header content, not the surrounding `TabItem` container. Right-click on the header label works; right-click on the strip's empty margin will not fire. Acceptable; documented for expectations.
- **Solution-relative fallback.** If `Vm.Solution.SolutionPath` is null (theoretically — no code path opens a tab pre-solution today), the relative-copy handler falls back silently to the absolute path. Do not throw, do not disable the menu item.
- **Paths outside the solution root** produce `..\` relative paths from `Path.GetRelativePath`. This is expected — do not sanitize or reject.
- **Explorer args.** `explorer.exe /select,<path>` is the documented reveal-and-select form; note the comma (not a space) after `/select`. `Process.Start` with `ProcessStartInfo` and `ArgumentList` is safer than a raw string — set `FileName = "explorer.exe"` and add args individually.
- **Project nodes have a `Path` set to their `.csproj`.** Treat `NodeKind.Project` the same as `NodeKind.File` for all three actions (see Acceptance Criteria).
- **`System.Diagnostics.Process` not yet imported in `MainWindow.axaml.cs`.** Add `using System.Diagnostics;`. `RunService` already uses it project-wide, so no new dependency.

## Open Decisions
1. **Extract a helper class or keep handlers inline in `MainWindow.axaml.cs`.** Three short handlers reusing a small `GetTargetPath(object dataContext)` helper is enough. Default: inline. Extract only if a second consumer surfaces.
2. **Menu icons.** Could reuse the `IconFont` glyph style (folder-open, content-copy, content-copy-outline). Default: no icons — text only, matches the existing top `Menu` items which are text-only.
3. **Access key underscores** (e.g., `_Open in Explorer`). Default: match top-menu convention — add them (`_Open in Explorer`, `Copy _absolute path`, `Copy _relative path`).
4. **Status bar feedback after copy.** Could set `Vm.Status = "Copied <path>"`. Default: yes — matches how other actions surface completion via the status bar; cheap and discoverable.

## Acceptance Criteria
- [ ] Right-clicking a tree item of any `NodeKind` (`File`, `Folder`, `Project`) opens a `ContextMenu` with exactly three items in order: Open in Explorer, Copy absolute path, Copy path relative to solution root.
- [ ] Right-clicking an editor tab header opens the same three-item `ContextMenu`.
- [ ] **Open in Explorer** on a file or a project (`.csproj`) node launches `explorer.exe` with `/select,<absolutePath>` so Explorer opens the parent directory with the target pre-selected.
- [ ] **Open in Explorer** on a folder node launches `explorer.exe <absoluteFolderPath>` and opens that folder directly (no selection).
- [ ] **Open in Explorer** from a tab header behaves identically to the file case (reveal-and-select).
- [ ] **Copy absolute path** places the target's full absolute path on the clipboard (native Windows separators).
- [ ] **Copy path relative to solution root** places `Path.GetRelativePath(solutionRootDir, targetAbsolutePath)` on the clipboard, where `solutionRootDir` is `Path.GetDirectoryName(Vm.Solution.SolutionPath)`. If `SolutionPath` is null, the absolute path is copied instead (silent fallback).
- [ ] Neither copy action throws when the clipboard is momentarily unavailable — failure surfaces as a status-bar message, not an unhandled exception.
- [ ] Loading a solution rooted at e.g. `D:\Development\CoolProject\CoolProject.slnx` and copying the relative path of `D:\Development\CoolProject\src\Foo\Bar.cs` yields `src\Foo\Bar.cs` (no leading separator, no `.\` prefix).

## Implementation

### 1. Add `using` directives
`Views/MainWindow.axaml.cs`: add `using System.Diagnostics;`. `Avalonia.Controls`, `Avalonia.Interactivity`, `MiniIde.Models`, `MiniIde.ViewModels` are already imported.

### 2. Define the context menu in XAML
`Views/MainWindow.axaml`. Because both the tree template and the tab-header template need the same menu, embed a `ContextMenu` inside each `StackPanel`. Alternative — a shared `ContextMenu` styled resource — is overkill for three menu items.

Two attach points:

- **Tree** — inside `TreeView.ItemTemplate` → `TreeDataTemplate DataType="m:TreeNode"` → the `StackPanel` currently holding the icon `TextBlock` + name `TextBlock`. Add:
  ```xml
  <StackPanel.ContextMenu>
      <ContextMenu>
          <MenuItem Header="_Open in Explorer" Click="OnCtxOpenInExplorerClick"/>
          <MenuItem Header="Copy _absolute path" Click="OnCtxCopyAbsolutePathClick"/>
          <MenuItem Header="Copy _relative path" Click="OnCtxCopyRelativePathClick"/>
      </ContextMenu>
  </StackPanel.ContextMenu>
  ```

- **Tabs** — inside `TabControl.ItemTemplate` → `DataTemplate DataType="vm:EditorTabViewModel"` → the `StackPanel` holding the header `TextBlock` + close `Button`. Add the identical `StackPanel.ContextMenu` block.

Each `MenuItem`'s `DataContext` inherits the item's `DataContext` (`TreeNode` for tree, `EditorTabViewModel` for tabs). Handlers key off that.

### 3. Handlers in code-behind
`Views/MainWindow.axaml.cs`. Three handlers plus one shared resolver:

- `private static string? GetTargetPath(object? dataContext)`:
  - `TreeNode { Path: not null } tn` → return `tn.Path`.
  - `EditorTabViewModel tab` → return `tab.FilePath`.
  - else → `null`.

- `private async void OnCtxOpenInExplorerClick(object? sender, RoutedEventArgs e)`:
  - Resolve target via `GetTargetPath((sender as MenuItem)?.DataContext)`; bail on null.
  - Determine mode: folder vs. reveal-and-select. If the invoking `DataContext` is a `TreeNode` with `Kind == NodeKind.Folder`, launch `explorer.exe <path>`. Otherwise (file, project, tab), launch `explorer.exe /select,<path>`.
  - Use `Process.Start` with a `ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true }` and `ArgumentList.Add(...)`. Wrap in try/catch and surface failures via `Vm.Status`.
  - `async void` retained for parity with other event handlers; body needs no `await` today. If you don't await inside, drop the `async`.

- `private async void OnCtxCopyAbsolutePathClick(object? sender, RoutedEventArgs e)`:
  - Resolve target; bail on null.
  - `await TopLevel.GetTopLevel(this)!.Clipboard!.SetTextAsync(targetPath)`.
  - Set `Vm.Status = $"Copied {targetPath}"` on success (per Open Decision #4 default).
  - Try/catch around the clipboard call; on failure set `Vm.Status = $"Copy failed: {ex.Message}"`.

- `private async void OnCtxCopyRelativePathClick(object? sender, RoutedEventArgs e)`:
  - Resolve target; bail on null.
  - Compute `var slnPath = Vm.Solution.SolutionPath;`. If `slnPath` null → fall back to absolute (same body as the absolute handler).
  - Else `var rel = Path.GetRelativePath(Path.GetDirectoryName(slnPath)!, target);`.
  - Copy `rel` (or the absolute fallback) via the same clipboard call + status message pattern.

Keep the three handlers small — the resolver plus a shared "copy this string to clipboard, report status" helper is enough to keep them under ~10 lines each. Extraction pattern up to implementer (Open Decision #1).

### 4. Manual verification
Run via `scripts/run.ps1`. Open `MiniIde.slnx`. Right-click through:
- A `.cs` file in the tree — three items appear; each behaves per AC.
- A folder node (e.g. `Views`) — Explorer opens *into* the folder, no file selected.
- The `MiniIde` project node — Explorer opens with `MiniIde.csproj` pre-selected.
- A tab header — three items appear; behaves like file case.

Confirm the status bar reports the copied string. Paste into a plain text editor to verify contents and separators.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds; no new warnings.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`.
- [ ] Right-click a `.cs` file in the tree (e.g., `Program.cs`). Three menu items appear.
- [ ] Choose **Open in Explorer** on that `.cs` file — Explorer opens the containing folder with the file pre-selected.
- [ ] Choose **Copy absolute path** — clipboard contains a full path starting with `C:\Development\CSharpMini\...`. Status bar shows `Copied <path>`.
- [ ] Choose **Copy path relative to solution root** — clipboard contains a path starting `src\MiniIde\...` (no leading separator, no `.\`).
- [ ] Right-click the `MiniIde` project node. Choose **Open in Explorer** — Explorer opens with `MiniIde.csproj` pre-selected.
- [ ] Right-click a folder node (e.g., `Views`). Choose **Open in Explorer** — Explorer opens that folder directly (no selection).
- [ ] Right-click a folder. Copy relative path — clipboard contains e.g. `src\MiniIde\Views`.
- [ ] Open a file (double-click), then right-click its tab header. All three actions behave identically to the file-node case.
- [ ] Open a second solution located elsewhere on disk. Confirm relative paths recompute against the new solution root (regression check for stale anchor).
- [ ] Attempt each action on a file whose absolute path lies outside the solution root (drag one in via a symlink or open a file via the file picker if that ever wires up). Relative-path copy yields a `..\...` path without throwing. (Skip if no such path is reachable in current build.)
- [ ] No exceptions appear in the Output pane throughout.

## Learnings

### Architectural decisions
- **Inline handlers, one shared resolver + shared clipboard helper.** Followed Open Decision #1 default. Three handlers stayed short; `GetTargetPath(object?)` collapses both `TreeNode` and `EditorTabViewModel` cases; `CopyToClipboardAsync(string)` centralizes status/exception plumbing. No helper class extracted — YAGNI.
- **`OnCtxOpenInExplorerClick` is sync (not `async void`).** Ticket flagged this as optional. No `await` inside, so dropped the `async` — no compiler warning, no captured state machine.
- **Status bar reports copy success and failure** per Open Decision #4. `Vm.Status = $"Copied {text}"` on success; `$"Copy failed: {ex.Message}"` on exception; `"clipboard unavailable"` if `TopLevel.GetTopLevel(this)?.Clipboard` returns null. Ticket AC required no unhandled exception; the null branch also handles the design-time / detached-window edge case.
- **Explorer `/select,` and target passed as two separate `ArgumentList` entries.** Windows Explorer accepts both `/select,<path>` (glued) and `/select, <path>` (space between). Splitting is safer against paths containing spaces / commas because `ArgumentList` escapes each entry.

### Problems encountered
- **`IClipboard.SetTextAsync` is an extension method, not an instance member.** Ticket's example (`Clipboard!.SetTextAsync(...)`) compiles only with `using Avalonia.Input.Platform;` because `SetTextAsync` lives in `Avalonia.Input.Platform.ClipboardExtensions`. Initial build failed with CS1061 — added the using directive and it built clean.

### Interesting tidbits
- The Avalonia `ContextMenu` embedded inside a `DataTemplate` inherits the templated item's `DataContext`, so each `MenuItem`'s `DataContext` is that item verbatim. No `PlacementTarget` gymnastics required — `((MenuItem)sender).DataContext` is the `TreeNode` / `EditorTabViewModel`.

### Rejected alternatives
- **Shared `ContextMenu` as a `StaticResource`.** Cleaner in principle, but three menu items × two attach points is barely any duplication, and shared-menu instances make per-instance `DataContext` reasoning fussier. Kept two literal inline blocks.
- **Icons on menu items.** Open Decision #2 default — text-only matches the existing top `Menu` items. Would only make sense if we later restyle the whole app's menus.

### Related areas affected
- Future context-menu additions (rename, delete, reveal in terminal) can reuse the same `StackPanel.ContextMenu` shape. If more actions accumulate the shared-resource path may become worth revisiting.
- Any future non-Windows target must replace the `explorer.exe` launch (`open` on macOS, `xdg-open` on Linux). Currently gated by `RuntimeIdentifier` `win-x64`.
