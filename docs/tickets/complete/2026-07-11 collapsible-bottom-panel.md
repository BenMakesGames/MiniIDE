# Collapsible Bottom Panel

## Context
**Current behavior**: The bottom tool panel (`BottomTabs` — Find / NuGet / Problems) occupies a fixed-pixel grid row in the workspace `Grid` (`RowDefinitions="*,4,220"`). A `GridSplitter` above it lets the user drag that row taller or shorter, but there is no way to get the panel out of the way entirely — it always consumes at least whatever the splitter was last dragged to. A prior ticket (`2026-07-07 activate-output-panel-on-run.md`) explicitly noted the panel "isn't collapsible today (fixed row height)" and scoped collapse handling out.

**New behavior**: A `▾` button, right-aligned over the Find/NuGet/Problems tab strip, collapses the bottom panel to a 22px bar showing only the tab headers — and disables splitter dragging while collapsed. Clicking the button again (now `▴`), clicking any tab in the strip, or any action that reveals a bottom tab (Ctrl+Shift+F, "Search solution", Shift+F12, "Find usages") restores the panel to its pre-collapse height and re-enables the splitter. Collapse state is view-only and does not persist across restarts.

## Scope
### In scope
- Name the workspace `Grid` and the bottom `GridSplitter` so code-behind can reach them (neither has an `x:Name` today).
- A collapse/restore toggle button overlaid on the `BottomTabs` header strip.
- Collapse/expand logic in `MainWindow.axaml.cs` — row height stash/restore + splitter enable/disable.
- Route the existing reveal path (`FocusFind`) and the currently-unrevealing find-references path (`FindRefsAsync`) through a shared "show a bottom tab" helper that expands first.

### Out of scope
- **Persisting collapse state or panel height across restarts.** No settings/preferences infrastructure exists in the app; the panel opens expanded every launch.
- **Auto-collapse.** Nothing ever collapses the panel implicitly — not on focus loss, not on navigation. `Reveal` deliberately calls `editor.Focus()` after every find/problem/go-to-def jump, and that must not be read as a cue to collapse.
- **Any change to tab-header font size, padding, or `MinHeight`.** The 22px bar works as-is (see Constraints).
- **Collapsing the left solution-tree panel**, or the NuGet tab's internal splitters. Only the editor↔bottom-panel splitter is touched.
- **Animating the collapse/expand.** Instant height change.

## Relevant Docs & Anchors
- `docs/avalonia.md` — two entries are directly load-bearing: *"Select a `TabItem` programmatically from code-behind … Keep tab-activation a view concern here — don't model 'selected tab' in the VM"*, and *"A `Button` can carry **both** `Command` and a code-behind `Click` handler"*. Collapse state is layout state and belongs in the view by the same argument.
- `docs/tickets/complete/2026-07-07 activate-output-panel-on-run.md` — establishes the reveal-a-bottom-tab pattern and the "tab selection is a view concern, do not push it into the view model" rule. Its `ShowOutput()` helper is gone (Output moved to the editor tab strip in `2026-07-10 output-as-file-tabs.md`), leaving `FocusFind` as the sole survivor of the pattern.
- **`MainWindow.FocusFind`** (`src/MiniIde/Views/MainWindow.axaml.cs`) — the one and only existing path that programmatically selects a bottom tab. Selects `FindTab`, then `Dispatcher.UIThread.Post`s a focus + select-all onto `FindBox` at `Background` priority (the deferral is required — the `TabItem`'s content isn't realized until after selection).
- **`MainWindow.TrySearchTermInEditor`** and the `Ctrl+Shift+F` branch of **`MainWindow.OnGlobalKeyDown`** — the two callers of `FocusFind`.
- **`MainWindow.FindRefsAsync`** (reached from the `Shift+F12` branch of `OnGlobalKeyDown` and from `OnCtxFindUsagesClick`) — populates the Find panel's results via `MainWindowViewModel.FindReferencesAsync` → `FindResultsViewModel.ShowReferences`, but **never selects the Find tab**. Harmless while the panel is always visible; a real bug once it can be collapsed.
- **The tab-close `✕` button** in the `TabControl.ItemTemplate` of the document tab strip (`MainWindow.axaml`) — the exemplar for a compact chrome button: `Padding="4,0" MinHeight="0" MinWidth="0"`, literal Unicode glyph in `Content`.
- **The `▶ Run` / `■ Stop` buttons** in the top bar — precedent that chrome buttons in this app use literal Unicode characters in `Content`, *not* the `IconFont` / `ActionIcon` glyph machinery.

## Constraints & Gotchas
- **22px is correct and the resulting clipping is intended.** Fluent's `TabItem` theme carries a `MinHeight` of 48, so at a 22px row the header strip is clipped — the tab text remains visible and the selected-tab underline is cut off. This was measured in the running app and is the desired look. **Do not** "fix" it by overriding `MinHeight`/`Padding`, shrinking the font, or adding a collapsed-state style class. No styling changes are needed at all.
- **Clicking the already-selected tab does not raise `SelectionChanged`.** If the panel is collapsed while Find is the active tab and the user clicks "Find", a `SelectionChanged`-based restore silently does nothing. Restore-on-tab-click must therefore hang off a **tunneling `PointerPressed`** on `BottomTabs` (registered `RoutingStrategies.Tunnel`, mirroring the `SolutionTree` handler registrations in the `MainWindow` ctor), guarded to act only while collapsed, and **must not** set `e.Handled` — the press still has to commit tab selection.
- **The stashed height must be read at collapse time, not hardcoded.** The `220` in the markup is only the initial value; the `GridSplitter` mutates `RowDefinitions[2].Height` in place, so the height to restore is whatever is there when the user collapses.
- **Disabling the splitter is load-bearing.** Left enabled, the user can drag a "collapsed" panel back open behind the toggle's back, desyncing the button glyph from reality.
- **Keep all of this out of the ViewModel.** `MainWindowViewModel` holds selection state but has never held layout state, and the codebase has an explicit recorded decision against putting tab/panel view concerns in the VM. A `bool` field plus a stashed `GridLength` in `MainWindow.axaml.cs` is the whole state.

## Open Decisions
1. **Minimum restore height** — if the user drags the splitter nearly shut and *then* collapses, restore returns to that same sliver. Default: accept it (restore is faithful to what they left). A clamped minimum is the alternative if it feels broken in practice.
2. **Splitter appearance while collapsed** — it stays a visible 4px seam, just non-draggable. Default: leave it visible (it still reads as the panel boundary). Hiding it is the alternative.
3. **Whether "Find usages" also focuses the `FindBox`** — `FocusFind` focuses and select-alls the query box, which makes sense for Ctrl+Shift+F but is odd for Shift+F12 (the user wants the *results*, and select-all on the query text is noise). Default: find-usages expands + selects the Find tab but leaves focus alone.
4. **Tooltip on the toggle button** (e.g. "Collapse panel" / "Restore panel"). Default: add one; low cost.
5. **Button glyph pair** — `▾`/`▴` as specified. Default: keep. Implementer may substitute a visually better-balanced pair if these render poorly at the button's size.

## Acceptance Criteria
- [ ] The workspace `Grid` and the editor↔bottom-panel `GridSplitter` each carry an `x:Name` referenced from `MainWindow.axaml.cs`.
- [ ] A toggle `Button` is right-aligned over the `BottomTabs` header strip, remains visible and clickable while the panel is collapsed, and does not overlap the Find/NuGet/Problems tab headers.
- [ ] Clicking the toggle while expanded sets the bottom row's height to exactly 22px and leaves the tab headers visible and clickable.
- [ ] While collapsed, the `GridSplitter` cannot resize the panel.
- [ ] Clicking the toggle while collapsed restores the bottom row to the exact height it had immediately before collapsing (including a height the user had previously set by dragging the splitter, not just the initial 220), and re-enables the splitter.
- [ ] The toggle's glyph reflects state: `▾` when expanded, `▴` when collapsed.
- [ ] Clicking any tab header in `BottomTabs` while collapsed restores the panel — **including clicking the tab that is already selected**.
- [ ] `Ctrl+Shift+F` and the "Search solution" context-menu item expand the panel (when collapsed) before selecting and focusing the Find tab; their existing behavior when already expanded is unchanged.
- [ ] `Shift+F12` and the "Find usages" context-menu item expand the panel (when collapsed) **and select the Find tab**, so references results are visible — a change from today, where they populate the Find panel without selecting it.
- [ ] Collapse state lives entirely in `MainWindow.axaml.cs`; no ViewModel gains a collapse/height/layout property.

## Implementation

### 1. Name the layout controls
The collapse logic needs to mutate the bottom row's height and toggle the splitter, and neither control is reachable today. In `MainWindow.axaml`, add an `x:Name` to the workspace `Grid` (the one declaring `RowDefinitions="*,4,220"`) and to the `GridSplitter` sitting in its row 1. Names are the implementer's call.

### 2. Overlay the toggle button on the tab strip
`TabControl` exposes no header-area extension point, and forking its `ControlTemplate` to add one is disproportionate. Instead, wrap `BottomTabs` in a single-cell `Grid` (taking over its `Grid.Row="2"` placement) and make the toggle `Button` a second child of that cell with `HorizontalAlignment="Right"` and `VerticalAlignment="Top"`, so it floats over the right end of the header strip. Give it the compact-chrome treatment used by the document tab-close button (`Padding="4,0"`, `MinHeight="0"`, `MinWidth="0"`) so it fits inside the 22px bar when collapsed, a literal `▾` in `Content`, and a `Click` handler. Keeping the button *outside* `BottomTabs` in the visual tree matters — it means the step-4 tunneling handler on `BottomTabs` never sees the button's own press, so the two paths can't double-fire.

### 3. Add collapse/expand to the code-behind
In `MainWindow.axaml.cs`, hold two pieces of state: a `bool` for collapsed-ness and a `GridLength` for the stashed height. Add a private const for the 22px collapsed height rather than scattering the literal.

Write three helpers, layered so every caller lands on the same code path:
- **Collapse** — stash the bottom row's *current* `Height` (read it live; the splitter may have changed it), set the row height to the 22px const, disable the splitter, flip the flag, and set the button's `Content` to `▴`.
- **Expand** — no-op if not collapsed; otherwise restore the stashed `GridLength`, re-enable the splitter, clear the flag, and set the button's `Content` back to `▾`. Being a safe no-op when already expanded is what lets every other caller invoke it unconditionally.
- **Show-a-bottom-tab** — takes the target `TabItem`, calls Expand, then sets `BottomTabs.SelectedItem`. This is the reveal primitive the rest of the app uses.

The button's `Click` handler simply dispatches to Collapse or Expand on the flag.

### 4. Restore on a tab-strip click
Register a `PointerPressed` handler on `BottomTabs` with `RoutingStrategies.Tunnel` in the `MainWindow` ctor, alongside the existing `SolutionTree.AddHandler(...)` registrations. Have it call Expand and return immediately if not collapsed. Do **not** set `e.Handled` — the press must still reach the `TabItem` and commit the selection. Because the panel is only 22px while collapsed, the only things under the pointer are the tab headers, so no narrower hit-test is needed; the "only when collapsed" guard is what keeps this inert during normal use.

### 5. Route the existing reveal path through the new primitive
Rework `FocusFind` so it calls the step-3 show-a-bottom-tab helper with `FindTab` instead of assigning `BottomTabs.SelectedItem` directly. Leave its `Dispatcher.UIThread.Post` focus + select-all block on `FindBox` exactly as-is — the deferral is still required. Its two callers (`TrySearchTermInEditor` and the `Ctrl+Shift+F` branch of `OnGlobalKeyDown`) need no changes.

### 6. Reveal the Find tab on find-references
`FindRefsAsync` fills the Find panel today but never surfaces it, which becomes a visible bug once the panel can be collapsed. After its `await Vm.FindReferencesAsync(...)`, call the show-a-bottom-tab helper with `FindTab`. Per Open Decision 3, don't reach for `FocusFind` here — the query box shouldn't be focused and select-alled on a find-usages. This covers both entry points (`Shift+F12` and `OnCtxFindUsagesClick`) since they share this method.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds. (Close any running MiniIde instance first — it locks `MiniIde.exe` and the copy step fails.)
- [ ] Launch the app and open a solution. Click the `▾` toggle — the bottom panel shrinks to a thin bar showing just the Find/NuGet/Problems tab text (the selected-tab underline is clipped away; this is expected). The glyph flips to `▴`.
- [ ] While collapsed, try to drag the splitter above the bar — nothing moves.
- [ ] Click `▴` — the panel returns to its previous height and the splitter drags normally again.
- [ ] Drag the splitter to a clearly non-default height (say, twice as tall as normal). Collapse, then restore — it comes back to *that* height, not 220.
- [ ] Collapse while the **Find** tab is selected, then click the **Find** tab header itself — the panel restores. (This is the `SelectionChanged`-won't-fire case; verify it explicitly.)
- [ ] Collapse while Find is selected, then click **NuGet** — the panel restores *and* switches to NuGet.
- [ ] Collapse the panel, then press `Ctrl+Shift+F` with the caret on an identifier in a code tab — the panel expands, the Find tab is selected, the search runs, and the query box is focused with its text selected.
- [ ] Collapse the panel, then press `Ctrl+Shift+F` with no usable term (e.g. no editor open) — the panel still expands and focuses the empty Find box.
- [ ] Collapse the panel, put the caret on a symbol, press `Shift+F12` — the panel expands, the Find tab is selected, and the references are visible. Repeat via the "Find usages" context-menu item.
- [ ] Regression: with the panel **expanded**, `Shift+F12` now also switches the bottom strip to the Find tab (previously it left the strip alone). Confirm this is the intended new behavior and nothing else about the results changed.
- [ ] Regression: clicking a Find result still opens the file and focuses the editor, and the bottom panel stays open (nothing auto-collapses on focus loss).
- [ ] Regression: the NuGet tab's internal splitters and the tree↔editor splitter still drag normally, both while the bottom panel is expanded and while it is collapsed.

## Learnings

### Open Decisions — how they resolved
1. **Minimum restore height — superseded.** The ticket's default was "accept the sliver". During review the user asked for a real floor instead, so the bottom `RowDefinition` now carries `MinHeight="200"`. Because the splitter can no longer strand the panel below 200, the stashed height is *always* ≥ 200 and the "restore to a sliver" case the decision worried about can't arise any more. The decision dissolved rather than being chosen.
2. **Splitter while collapsed — left visible.** `IsEnabled = false` on the `GridSplitter`; it stays a 4px seam and reads as the panel boundary. Fluent's disabled state didn't visibly dim it (the explicit `Background="#2D2D30"` wins), so no styling was needed.
3. **Find-usages does not focus the `FindBox`.** `FindRefsAsync` calls `ShowBottomTab(FindTab)` (expand + select), not `FocusFind` — the user wants the results, and select-alling the query box would be noise.
4. **Tooltip — added.** `ToolTip.SetTip` flips it between "Collapse panel" / "Restore panel" alongside the glyph.
5. **Glyph pair — kept `▾`/`▴`.** Renders fine at the button's size.

### Architectural decisions
- **Three layered helpers, one code path.** `CollapseBottomPanel` / `ExpandBottomPanel` / `ShowBottomTab(TabItem)`. `Expand` is a deliberate no-op when already expanded, which is what lets every reveal path call it unconditionally — no caller has to know the panel's state. `ShowBottomTab` is the reveal primitive; `FocusFind` and `FindRefsAsync` both route through it, so neither can forget the expand half.
- **All state in the view.** A `bool` and a stashed `GridLength` in `MainWindow.axaml.cs`. No VM touched, per the codebase's standing rule that tab/panel selection is a view concern.
- **The 200px floor is state, not markup.** `MinHeight` has to be zeroed *before* setting the 22px collapsed height, or the floor clamps the bar straight back up to 200 and the collapse silently does nothing. `Expand` puts it back. This is the one non-obvious ordering constraint in the feature.

### Problems encountered
- **The toggle button didn't line up with the tab labels.** Initially it was `VerticalAlignment="Top"` with a 2px margin, so it floated above the text baseline of the Find/NuGet/Problems labels. Fixed by wrapping it in a band `Grid` exactly as tall as the tab-header strip and centering the button inside it.
- **…and binding that band's height to `#FindTab.Bounds.Height` was a trap.** It compiles, and it's correct while expanded — but when the row is squeezed to 22px the `TabItem` keeps reporting its *unclipped* ~48px bounds, so the band stayed 48 tall, centering the button ~24px below the panel top: past the bottom edge of a 22px bar, where it rendered clipped. Measured from a screenshot (button pixel rows 824–831 vs. the label centered at 822). Replaced with the band height set explicitly from `Collapse`/`Expand`, which already own the state — the strip height is a `TabStripHeight = 48` const (Fluent's `TabItem` MinHeight), and the band drops to `CollapsedPanelHeight` on collapse. Verified after the fix: label center y=635, button center y=635.
- **Restore-on-tab-click can't hang off `SelectionChanged`** (as the ticket warned): clicking the *already-selected* tab doesn't raise it. Tunneling `PointerPressed` on `BottomTabs`, never setting `e.Handled`, confirmed working in the running app.

### Interesting tidbits
- Keeping the toggle **outside** `BottomTabs` in the visual tree isn't just tidiness — it's why the tunneling `PointerPressed` handler on `BottomTabs` never sees the button's own press, so collapse-via-button and restore-via-tab-click can't double-fire.
- `OnBottomTabsPointerPressed` needs no collapsed-guard of its own: `ExpandBottomPanel()` is already a no-op when expanded, so the handler is a one-liner.

### Related areas affected
- `FindRefsAsync` now selects the Find tab on **every** find-usages, expanded or not — a deliberate behavior change from before (it used to populate the panel without surfacing it).

### Rejected alternatives
- **Forking the `TabControl` `ControlTemplate`** to add a real header-area slot — disproportionate for one button.
- **Giving the button itself `Height="48"`** instead of a band wrapper — it would render 48px of button chrome on the strip, and would clip the same way as the `Bounds` binding when collapsed.
- **Overriding `TabItem` `MinHeight`/`Padding`** to make the 22px bar "fit" — explicitly out of scope; the clipped strip is the intended look.
