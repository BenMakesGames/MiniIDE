# Tab Header Context Menu Enabled Only on the Second Right-Click

## Context
**Current behavior**: The first right-click on a file tab's header opens the context menu with *Open in Explorer* / *Copy absolute path* / *Copy relative path* all greyed out. Right-clicking the same tab again shows them correctly enabled. The items still *work* when clicked (the `Click` handlers resolve their own `DataContext`, which is correct by then) — only the enabled state is wrong, and only on the first open.

The cause is a timing assumption in `OnTabHeaderCtxOpening`, which reads `cm.DataContext` to decide whether the tab has a `FilePath`. Avalonia raises `ContextMenu.Opening` (via `CancelOpening()`) *before* `ContextMenu.Open()` runs — and `Open()` is where `_popup.SetPopupParent(control)` and `_popup.Child = this` happen. Until then the menu has no logical parent, so nothing has propagated a `DataContext` into it: the handler sees `null`, `GetTargetPath(null)` returns `null`, and every item is disabled. On the second open the popup is still parented from last time (`PopupClosed` only calls `SetPopupParent(null)` when the menu has no attached controls, and the tab header is still in the visual tree), so the inherited `DataContext` is present and the enablement is right. Each realized tab header owns its own `ContextMenu` instance, so the wrong-on-first-open behavior repeats for every newly opened tab, not once per app run.

**New behavior**: The tab-header context menu shows correct enablement the *first* time it is opened on any tab — path items enabled on file tabs, greyed on output tabs (which have no `FilePath`). Enablement becomes a declarative `IsEnabled` binding on the items, evaluated when the menu is parented and its `DataContext` arrives (inside `Open()`, before the popup is shown), and the `Opening` handler goes away. No change to which items are enabled, or to what they do when clicked.

## Prerequisites
None.

## Scope
### In scope
- The tab-header `ContextMenu` inside `TabControl.ItemTemplate` in `Views/MainWindow.axaml` — per-item `IsEnabled` gating on the bound tab's `FilePath`.
- `Views/MainWindow.axaml.cs` — remove the now-dead `OnTabHeaderCtxOpening` handler.
- `docs/avalonia.md` — a gotcha bullet recording the `Opening`-fires-before-parenting ordering, so the next `Opening` handler doesn't reach for `DataContext`.

### Out of scope
- **The solution-label menu (`OnSolutionCtxOpening`) and the code-editor menu (`OnCodeCtxOpening`).** Both are `Opening` handlers, but neither reads the menu's `DataContext` — they compute from `Vm` and from `FindActiveEditor()` respectively, so neither has this bug. Leave them imperative; converting them is not part of this fix.
- **The solution-tree node menu.** It has no `Opening` handler and no disabled states.
- **Changing the UX for output tabs.** Greyed-but-present items stay (the decision from `output-as-file-tabs`); do not switch to cancelling the menu or hiding items. Cancelling would need the same `DataContext` that isn't available yet, so it is not an alternative fix.
- **Headless view tests.** `MiniIde.Tests` does not reference `MiniIde` yet — see `docs/tickets/headless-view-tests.md`. Verification here is manual.

## Relevant Docs & Anchors
- **Design docs**: `docs/avalonia.md` — the two existing `ContextMenu` bullets (`DataContext` inheritance from the attached control; `Opening` is a synchronous `CancelEventHandler` resolved from `sender`) are what this ticket amends, plus the `ObjectConverters.IsNotNull` bullet for the idiomatic null-gating converter.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — introduced `OnTabHeaderCtxOpening`. Its Implementation explicitly offered "per-item enable bound to *has a file path*" as the alternative to the `Opening` handler; this ticket takes that branch. Read its Learnings for why the items are disabled rather than hidden.
  - `docs/tickets/complete/2026-07-10 context-menu-icons.md` — why the `MenuItem:disabled → Opacity 0.4` style exists (an explicit `Foreground` on an icon `TextBlock` defeats the theme's disabled dimming). That style must survive this change.
  - `docs/tickets/complete/2026-07-05 file-path-context-menu.md` — the original menu and the `GetTargetPath` path-resolution contract.
- **Code anchors**: `OnTabHeaderCtxOpening` and `GetTargetPath` in `Views/MainWindow.axaml.cs`; the `TabControl.ItemTemplate` header `StackPanel` and its `ContextMenu` in `Views/MainWindow.axaml`; `TabViewModelBase.FilePath`.

## Constraints & Gotchas
- **`ContextMenu.Opening` fires before the menu is parented.** Verified in the Avalonia 12.1 source: `ControlContextRequested` calls `CancelOpening()` (which raises `Opening`) and only then `Open()`, which does `_popup.SetPopupParent(control)` / `_popup.Child = this`. Any state read from `cm.DataContext`, `cm.PlacementTarget`, or a child `MenuItem`'s `DataContext` during `Opening` is therefore unset on the first open of a given menu instance. Do not "fix" this by re-reading the same properties differently in the handler — the values do not exist yet.
- **`FilePath` is immutable** (get-only, set in the `TabViewModelBase` constructor), so the binding needs no change notification. Do not add `[ObservableProperty]` machinery for it.
- **Compiled bindings are on by default** (`AvaloniaUseCompiledBindingsByDefault`). The tab-header `DataTemplate` declares `DataType="vm:TabViewModelBase"`, and that `x:DataType` scope covers the nested `ContextMenu`; if the compiler disagrees, put an explicit `x:DataType` on the `ContextMenu` rather than disabling compiled bindings for the subtree.
- **Keep the scoped `MenuItem:disabled` opacity style** in `ContextMenu.Styles`. Without it, the *Open in Explorer* icon stays bright yellow on a greyed item.
- The stale XAML comment above the menu names `OnTabHeaderCtxOpening`; it needs rewriting, not deleting — the "path actions are inert on output tabs" rationale is still the reason the gating exists.

## Open Decisions
1. **Converter vs. computed property** — `IsEnabled="{Binding FilePath, Converter={x:Static ObjectConverters.IsNotNull}}"` on each item (matches the existing `Image`/`Error` `IsVisible` bindings in this same file, no VM change) vs. a `HasFilePath => FilePath is not null` property on `TabViewModelBase`. Default: the converter.
2. **Per-item vs. menu-level gating** — three item bindings vs. one `IsEnabled` on the `ContextMenu` itself relying on effective-enabled inheritance. Default: per-item; the menu-level form disables the popup root, and its interaction with the `:disabled` style and light dismiss isn't worth the one-line saving.

## Acceptance Criteria
- [ ] Opening the tab-header context menu on a file tab for the first time shows *Open in Explorer*, *Copy absolute path*, and *Copy relative path* all enabled; opening it on an output tab shows all three disabled and dimmed. Both hold on the first open, with no dependence on a prior open of that menu.
- [ ] Tab-header item enablement is expressed declaratively in `MainWindow.axaml` against the bound tab's `FilePath`; no code-behind sets `IsEnabled` on tab-header menu items.
- [ ] `OnTabHeaderCtxOpening` no longer exists in `MainWindow.axaml.cs`, and the tab-header `ContextMenu` has no `Opening` attribute.
- [ ] `GetTargetPath`, the three `OnCtx*Click` handlers, `OnSolutionCtxOpening`, and `OnCodeCtxOpening` are unchanged in behavior.
- [ ] `docs/avalonia.md` documents that `ContextMenu.Opening` is raised before the menu is parented, and states the consequence: gate enablement with bindings, and never read `DataContext` in an `Opening` handler.

## Implementation

### 1. Gate the tab-header menu items declaratively
So enablement is computed from the bound tab rather than from a `DataContext` that hasn't arrived yet, replace the imperative gating with an `IsEnabled` binding on each of the three `MenuItem`s in the tab-header `ContextMenu` (`TabControl.ItemTemplate` → header `StackPanel` → `StackPanel.ContextMenu`, `Views/MainWindow.axaml`). Gate on the tab's `FilePath` being non-null — per Open Decision 1, the `ObjectConverters.IsNotNull` form used elsewhere in this file is the default. Remove the `Opening="OnTabHeaderCtxOpening"` attribute from that `ContextMenu`, keep its `ContextMenu.Styles` block, and rewrite the comment above it so it still explains *why* the items are inert on output tabs and now points at the binding instead of the handler.

### 2. Delete the dead handler
Remove `OnTabHeaderCtxOpening` from `Views/MainWindow.axaml.cs`. `GetTargetPath` stays — the `Click` handlers still use it, including its `TabViewModelBase` arm.

### 3. Record the ordering gotcha
Add a bullet to `docs/avalonia.md`, next to the two existing `ContextMenu` bullets: `Opening` is raised from `CancelOpening()` in `ControlContextRequested` *before* `Open()` parents the popup (`SetPopupParent` / `_popup.Child = this`), so on a menu's first open the `ContextMenu` and its `MenuItem`s have no inherited `DataContext` — a handler that reads it sees `null` and computes the wrong enablement, then looks correct on every subsequent open because the popup stays parented while the attached control is in the visual tree. Prescribe the fix shape: bind `IsEnabled` per item against the bound model; reserve `Opening` for state reachable without the `DataContext` (window-level VM fields, the focused editor), which is why the solution and code-editor menus are unaffected. Amend the existing "`ContextMenu.Opening` is a `CancelEventHandler`" bullet to cross-reference this rather than restating it.

## Test Plan
- [ ] `dotnet build MiniIde.slnx` succeeds with no new warnings, and `dotnet test MiniIde.slnx` passes.
- [ ] Launch, open `MiniIde.slnx`, open a source file, and right-click its tab header **once** — all three items are enabled on that first open. Click *Copy absolute path* and confirm the status line reports the copied path.
- [ ] Run a project so an output tab appears, then right-click the output tab header **once** — all three items are greyed *and* dimmed (icon included, not bright-yellow-on-grey).
- [ ] Open a second, previously unopened file, right-click its tab header once — enabled immediately, confirming the fix is per-tab and not an artifact of the first menu having been opened earlier.
- [ ] Regression: right-click a solution-tree node (file, folder, project) — the menu opens with its items enabled on the first try.
- [ ] Regression: with no solution loaded, right-click the `<no solution>` label — *Open new solution…* enabled, the rest greyed on first open; after opening a solution, right-click it again — all items enabled.
- [ ] Regression: right-click inside a C# editor over an identifier — *Find usages* / *Go to definition* / *Rename…* light up as before, and over whitespace they grey out.

## Learnings

### Architectural decisions
- **Open Decision 1 resolved to the converter.** Each of the three `MenuItem`s got `IsEnabled="{Binding FilePath, Converter={x:Static ObjectConverters.IsNotNull}}"`. No `HasFilePath` property was added to `TabViewModelBase` — the converter form already appears twice in this same file (`Image`/`Error` `IsVisible`), so it reads as house style, and a VM property would have been dead weight for a get-only field.
- **Open Decision 2 resolved to per-item gating.** Three bindings, not one on the `ContextMenu`. Confirmed visually that the `MenuItem:disabled → Opacity 0.4` style still fires per item, icon included — a menu-level `IsEnabled` would have made the popup root itself disabled, and the interaction with the scoped style was not worth testing for a two-line saving.
- **Compiled bindings resolved without an explicit `x:DataType` on the `ContextMenu`.** The `DataTemplate DataType="vm:TabViewModelBase"` scope reaches into `StackPanel.ContextMenu` — build succeeded with 0 warnings, so the fallback in Constraints & Gotchas was unnecessary.

### Problems encountered
- None during implementation. The fix compiled and behaved correctly on the first run; the diagnosis in the ticket was accurate.

### Interesting tidbits
- The bug is self-concealing in a way that makes it easy to dismiss as a fluke: the popup stays parented after the first close (`PopupClosed` only calls `SetPopupParent(null)` when the menu has no attached controls), so *every* open after the first looks right. Anyone who reflexively right-clicks twice while investigating would never see it.
- The app accepts a solution path as `argv[0]` (via `IClassicDesktopStyleApplicationLifetime.Args`), which made scripted verification much cheaper than driving the Ctrl+O file picker — worth remembering for future manual UI passes.
- `ContextMenu` popups are separate top-level windows, so verifying them by screenshot requires a full-screen capture, not a `PrintWindow`-style grab of the main window's rect.

### Workarounds / limitations
- None. The declarative form is the correct shape, not a workaround — Avalonia evaluates the binding once the `DataContext` propagates inside `Open()`, before the popup is shown.

### Related areas affected
- `OnSolutionCtxOpening` and `OnCodeCtxOpening` were deliberately left imperative (per Scope) and verified unchanged in behavior: the solution menu computes from `Vm`, the editor menu from `FindActiveEditor()`, and neither touches the menu's `DataContext`, so neither is exposed to the ordering bug. If either ever grows a per-item check that needs the *bound item*, it must move to a binding too.
- `docs/avalonia.md` gained a new `ContextMenu` bullet documenting the `Opening`-before-parenting ordering; the existing "`Opening` is a `CancelEventHandler`" bullet now cross-references it instead of standing alone as an invitation to compute enablement there.

### Rejected alternatives
- **Re-reading the `DataContext` differently inside `Opening`** (from `PlacementTarget`, from a child `MenuItem`, deferred to a `Dispatcher.Post`) — the values genuinely do not exist yet at that point, and a dispatcher hop would race the popup being shown.
- **Cancelling the menu on output tabs** — would need the same unavailable `DataContext`, and the greyed-but-present UX is a deliberate decision inherited from `output-as-file-tabs`.
- **Hiding the items on output tabs** — same rationale; a menu whose contents change shape between tabs is harder to build muscle memory against than one with consistent, sometimes-inert items.

### Verification notes
- `dotnet build MiniIde.slnx` — 0 warnings, 0 errors. `dotnet test MiniIde.slnx` — 60/60 passed.
- Manual pass driven by scripted clicks against a live instance, screenshotting each menu on its **first** open: file tab (all three enabled, *Copy absolute path* put `D:\Development\C-sharp\MiniIDE\src\MiniIde\Program.cs` on the clipboard), output tab from a `MiniIde.Tests` run (all three greyed *and* dimmed, folder icon included), a second freshly-opened file tab (enabled immediately — confirms per-tab, not an artifact of an earlier open). Regressions all clean on first open: solution-tree node menu, solution label with a solution loaded (all enabled) and without (only *Open solution…* enabled), editor menu over an identifier (enabled) and over whitespace (greyed).
