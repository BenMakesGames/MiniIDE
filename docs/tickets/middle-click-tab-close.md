# Middle-Click a Code-Area Tab to Close It

## Context
**Current behavior**: A tab in the main file-area `TabControl` can only be closed by clicking the small ✕ button in its header (`OnCloseTabClick` → `tab.CloseCommand`). There is no middle-click affordance.

**New behavior**: Middle-clicking (mouse-wheel button) anywhere on a tab's header in the main file-area `TabControl` closes that tab, running the exact same close path as the ✕ button — including any save-on-close / live-run-stop behavior in `CloseTabAsync`. This is a pure addition; the ✕ button, selection, and context menu are unaffected.

## Scope
### In scope
- The main file-area `TabControl` header (`ItemTemplate` in `MainWindow.axaml`, DataContext = `TabViewModelBase`): detect a middle-button press on a tab header and invoke that tab's close command.
- A code-behind handler in `MainWindow.axaml.cs` that mirrors the resolution done by `OnCloseTabClick` (get the `TabViewModelBase`, execute its `CloseCommand`).

### Out of scope
- The bottom `BottomTabs` panel (Find / NuGet / Problems / Disk) — those are fixed tool tabs with no close affordance; do not add middle-click-close there.
- Any change to close semantics (save prompts, stopping a live output tab's process). Middle-click routes through the same `CloseCommand`/`CloseTabAsync` path as the ✕ button, so behavior stays identical — this ticket only adds a new trigger.
- Mouse-button remapping / configurability.

## Relevant Docs & Anchors
- **Related tickets**: `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — describes the `CloseTabAsync`/`RequestClose` close path and the live-run-stop-on-close behavior that middle-click will now also trigger (for context; no change to it here).
- **Code anchors**:
  - `Views/MainWindow.axaml` — the file-area `TabControl` (`ItemsSource="{Binding Tabs}"`) and its `TabControl.ItemTemplate` (the header `StackPanel` with the ✕ `Button Click="OnCloseTabClick"` and the `Header` `TextBlock`).
  - `Views/MainWindow.axaml.cs` — `OnCloseTabClick` (the exemplar for resolving the tab and executing `CloseCommand`); the several tunnel `PointerPressed` handlers (`OnTreePointerPressed`, `OnNuGetProjectsPointerPressed`, etc.) as the local idiom for pointer handling.
  - `ViewModels/TabViewModelBase.cs` — `CloseCommand` / `CloseAsync` / `RequestClose`.

## Constraints & Gotchas
- **Middle-button detection uses `PointerUpdateKind`, not a click count.** In Avalonia, read `e.GetCurrentPoint(<visual>).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed` inside a `PointerPressed` handler (there is no `MiddleTapped`/`Click` event for the middle button). No existing handler in the codebase does this yet — it's new.
- **Don't break left-click selection or right-click context menu.** Only act when the press is the middle button; return early otherwise so normal `TabControl` selection and the header `ContextMenu` keep working. Set `e.Handled = true` only in the middle-button branch.
- **Async void handler + fire-and-forget close.** `CloseCommand.ExecuteAsync(null)` is awaited in `OnCloseTabClick`; mirror that (async void handler, or fire the command) so save-on-close still runs.

## Open Decisions
1. **Handler wiring** — add `PointerPressed="..."` directly on the header `StackPanel` in the `ItemTemplate` (DataContext is the tab, resolve via `(sender as Control)?.DataContext as TabViewModelBase`) vs. a tunnel handler added on the `TabControl` in the ctor that hit-tests to the tab. Default: the `ItemTemplate`-level handler — it's the most direct (the tab is already the DataContext) and matches how the ✕ `Button` is wired. Note the header template only spans the ✕+title content, so middle-click registers over the header content rather than the TabItem's outer padding; that's the expected, universal middle-click-close target.
2. **Which visual to pass to `GetCurrentPoint`** — the `sender` control is fine. Default: `sender as Visual`.

## Acceptance Criteria
- [ ] Middle-clicking a tab's header in the main file-area `TabControl` closes that tab, and the tab it closes is the one under the cursor (not necessarily the selected tab).
- [ ] Middle-clicking closes via the same path as the ✕ button, so a live/running output tab is still stopped on close and any save-on-close logic in `CloseTabAsync` still runs.
- [ ] Left-clicking a tab header still selects it; right-clicking still opens the header context menu — neither is intercepted by the middle-click handling.
- [ ] Middle-clicking has no effect on the bottom `BottomTabs` tool tabs.

## Implementation

### 1. Add a middle-button pointer handler to the tab header
In `MainWindow.axaml`, on the header `StackPanel` inside the file-area `TabControl.ItemTemplate` (the one containing the ✕ `Button` and `Header` `TextBlock`), wire a `PointerPressed` handler (Open Decision #1). In `MainWindow.axaml.cs`, add the handler: read the pointer's `PointerUpdateKind` via `e.GetCurrentPoint(sender as Visual).Properties`; if it is not `MiddleButtonPressed`, return without handling. Otherwise resolve the `TabViewModelBase` from the sender's `DataContext` (mirroring how `OnCloseTabClick` resolves the tab from the button `Tag`), set `e.Handled = true`, and `await tab.CloseCommand.ExecuteAsync(null)`. Guard the DataContext cast so a press with no resolvable tab is a no-op.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings (kill any running `MiniIde.exe` first — it locks its own output DLL).
- [ ] Launch via `scripts/run.ps1`. Open a solution and open several file tabs. Middle-click a tab's header — that tab closes and the others remain.
- [ ] Middle-click a tab that is *not* currently selected — the middle-clicked tab closes (selection is unaffected on the survivors).
- [ ] Left-click various tabs — selection still switches normally. Right-click a tab header — the Open in Explorer / Copy path context menu still appears.
- [ ] Run a project to create a live `<project> - Output` tab, then middle-click its header while it's still running — the tab closes and the process stops (parity with closing via ✕).
- [ ] Middle-click within the bottom Find/NuGet/Problems/Disk tab strip — nothing closes.
