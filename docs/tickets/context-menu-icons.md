# Context-Menu Icons for Solution/Project Actions

## Context
**Current behavior**: The solution-name top-bar context menu (`MainWindow.axaml`, the `TextBlock` bound to `SolutionName`) lists its items as text only — *Open new solution…*, *Reload solution*, *Open in Claude Code*, *Open in Explorer*, *Copy absolute path*, *Copy relative path*. No `MenuItem` anywhere in the app uses an icon; every existing icon (tree nodes, startup ComboBox, problems panel) is a Material Design Icons glyph rendered as a `TextBlock FontFamily="{StaticResource IconFont}"`.

**New behavior**: Three action items gain a leading MDI glyph icon. On the solution top-bar menu: *Reload solution* → green refresh (`refresh`), *Open in Claude Code* → blue robot (`robot`), *Open in Explorer* → yellow folder. For cross-menu consistency, the yellow folder icon is also added to the *Open in Explorer* item in the tree-node and tab-header context menus (the same command appears in three menus total). *Open new solution…* and the two *Copy path* items stay iconless.

## Prerequisites
- `docs/tickets/complete/2026-07-04 file-type-icons.md` — established the `IconFont` app resource, the MDI TTF asset (pinned 6.8.96), and the `TextBlock`-glyph rendering pattern this ticket reuses.

## Scope
### In scope
- New `Models/ActionIcon.cs` holding MDI glyph constants for the three action icons (robot / refresh / folder), so action glyphs stay out of the file-type-scoped `FileIcon`.
- `Views/MainWindow.axaml`: add `<MenuItem.Icon>` to *Reload solution*, *Open in Claude Code*, and all three *Open in Explorer* items.

### Out of scope
- Icons on *Open new solution…*, *Copy absolute path*, *Copy relative path* — explicitly left iconless this ticket.
- Icons on the code-editor context menu (`OnCodeCtxOpening`) — it has no Explorer/Reload/Claude item.
- Any new NuGet package, asset file, or icon-font change — the existing `IconFont` + MDI TTF cover all three glyphs.
- Refactoring the three duplicated `Open in Explorer` `ContextMenu` blocks into a shared resource — prior tickets deliberately kept them inline; not this ticket's concern.
- Runtime theming / user-configurable colors — hex values compile in, as with `FileIconPalette`.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Views/MainWindow.axaml` — the solution-name `TextBlock`'s `ContextMenu` (`Opening="OnSolutionCtxOpening"`): items *Reload solution* (`OnReloadSolutionClick`), *Open in Claude Code* (`OnCtxOpenWithClaudeClick`), *Open in Explorer* (`OnCtxOpenInExplorerClick`). Two further *Open in Explorer* items live in the tree-node `StackPanel.ContextMenu` (inside `TreeDataTemplate DataType="m:TreeNode"`) and the tab-header `StackPanel.ContextMenu`. Grep `_Open in Explorer` to confirm exactly three.
  - `App.axaml` — `<FontFamily x:Key="IconFont">` resource.
  - `Models/FileIcon.cs` — the glyph-constant style to mirror (`\U000FXXXX` encoding, MDI-name comment per constant, version note in header). **Do not add action glyphs here** — its header scopes it to file-type glyphs.
  - `Views/MainWindow.axaml` root element — `xmlns:m="using:MiniIde.Models"` is already declared, so `{x:Static m:ActionIcon.Xxx}` resolves without a new namespace.
  - Existing glyph-`TextBlock` exemplar: the tree-node icon `TextBlock` in the `TreeDataTemplate` (`FontFamily="{StaticResource IconFont}"`, `FontSize="16"`, `VerticalAlignment="Center"`).
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 project-kind-icons-in-dropdowns.md` — **read its Learnings**: an explicit `Foreground` `IBrush` overrides the theme's disabled-state dimming, which forced a `:disabled → Opacity` style on the startup ComboBox. Same hazard applies here (see Constraints).
  - `docs/tickets/complete/2026-07-05 solution-context-menu-actions.md` — introduced the *Open in Claude Code* / *Reload* items and `OnSolutionCtxOpening`.
  - `docs/tickets/complete/2026-07-04 file-type-icons.md` — MDI encoding gotchas (below-BMP codepoints, `\U000FXXXX`, codepoint drift, family-name suffix).

## Constraints & Gotchas
- **MDI codepoints are below-BMP.** Glyphs live at U+F0000+; C# needs `\U000FXXXX` (uppercase `\U`, 8 hex digits), not `\uXXXX`. Mirror `FileIcon.cs` exactly.
- **Verify codepoints against the pinned release.** Confirm each glyph's hex against MDI 6.8.96 (`cheatsheet.html` per `Assets/icons/README.md`) before trusting it — a wrong codepoint renders as a different glyph or tofu, silently. Expected MDI names: `robot`, `refresh`, and `folder` (or `folder-open`, see Open Decisions). The `folder` codepoint is already proven in `FileIcon.Folder`.
- **Disabled icons won't dim on their own.** `OnSolutionCtxOpening` sets `IsEnabled=false` on every solution-menu item except *Open new solution…* when no solution is loaded. A `TextBlock` with an explicit `Foreground` brush keeps its full color even while the item's text is greyed — the exact issue the project-kind-icons ticket hit. Decide per Open Decisions whether to dim (e.g. a scoped `MenuItem:disabled` opacity style) or accept bright-icon-on-greyed-text. The tree-node and tab *Open in Explorer* items are never disabled, so this only concerns the solution menu.
- **Icon gutter applies to the whole menu.** Once any item in a `ContextMenu` has an `Icon`, Avalonia reserves the icon column for all siblings, so the iconless items become blank-but-indented (aligned under the icons). This is expected; it is the intended look, not a regression.
- **`x:Static`, not binding.** These are static `MenuItem`s whose `DataContext` is the `MainWindowViewModel` (not a node), so the glyph must come via `{x:Static m:ActionIcon.Xxx}`, not a `{Binding}`.

## Open Decisions
1. **Explorer glyph** — `folder` (matches "folder", codepoint already proven in `FileIcon.Folder`) vs. `folder-open` (reads more like "open in…"). Default: `folder`.
2. **Icon colors** — starter hexes: robot blue `#569CD6`, refresh green `#3FB950`, folder yellow `#E8C547`. Tune in the visual pass as prior icon tickets did. Inline `Foreground` on each `TextBlock` vs. a small color set — implementer's call; inline is fine for three icons.
3. **Disabled-state dimming** on the solution menu — add a scoped `MenuItem:disabled` opacity treatment so the icons dim with the text, or accept bright icons on disabled items. Default: accept as-is unless it looks jarring in the visual pass, then add the opacity style.
4. **Constant naming** in `ActionIcon.cs` — e.g. `Reload` / `Claude` / `Explorer` vs. glyph-named `Refresh` / `Robot` / `Folder`. Default: action-named (`Reload`, `Claude`, `Explorer`) so the menu's intent reads at the use site.

## Acceptance Criteria
- [ ] `Models/ActionIcon.cs` exists with MDI glyph constants (encoded `\U000FXXXX`, one MDI-name comment each) for the refresh, robot, and folder action icons.
- [ ] On the solution top-bar context menu: *Reload solution* shows a green refresh glyph, *Open in Claude Code* shows a blue robot glyph, *Open in Explorer* shows a yellow folder glyph — each left of its text.
- [ ] All three *Open in Explorer* `MenuItem`s (solution menu, tree-node menu, tab-header menu) show the yellow folder icon; the glyph and color are identical across the three.
- [ ] *Open new solution…*, *Copy absolute path*, and *Copy relative path* have no icon glyph.
- [ ] Every icon uses `FontFamily="{StaticResource IconFont}"`; no new font, asset, or NuGet reference is added.
- [ ] The glyph value comes from `Models/ActionIcon.cs`; no action glyph is added to `Models/FileIcon.cs`.

## Implementation

### 1. Add `Models/ActionIcon.cs`
Create a static class mirroring `FileIcon.cs`'s shape — a header comment noting MDI 6.8.96 and the below-BMP encoding, then one `public const string` per action glyph with a trailing `// mdi-name` comment. Cover the three actions (refresh, robot, folder). Verify each codepoint against the pinned cheatsheet; reuse the known-good `folder` codepoint from `FileIcon.Folder` if the Explorer glyph stays `folder`.

### 2. Icon the solution-menu items
In `Views/MainWindow.axaml`, on the solution-name `TextBlock`'s `ContextMenu` (the one with `Opening="OnSolutionCtxOpening"`), add a `<MenuItem.Icon>` to *Reload solution*, *Open in Claude Code*, and *Open in Explorer*. Each icon is a `TextBlock` mirroring the tree-node icon exemplar — `FontFamily="{StaticResource IconFont}"`, `FontSize="16"`, `VerticalAlignment="Center"` — with `Text="{x:Static m:ActionIcon.Xxx}"` and an explicit `Foreground` (starter hexes per Open Decision 2). Leave *Open new solution…* and the two *Copy* items untouched.

### 3. Icon the tree-node and tab *Open in Explorer* items
Apply the same yellow-folder `<MenuItem.Icon>` to the *Open in Explorer* `MenuItem` in the tree-node `StackPanel.ContextMenu` and the tab-header `StackPanel.ContextMenu`. Keep the glyph/color identical to step 2's Explorer icon so the command looks the same wherever it appears.

### 4. Visual pass
Launch, open `MiniIde.slnx`, and tune the three hexes (Open Decision 2) against the `#1E1E22`-derived menu background for readability. Confirm alignment and row height read cleanly. If the solution menu's disabled bright-icon look is jarring at startup, add the scoped `MenuItem:disabled` opacity style (Open Decision 3).

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1`. Open `MiniIde.slnx`. Right-click the solution name — *Reload solution* shows a green refresh icon, *Open in Claude Code* a blue robot, *Open in Explorer* a yellow folder; the other three items are iconless (blank gutter, aligned).
- [ ] Each icon'd action still works: *Reload solution* reloads, *Open in Claude Code* opens a terminal in the solution dir, *Open in Explorer* reveals the `.slnx`.
- [ ] Right-click a tree node (project/folder/file) and a tab header — their *Open in Explorer* shows the same yellow folder icon and still reveals the target.
- [ ] With no solution loaded, right-click the `<no solution>` label — the menu opens; *Open new solution…* is enabled, the rest greyed. Confirm the disabled icons look acceptable (or are dimmed if Open Decision 3 was taken).
- [ ] No glyph renders as a blank box / tofu (would indicate a wrong codepoint or font-family suffix).
- [ ] No exceptions in the Output pane while opening the menus.
