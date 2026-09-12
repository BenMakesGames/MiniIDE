# "Open in External App" Context-Menu Action

## Context
**Current behavior**: Right-clicking a solution-tree node or a file tab's header offers *Open in Explorer*, *Copy absolute path*, *Copy relative path*. There is no way to hand a file to the program Windows has registered for its extension — opening a `.png` in the default image viewer, a `.md` in the default editor, a `.csproj` in Visual Studio — without going out to Explorer first and double-clicking there.

**New behavior**: Both menus gain an *Open in external app* item, placed immediately above *Open in Explorer*, that shell-executes the file so Windows launches whatever app is registered for it. The item is disabled on folder nodes (shell-executing a folder just opens Explorer, duplicating the item below it) and on output tabs (no backing file), matching how those menus already grey out path actions. When the extension has no registered app, the action falls back to the Windows *Open with* picker instead of dead-ending.

## Prerequisites
None.

## Scope
### In scope
- `ShellService` — a new launch method for "open this path with its registered app", plus the no-association fallback to the Windows *Open with* dialog.
- `Views/MainWindow.axaml` — the new `MenuItem` in the tree-node context menu (inside the `TreeDataTemplate` for `m:TreeNode`) and in the tab-header context menu (inside `TabControl.ItemTemplate`), each above its *Open in Explorer* sibling.
- `Views/MainWindow.axaml.cs` — a `Click` handler alongside `OnCtxOpenInExplorerClick`.
- Enablement gating for the tree item so it greys out on folder nodes.
- A new `ActionIcon` glyph constant if the item gets an icon (see Open Decisions), plus the icon-font subset regeneration that always accompanies one.

### Out of scope
- **The solution-name top-bar context menu.** Deliberately unchanged this ticket — it keeps *Open new solution… / Reload / Open in Claude Code / Open in Explorer / Copy path*. Confirmed with the requester.
- **Opening folders externally.** Folders stay disabled; no "open folder with a registered handler" behavior.
- **Any in-app editor/preview change.** Double-click still opens the file in a MiniIDE tab; this action is purely an OS hand-off and never touches the tab set.
- **Choosing a specific app, an "Open with…" submenu listing candidates, or remembering a per-extension override.** The registered default (or the OS picker on failure) is the whole feature.
- **Non-Windows behavior.** `ShellService` is already Windows-only (`explorer.exe`, `wt.exe`); no cross-platform abstraction here.
- **Fixing the tab-menu first-open-disabled bug.** See `docs/tickets/tab-context-menu-first-open-disabled.md`; this ticket inherits whatever gating mechanism is in place and does not change it.

## Relevant Docs & Anchors
- **Related tickets**:
  - `docs/tickets/complete/2026-07-10 context-menu-icons.md` — the `ActionIcon` + `<MenuItem.Icon>` pattern, and the gotcha that an explicit `Foreground` on an icon `TextBlock` defeats the theme's disabled dimming (hence the scoped `MenuItem:disabled → Opacity 0.4` styles).
  - `docs/tickets/complete/2026-07-10 extract-shell-service.md` — why `ShellService` exists and its contract (throws on launch failure; the caller surfaces it on `Vm.Status`).
  - `docs/tickets/tab-context-menu-first-open-disabled.md` (**open, not yet implemented**) — read its Context and Constraints: `ContextMenu.Opening` fires *before* the menu is parented, so `DataContext`-derived enablement computed in an `Opening` handler is wrong on a menu's first open. That is why the new tree gating must be a binding, not an `Opening` handler.
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — why output tabs keep greyed-but-present path items rather than hiding them.
- **Code anchors**: `ShellService.RevealInExplorer` and `ShellService.OpenTerminalWithClaude` (the two existing `UseShellExecute = true` launches, including the `_wtUnavailable` fallback-once pattern); `OnCtxOpenInExplorerClick` and `GetTargetPath` in `Views/MainWindow.axaml.cs`; `OnTabHeaderCtxOpening` in the same file; the three `_Open in Explorer` `MenuItem`s in `Views/MainWindow.axaml` (grep the header text — only two of the three are in scope); `TreeNode` / `NodeKind` in `Models/SolutionNode.cs`; `Models/ActionIcon.cs`.
- **Tooling**: `tools/README.md` — the icon-font subset workflow and its guard test.

## Constraints & Gotchas
- **Adding an `ActionIcon` glyph obliges a font-subset re-run.** The shipped MDI TTF contains only the glyphs referenced by `\U000FXXXX` escapes in `src/MiniIde/**/*.cs`. After adding a constant, run `cd tools && npm run subset`. `IconFontCoverageTests` fails the test run if you forget — a forgotten re-run renders as a blank tofu box.
- **Verify the codepoint against pinned MDI 6.8.96**, not from memory — a wrong codepoint renders a different glyph or tofu, silently. `tools/fonts/MaterialDesignIconsDesktop.full.ttf` is the pinned source; the upstream cheatsheet for that tag is the reference (`src/MiniIde/Assets/icons/README.md`).
- **Never compute enablement from `DataContext` inside a `ContextMenu.Opening` handler** — it is null on a menu's first open (see the related open ticket). Gate the tree item with an `IsEnabled` binding against the bound `TreeNode`.
- **Keep the scoped `MenuItem:disabled` opacity style in step with the new item.** The tree-node `ContextMenu` currently has *no* such style because none of its items were ever disabled. If the new item carries an explicit-`Foreground` icon, a disabled folder row would show a bright icon next to grey text unless that menu gains the same `ContextMenu.Styles` block the solution and tab menus already have.
- **`Process.Start` with `UseShellExecute = true` throws `Win32Exception`** when no handler is registered; the distinguishing signal is `NativeErrorCode == 1155` (`ERROR_NO_ASSOCIATION`). Do not treat *every* failure as "no association" — a genuinely broken handler should still surface as an error rather than silently becoming an *Open with* prompt.
- **Modern Windows often shows its own "How do you want to open this file?" prompt** instead of returning `ERROR_NO_ASSOCIATION`, so the fallback path may rarely fire in practice. That is acceptable — it exists so the action never dead-ends silently, not because it is the common path.
- **`ShellService` must stay Avalonia-free** (its header states this): it throws; the view sets `Vm.Status`.

## Open Decisions
1. **Glyph choice** — candidate MDI names: `open-in-new`, `launch`, `application-export`, `open-in-app`. Default: `open-in-new`; verify the codepoint against pinned 6.8.96 before committing, and pick a color that reads against the `#1E1E22` menu background without colliding with the neighbouring yellow folder.
2. **Icon at all** — the two *Copy path* items are iconless, so an iconless new item is defensible and skips the font-subset step. Default: give it an icon, since it sits directly above the icon'd *Open in Explorer* and reads as a peer action.
3. **Tree gating shape** — a computed `TreeNode` property (mirroring the existing `IconGlyph` / `IconColor` computed members) vs. a new `IValueConverter` vs. `ObjectConverters`-style composition. Default: the computed property; there is no converter in the project today and one enum comparison does not earn the first.
4. **Fallback mechanism** — `ProcessStartInfo { Verb = "openas" }` (the shell's registered *Open with* verb) vs. `rundll32.exe shell32.dll,OpenAs_RunDLL <path>`. Default: the `openas` verb; drop to `rundll32` only if it proves unreliable in manual testing.
5. **Method name** on `ShellService` — e.g. `OpenWithRegisteredApp` / `OpenExternally` / `OpenInDefaultApp`. Default: implementer's call; keep it verb-first and consistent with `RevealInExplorer`.

## Acceptance Criteria
- [ ] The solution-tree node context menu and the file-tab header context menu each contain an *Open in external app* item positioned immediately above *Open in Explorer*.
- [ ] Invoking it on a file node or a file tab launches the program Windows has registered for that extension, with the file as its argument.
- [ ] The item is disabled (and visually dimmed in step with the rest of the row, icon included) on folder tree nodes; it is enabled on file and project nodes.
- [ ] The item is disabled on output tabs, consistent with the other path actions in that menu.
- [ ] Tree-item enablement is expressed as a binding in `MainWindow.axaml`; no `ContextMenu.Opening` handler computes it from a `DataContext`.
- [ ] `ShellService` exposes the new launch method, remains free of Avalonia/VM references, and throws on launch failure rather than swallowing it; the view surfaces the failure on `Vm.Status` with a message naming the action.
- [ ] A launch that fails with `ERROR_NO_ASSOCIATION` results in the Windows *Open with* picker for that file; any other launch failure surfaces as a status-bar message.
- [ ] The solution-name context menu is unchanged.
- [ ] If a new `ActionIcon` constant was added, the shipped subset font contains its glyph (`IconFontCoverageTests` passes).

## Implementation

### 1. Add the external-open launch to `ShellService`
So the OS hand-off lives with the app's other process launches rather than in the view, add a method next to `RevealInExplorer` that starts the given path with `UseShellExecute = true` and no explicit program — the path *is* the `FileName`. Document it in the same XML-doc style as its neighbours: what it does, and that it throws on launch failure.

Then layer the no-association fallback into the same method, mirroring the shape of `OpenTerminalWithClaude`'s wt→PowerShell fallback (try, catch the specific failure, take the second route; a success on *either* route is a success). Catch `Win32Exception` and check for `ERROR_NO_ASSOCIATION` (1155) specifically — on that, relaunch the same path via the shell's *Open with* verb (Open Decision 4). Any other exception propagates to the caller. Unlike `_wtUnavailable`, do **not** latch the fallback: association is per-extension, so the next file may well have a handler.

### 2. Add the glyph constant (if icon'd)
Per Open Decisions 1–2, add one `public const string` to `Models/ActionIcon.cs` in the existing style — `\U000FXXXX` encoding, trailing `// mdi-name` comment. Then regenerate the font: `cd tools && npm run subset`.

### 3. Gate the tree node
Add whatever `TreeNode` member the gating binding needs (Open Decision 3) so the menu can ask the node whether it can be opened externally: true for nodes with a non-null `Path` that are not `NodeKind.Folder`. Mirror the existing computed members on `TreeNode` for shape, and comment the *why* — folders would only re-open Explorer.

### 4. Add the tree-node menu item
In the tree-node `StackPanel.ContextMenu` (inside `TreeDataTemplate DataType="m:TreeNode"` in `Views/MainWindow.axaml`), insert the new `MenuItem` above *Open in Explorer*: header with an `_` mnemonic that does not collide with the existing items in that menu, `Click` wired to the new handler, `IsEnabled` bound to the step-3 member, and (if icon'd) a `<MenuItem.Icon>` `TextBlock` mirroring the Explorer icon's `FontFamily`/`FontSize`/`VerticalAlignment` with its own `Foreground`. Per Constraints, add the `ContextMenu.Styles` `MenuItem:disabled → Opacity` block to this menu — copy the one on the solution or tab menu, comment included — so the folder-disabled row dims icon *and* text together.

### 5. Add the tab-header menu item
Insert the same item above *Open in Explorer* in the tab-header `StackPanel.ContextMenu` (inside `TabControl.ItemTemplate`). Its enablement follows its siblings: `OnTabHeaderCtxOpening` disables every `MenuItem` in the menu when the bound tab has no `FilePath`, so the new item is covered with no handler change. If `docs/tickets/tab-context-menu-first-open-disabled.md` has landed by then, that menu will instead gate per-item via an `IsEnabled` binding on `FilePath` — match whichever mechanism is actually present, and do not introduce a second one alongside it.

### 6. Wire the click handler
Add a handler beside `OnCtxOpenInExplorerClick` in `Views/MainWindow.axaml.cs`, following its exact shape: resolve the target via `GetTargetPath` on the sender's `DataContext`, bail on null, call the new `ShellService` method inside `try`/`catch`, and on failure set `Vm.Status` to a message naming this action. Both call sites (tree, tab) share the one handler, as the Explorer item already does.

## Test Plan
- [ ] `dotnet build MiniIde.slnx` succeeds with no new warnings, and `dotnet test MiniIde.slnx` passes — including `IconFontCoverageTests`, which fails if the font subset was not regenerated after adding a glyph.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`. Right-click a `.cs` file in the tree → *Open in external app* sits directly above *Open in Explorer*, is enabled, and opens the file in the registered editor.
- [ ] Right-click a `.png` (or other non-code file) in the tree → opens in the default image viewer, not in MiniIDE.
- [ ] Right-click a **folder** node → the item is present, greyed, and dimmed together with its icon; clicking it does nothing. *Open in Explorer* on that same folder still opens the folder.
- [ ] Right-click a **project** node (`.csproj`) → the item is enabled and hands the project to its registered handler.
- [ ] Open a file, right-click its **tab header** → the item appears above *Open in Explorer* and opens the same file externally.
- [ ] Run a project so an output tab appears, right-click that tab's header → the item is greyed alongside the other path actions.
- [ ] Regression: the solution-name menu is untouched — *Open new solution… / Reload / Open in Claude Code / Open in Explorer / Copy absolute path / Copy relative path*, with no new item.
- [ ] Regression: *Open in Explorer* and both *Copy path* items still work from the tree and tab menus.
- [ ] No glyph renders as a blank tofu box in any of the three menus.
- [ ] No unhandled exception appears while opening the menus or invoking the action; a deliberate failure (e.g. an extension with no handler, if one is reachable on the machine) either shows the Windows *Open with* picker or reports on the status line — never crashes.

## Learnings

### Architectural decisions
- **Open Decision 1 (glyph)** — `open-in-new` (`\U000F03CC`), rendered in `#569CD6` (the app's blue, already used by the solution menu's *Open in Claude Code*). Blue reads clearly against `#1E1E22` and doesn't collide with the yellow folder glyph directly below it; neither tree nor tab menu carries another blue item, so there's no ambiguity within a menu.
- **Open Decision 2 (icon at all)** — icon'd, per the default. It sits directly above the icon'd *Open in Explorer* and reads as its peer; the two *Copy path* items stay iconless below.
- **Open Decision 3 (tree gating shape)** — computed `TreeNode.CanOpenExternally` (`Path is not null && Kind != NodeKind.Folder`), mirroring `IconGlyph`/`IconColor`. Note the ticket's premise "there is no converter in the project today" was already stale: the tab menu uses `ObjectConverters.IsNotNull`. The computed property still wins here — the tree condition is two clauses, not a null check, so a converter would need a second converter or a multi-binding.
- **Open Decision 4 (fallback)** — `ProcessStartInfo { Verb = "openas" }`. Never reached in manual testing (see below), but it costs one `catch` clause and needs no `rundll32` shell-out.
- **Open Decision 5 (method name)** — `ShellService.OpenWithRegisteredApp(string path)`. Verb-first, and says *which* app it picks — `OpenExternally` doesn't.
- One handler (`OnCtxOpenExternallyClick`) serves both menus, exactly as `OnCtxOpenInExplorerClick` already does — `GetTargetPath` already resolves `TreeNode` and `TabViewModelBase`, so no dispatch changes were needed.

### Problems encountered
- **The `ContextMenu.Opening` mechanism was already gone.** The ticket hedged ("match whichever mechanism is actually present") — `docs/tickets/complete/2026-08-20 tab-context-menu-first-open-disabled.md` had landed, so `OnTabHeaderCtxOpening` no longer exists and every tab item gates on `IsEnabled="{Binding FilePath, Converter={x:Static ObjectConverters.IsNotNull}}"`. The new item copies that binding; no second mechanism introduced.
- **The build failed on a locked `MiniIde.exe`** — a running instance of the app holds its own exe. The compile itself succeeded; only the copy step failed. Two MiniIde processes were live and only one was the dev build; the other was the installed copy at `E:\APPS\MiniIDE`. Check `Get-Process MiniIde | Select Id, Path` before killing anything.

### Interesting tidbits
- **The `openas` fallback almost certainly never fires on this machine.** Invoking the action on a file with a deliberately unregistered extension (`.miniide-nope`) produced Windows' own *"How do you want to open this file?"* dialog and `Process.Start` **succeeded** — the shell handled it before .NET could raise `ERROR_NO_ASSOCIATION`. Exactly what Constraints predicted. The fallback stays as a guarantee the action never dead-ends, not as a live path. (Its dialog defaults the *"Always use this app…"* checkbox to **checked** — dismiss with Escape, don't click OK, or you permanently register an association while testing.)
- **MDI glyph names live in the pinned font's `post` table**, so a codepoint can be verified locally instead of against a remembered cheatsheet — `opentype.js` exposes `glyph.name` and `glyph.unicode`. Cross-checking `folder`/`refresh`/`robot` against the existing constants proved the lookup was reading the right table before trusting it for `open-in-new`. Written up in `tools/README.md`.
- **The app takes a solution path as `argv[0]`** (`ResolveStartupSolution(desktop.Args)`), which makes scripted UI verification far cheaper than driving the file-picker dialog: `Start-Process MiniIde.exe -ArgumentList <path-to-.slnx>`.
- Adding a longer constant name to `ActionIcon` broke the block's `=` alignment; the three existing constants were re-padded to match. Cosmetic, same declaration block.

### Workarounds / limitations
- **Project nodes (`.csproj`) were verified as *enabled* but not invoked** — `.csproj` is registered to Visual Studio on this machine and launching it is disproportionate for a menu test. Enablement is the part the code controls; the hand-off itself is the same `Process.Start` call proven on `.md`.
- `ShellService` remains Windows-only by design (out of scope): `Verb = "openas"` is a Windows shell verb and `UseShellExecute` semantics differ on other platforms.

### Related areas affected
- The tree-node `ContextMenu` gained its first `MenuItem:disabled → Opacity 0.4` style block — previously unnecessary because nothing in that menu could be disabled. Any future disabled item in that menu now dims correctly for free.
- `docs/avalonia.md` gained the shell-execute-with-registered-app bullet (next to the existing visible-terminal one); `tools/README.md` gained the codepoint-verification recipe.

### Rejected alternatives
- **`rundll32.exe shell32.dll,OpenAs_RunDLL <path>`** for the fallback — the `openas` verb does the same through a supported API with no command-line quoting to get wrong. Kept as the escape hatch if `openas` ever proves unreliable.
- **Treating every `Win32Exception` as "no association"** — would turn a genuinely broken handler into a silent *Open with* prompt, hiding the real failure. The `when (ex.NativeErrorCode == 1155)` filter keeps other failures on the `Vm.Status` path.
- **Latching the fallback** the way `_wtUnavailable` latches — wrong here: `wt.exe` is either installed or not, but association is per-extension and varies file to file.
- **An `IValueConverter` for the tree gating** — see Open Decision 3; one enum comparison plus a null check reads better as a computed property beside the other computed `TreeNode` members.
