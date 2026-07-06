# Remove Navigate Menu, Surface Shortcuts in Code Context Menu

## Context
**Current behavior**: The top menu bar has a `_Navigate` menu with two items — "Go to _Definition (F12)" and "Find _References (Shift+F12)" — that invoke `GoToDefinitionAsync()` / `FindRefsAsync()` against the current caret. These duplicate the F12 / Shift+F12 keyboard shortcuts (handled globally in `OnGlobalKeyDown`) and, once the code-view context menu ships, the "Go to definition" / "Find usages" items on that menu.

**New behavior**: The `_Navigate` menu is gone; those two actions live only on the code-editor context menu and the keyboard. The context menu's "Find usages" and "Go to definition" items display their keyboard shortcuts (Shift+F12 / F12) as a gesture hint, so removing the menu loses no shortcut discoverability. `_Navigate` is now the **only** remaining top-menu item (the `_File` menu was removed by `solution-context-menu-actions`), so deleting it empties the `<Menu>` — this ticket must also remove the now-empty `<Menu Grid.Row="0">` element itself.

## Prerequisites
- `docs/tickets/code-view-context-menu.md` — must be implemented first. This ticket edits the three-item context menu it adds to the `ae:TextEditor` (Search solution / Find usages / Go to definition).

## Scope
### In scope
- Delete the `_Navigate` `MenuItem` (both children) from the top `Menu` in `Views/MainWindow.axaml`, then remove the now-empty `<Menu Grid.Row="0">` element itself (`_File` already gone).
- Delete the now-unused `OnGoToDefClick` / `OnFindRefsClick` click handlers in `Views/MainWindow.axaml.cs`.
- Add shortcut-gesture hints to the code context menu's Find usages (Shift+F12) and Go to definition (F12) items.

### Out of scope
- Changing the F12 / Shift+F12 / Ctrl+Shift+F keyboard handling in `OnGlobalKeyDown` — unchanged.
- Removing or renaming `GoToDefinitionAsync()` / `FindRefsAsync()` — still called by the keyboard handlers and the context menu.
- Any context menus (tree, tab-header, solution-name) — untouched. (The `_File` menu no longer exists; `solution-context-menu-actions` removed it.)

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — the top `<Menu Grid.Row="0">` block; the `_Navigate` `MenuItem` (headers `Go to _Definition (F12)` / `Find _References (Shift+F12)`). The code-editor `ContextMenu` on the `ae:TextEditor` in the `EditorTabViewModel` `DataTemplate` (added by the prerequisite ticket) — where the gesture hints go.
  - `Views/MainWindow.axaml.cs` — `OnGoToDefClick` / `OnFindRefsClick` (the wrappers to delete); `OnGlobalKeyDown` (the F12 / Shift+F12 branches that must remain); `GoToDefinitionAsync()` / `FindRefsAsync()` (keep).
- **Related tickets**:
  - `docs/tickets/code-view-context-menu.md` — defines the context menu being edited here.

## Constraints & Gotchas
- After deleting `_Navigate` the top `<Menu>` has no children — remove the `<Menu Grid.Row="0">` element entirely (Grid rows are `Auto`, so the empty row collapses harmlessly, but the dead element should go).
- The gesture hint is display-only. F12 / Shift+F12 are already handled in `OnGlobalKeyDown`; do **not** add a `HotKey`/`KeyBinding` that would double-fire or steal the key from the global handler. If using `MenuItem.InputGesture`, confirm it renders the text without also registering an accelerator in this context; if it does register one, prefer the inline-header-text approach (Open Decision #2) instead.
- Verify `OnGoToDefClick` / `OnFindRefsClick` have no other references before deleting (grep — current usage is only the `_Navigate` XAML).

## Open Decisions
1. **Search item shortcut hint** — also hint the "Search solution" item with its Ctrl+Shift+F gesture, or hint only the two symbol items that came from the Navigate menu. Default: hint Search too, for consistency, since it has a global shortcut. Skip if the interpolated header (`Search solution for "Foo"`) leaves no clean room for a gesture.
2. **Gesture display mechanism** — `MenuItem.InputGesture="F12"` (right-aligned gesture column, idiomatic Avalonia) vs. inline header text like the old Navigate menu (`Go to definition (F12)`). Default: `InputGesture` if it renders cleanly and display-only per Constraints; otherwise inline header text.

## Acceptance Criteria
- [ ] There is no `_Navigate` menu, and the top `<Menu>` element is gone entirely (no empty menu bar; `_File` was already removed).
- [ ] `OnGoToDefClick` and `OnFindRefsClick` no longer exist in `Views/MainWindow.axaml.cs`, and the project builds.
- [ ] The code-editor context menu's "Go to definition" item visibly shows the F12 shortcut, and "Find usages" shows Shift+F12.
- [ ] F12, Shift+F12, and Ctrl+Shift+F still perform Go to Definition, Find References, and Focus Find respectively (unchanged), with no double-firing from the added hints.
- [ ] The context menu items still perform Go to definition / Find usages when clicked (behavior from the prerequisite ticket intact).

## Implementation

### 1. Remove the Navigate menu
In `Views/MainWindow.axaml`, delete the entire `<MenuItem Header="_Navigate">…</MenuItem>` block (both child items) from the top `Menu`. `_Navigate` is the only child left (`_File` already removed by `solution-context-menu-actions`), so also delete the enclosing empty `<Menu Grid.Row="0">…</Menu>` element.

### 2. Delete the dead click handlers
In `Views/MainWindow.axaml.cs`, remove `OnGoToDefClick` and `OnFindRefsClick` (the one-line wrappers). Keep `GoToDefinitionAsync()` / `FindRefsAsync()` — they remain called by `OnGlobalKeyDown` and the context menu handlers.

### 3. Add gesture hints to the context menu items
On the code-editor `ContextMenu` (in the `EditorTabViewModel` `DataTemplate` in `Views/MainWindow.axaml`), give the "Go to definition" and "Find usages" `MenuItem`s their shortcut hints (F12 / Shift+F12) per Open Decision #2 — display-only, no accelerator registration (Constraints). Optionally hint Search with Ctrl+Shift+F per Open Decision #1.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`, open a C# file.
- [ ] There is no `Navigate` menu and no menu bar at all (the whole `<Menu>` is gone; `File` was already removed).
- [ ] Right-click an identifier in the editor — the context menu shows "Go to definition" with F12 and "Find usages" with Shift+F12 (and Search with Ctrl+Shift+F if Decision #1 taken). Clicking each still performs its action.
- [ ] Press F12 on an identifier — navigates to definition; Shift+F12 — populates Find references; Ctrl+Shift+F — focuses Find. None double-fire or throw.

## Learnings

### Open Decisions resolved
- **#1 (hint Search)**: Took the default — hinted `Search solution` with `Ctrl+Shift+F`. Avalonia's `InputGesture` renders in its own right-aligned column, independent of the header text, so the dynamically-interpolated header (`Search solution for "Foo"`, set in `OnCodeCtxOpening`) coexists with the gesture hint without layout conflict.
- **#2 (gesture mechanism)**: Took the default — `MenuItem.InputGesture`. Confirmed display-only: in Avalonia `InputGesture` only populates the gesture text block; `HotKey` is what registers a `KeyBinding`/accelerator. So the hints cannot double-fire against the global `OnGlobalKeyDown` handler, which stays the sole owner of F12 / Shift+F12 / Ctrl+Shift+F. No inline-header fallback needed.

### Architectural notes
- Deleting `_Navigate` emptied the top `<Menu>`, so the whole `<Menu Grid.Row="0">` element was removed. The `Grid RowDefinitions="Auto,Auto,*,Auto"` was left unchanged — the now-unused first `Auto` row collapses to zero height. Elements kept their original `Grid.Row` indices (Border stays `Grid.Row="1"`), so no re-indexing was needed.

### Verification limits
- Build compiles the AXAML, which validates that `KeyGesture.Parse` accepts `"F12"`, `"Shift+F12"`, `"Ctrl+Shift+F"`. The visual rendering of the gesture column and the click/keyboard behavior require a running GUI + right-click, which can't be exercised headlessly — left for manual confirmation per the Test Plan.
