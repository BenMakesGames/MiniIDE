# Remove Navigate Menu, Surface Shortcuts in Code Context Menu

## Context
**Current behavior**: The top menu bar has a `_Navigate` menu with two items — "Go to _Definition (F12)" and "Find _References (Shift+F12)" — that invoke `GoToDefinitionAsync()` / `FindRefsAsync()` against the current caret. These duplicate the F12 / Shift+F12 keyboard shortcuts (handled globally in `OnGlobalKeyDown`) and, once the code-view context menu ships, the "Go to definition" / "Find usages" items on that menu.

**New behavior**: The `_Navigate` menu is gone; those two actions live only on the code-editor context menu and the keyboard. The context menu's "Find usages" and "Go to definition" items display their keyboard shortcuts (Shift+F12 / F12) as a gesture hint, so removing the menu loses no shortcut discoverability. The menu bar retains only `_File`.

## Prerequisites
- `docs/tickets/code-view-context-menu.md` — must be implemented first. This ticket edits the three-item context menu it adds to the `ae:TextEditor` (Search solution / Find usages / Go to definition).

## Scope
### In scope
- Delete the `_Navigate` `MenuItem` (both children) from the top `Menu` in `Views/MainWindow.axaml`.
- Delete the now-unused `OnGoToDefClick` / `OnFindRefsClick` click handlers in `Views/MainWindow.axaml.cs`.
- Add shortcut-gesture hints to the code context menu's Find usages (Shift+F12) and Go to definition (F12) items.

### Out of scope
- Changing the F12 / Shift+F12 / Ctrl+Shift+F keyboard handling in `OnGlobalKeyDown` — unchanged.
- Removing or renaming `GoToDefinitionAsync()` / `FindRefsAsync()` — still called by the keyboard handlers and the context menu.
- The `_File` menu and any other context menus (tree, tab-header, solution-name).

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — the top `<Menu Grid.Row="0">` block; the `_Navigate` `MenuItem` (headers `Go to _Definition (F12)` / `Find _References (Shift+F12)`). The code-editor `ContextMenu` on the `ae:TextEditor` in the `EditorTabViewModel` `DataTemplate` (added by the prerequisite ticket) — where the gesture hints go.
  - `Views/MainWindow.axaml.cs` — `OnGoToDefClick` / `OnFindRefsClick` (the wrappers to delete); `OnGlobalKeyDown` (the F12 / Shift+F12 branches that must remain); `GoToDefinitionAsync()` / `FindRefsAsync()` (keep).
- **Related tickets**:
  - `docs/tickets/code-view-context-menu.md` — defines the context menu being edited here.

## Constraints & Gotchas
- After deletion the top `Menu` contains a single `_File` item — expected, not a bug.
- The gesture hint is display-only. F12 / Shift+F12 are already handled in `OnGlobalKeyDown`; do **not** add a `HotKey`/`KeyBinding` that would double-fire or steal the key from the global handler. If using `MenuItem.InputGesture`, confirm it renders the text without also registering an accelerator in this context; if it does register one, prefer the inline-header-text approach (Open Decision #2) instead.
- Verify `OnGoToDefClick` / `OnFindRefsClick` have no other references before deleting (grep — current usage is only the `_Navigate` XAML).

## Open Decisions
1. **Search item shortcut hint** — also hint the "Search solution" item with its Ctrl+Shift+F gesture, or hint only the two symbol items that came from the Navigate menu. Default: hint Search too, for consistency, since it has a global shortcut. Skip if the interpolated header (`Search solution for "Foo"`) leaves no clean room for a gesture.
2. **Gesture display mechanism** — `MenuItem.InputGesture="F12"` (right-aligned gesture column, idiomatic Avalonia) vs. inline header text like the old Navigate menu (`Go to definition (F12)`). Default: `InputGesture` if it renders cleanly and display-only per Constraints; otherwise inline header text.

## Acceptance Criteria
- [ ] The top menu bar shows only `_File`; there is no `_Navigate` menu.
- [ ] `OnGoToDefClick` and `OnFindRefsClick` no longer exist in `Views/MainWindow.axaml.cs`, and the project builds.
- [ ] The code-editor context menu's "Go to definition" item visibly shows the F12 shortcut, and "Find usages" shows Shift+F12.
- [ ] F12, Shift+F12, and Ctrl+Shift+F still perform Go to Definition, Find References, and Focus Find respectively (unchanged), with no double-firing from the added hints.
- [ ] The context menu items still perform Go to definition / Find usages when clicked (behavior from the prerequisite ticket intact).

## Implementation

### 1. Remove the Navigate menu
In `Views/MainWindow.axaml`, delete the entire `<MenuItem Header="_Navigate">…</MenuItem>` block (both child items) from the top `Menu`. Leave the `_File` menu as the sole entry.

### 2. Delete the dead click handlers
In `Views/MainWindow.axaml.cs`, remove `OnGoToDefClick` and `OnFindRefsClick` (the one-line wrappers). Keep `GoToDefinitionAsync()` / `FindRefsAsync()` — they remain called by `OnGlobalKeyDown` and the context menu handlers.

### 3. Add gesture hints to the context menu items
On the code-editor `ContextMenu` (in the `EditorTabViewModel` `DataTemplate` in `Views/MainWindow.axaml`), give the "Go to definition" and "Find usages" `MenuItem`s their shortcut hints (F12 / Shift+F12) per Open Decision #2 — display-only, no accelerator registration (Constraints). Optionally hint Search with Ctrl+Shift+F per Open Decision #1.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`, open a C# file.
- [ ] The menu bar shows only `File`; there is no `Navigate` menu.
- [ ] Right-click an identifier in the editor — the context menu shows "Go to definition" with F12 and "Find usages" with Shift+F12 (and Search with Ctrl+Shift+F if Decision #1 taken). Clicking each still performs its action.
- [ ] Press F12 on an identifier — navigates to definition; Shift+F12 — populates Find references; Ctrl+Shift+F — focuses Find. None double-fire or throw.
