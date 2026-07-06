# Code-View Context Menu (Search / Find Usages / Go to Definition)

## Context
**Current behavior**: Right-clicking inside an open code editor (`AvaloniaEdit.TextEditor` in the `EditorTabViewModel` `DataTemplate`) does nothing app-specific. The three symbol/search actions all exist but are only reachable by keyboard against the *keyboard caret*: Go to Definition (F12), Find References (Shift+F12), and solution-wide search (Ctrl+Shift+F, via the Find tab). None act on the text under the mouse.

**New behavior**: Right-clicking in a code editor opens a three-item context menu — **Search solution for "…"**, **Find usages**, **Go to definition** — acting on the text/token under the pointer. The right-click first moves the caret to the clicked position (VS/Rider-style), so the two symbol actions reuse the existing caret-based paths verbatim. All three items are always present; each is independently enabled or disabled at menu-open based on a cheap, synchronous check. Items whose action turns out to be impossible only after being attempted (framework go-to-definition, unresolved identifiers) report why in the status bar and disable themselves on the next menu-open at that same token, until the document is edited.

## Prerequisites
None. Builds on existing infrastructure: the `WorkspaceService` Roslyn engine (F12/Shift+F12), the `FindResultsViewModel` search pipeline, the `RoslynColorizer` classified-span cache, and the established inline-`ContextMenu` idiom.

## Scope
### In scope
- New context menu on the code editor, wired in `Views/MainWindow.axaml` + `Views/MainWindow.axaml.cs`.
- Right-button hit-test → caret placement (the missing "act on the clicked word" primitive).
- Synchronous per-item enablement in a `ContextMenu.Opening` handler, driven by the char under the offset + the cached classified span + solution-loaded state.
- A public classification lookup on `RoslynColorizer`.
- A small distinction in `WorkspaceService.FindReferencesAsync` between "no symbol at position" and "symbol found, zero references," so find-usages failure is detectable.
- A per-editor "failed-token" cache consulted during enablement and cleared on document edit.

### Out of scope
- Metadata-as-source / decompilation so that go-to-definition can navigate into framework symbols (e.g. `System.Linq.Enumerable.Any`). Those remain "no source definition" → status message.
- Cut/Copy/Paste/Select-All menu items (leave AvaloniaEdit's own editing affordances as-is; only add the three new items — but see Constraints re: not clobbering any built-in menu).
- Changing keyboard F12 / Shift+F12 / Ctrl+Shift+F behavior.
- Any context menu on the read-only Output editor.
- Extracting the now-multiple inline `ContextMenu` blocks into a shared resource (the codebase deliberately keeps them inline — see the file-path ticket).

## Relevant Docs & Anchors
- **Code anchors** (read before coding):
  - `Views/MainWindow.axaml` — the `ae:TextEditor` (`Name="Editor"`, `AttachedToVisualTree="OnEditorAttached"`) inside the `EditorTabViewModel` `DataTemplate`; the existing inline `<ContextMenu>` blocks on the tree `TreeDataTemplate` and the tab-header `DataTemplate` (the idiom to mirror).
  - `Views/MainWindow.axaml.cs` — `BindEditor` and the `_bindings` dictionary (per-editor `EditorBinding` holding the `RoslynColorizer`); `GoToDefinitionAsync()` / `FindRefsAsync()` (the caret-based reuse targets); `FindActiveEditor()`; `OpenHit`; `FocusFind()`; the `RoslynColorizer` class at the bottom of the file (holds `_spans`).
  - `Services/WorkspaceService.cs` — `GoToDefinitionAsync(file, position)` (returns `null` when no symbol or no in-source location); `FindReferencesAsync(file, position)` (the method to split into "no symbol" vs "zero refs").
  - `ViewModels/MainWindowViewModel.cs` — `GoToDefinitionAsync(file, position)` / `FindReferencesAsync(file, position)` (thin async wrappers that `EnsureLoadedAsync` then delegate, and set `Status`); `Solution.SolutionPath`; `Status`.
  - `ViewModels/FindResultsViewModel.cs` — `Query`, `UseRegex`, `SearchCommand` (guards on `SolutionPath != null`); rooted at the solution directory.
  - `Services/SyntaxHighlightService.cs` — the single-file `AdhocWorkspace` classifier feeding the colorizer (references only `object`'s assembly, so many identifiers classify as plain `Identifier` — relevant to the allow/deny logic).
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 file-path-context-menu.md` — the origin of the inline-`ContextMenu` + `Click`-handler idiom; its Learnings cover `MenuItem.DataContext` inheritance in embedded menus.
  - `docs/tickets/complete/` `xml-json-syntax-highlighting.md` — background on the highlighting/colorizer split (C# uses `RoslynColorizer`; XML/JSON use `.xshd`).

## Constraints & Gotchas
- **Right-click must set the caret before the menu opens, and must not destroy an existing selection the user right-clicked inside.** AvaloniaEdit does not move the caret on right-click by default. Handle right-button `PointerPressed` (consider a tunneling handler so it runs before AvaloniaEdit's own pointer logic) and hit-test the point to a document offset (`editor.GetPositionFromPoint(point)` → `TextViewPosition?` → `Document.GetOffset(...)`; confirm exact API). If the click falls **inside the current selection**, preserve the selection and do not move the caret (so "Search selection" works); otherwise move the caret to the clicked offset (collapsing any selection). The two symbol actions read `editor.CaretOffset`, so once the caret is placed they need no signature change.
- **Enablement must be fully synchronous.** The `Opening` handler cannot `await` — it must not touch `WorkspaceService` (lazy MSBuild load, slow first use). Use only: the char at the offset, `RoslynColorizer.ClassificationAt(offset)` (cached spans), the failed-token cache, and `SolutionPath`/tab `Mode`.
- **`FindActiveEditor()` matches on `DataContext == Vm.ActiveTab`.** The output editor won't match, so it resolves the code editor. The menu handlers can rely on it, matching the existing F12 code-behind.
- **Find-usages empty-list ambiguity.** Today `WorkspaceService.FindReferencesAsync` returns an empty list both when *no symbol* resolves and when a symbol resolves with *zero references*. Only the former is a "failure" for retroactive-disable; the latter is a legitimate result. Splitting these (see Implementation) ripples to `MainWindowViewModel.FindReferencesAsync` and the Shift+F12 code-behind `FindRefsAsync()` — update both callers to treat the new "no symbol" signal as a `"No symbol found"` status without disabling anything on the keyboard path.
- **Adhoc classifier under-resolves.** The single-file classifier references only mscorlib, so framework/extension identifiers (e.g. `Any`) often classify as plain `Identifier` rather than `MethodName`. The enablement allow-list must therefore treat plain `Identifier` as eligible — do **not** require a specific `*Name` classification.
- **Don't clobber a built-in editor menu.** Confirm the `TextEditor`/`TextArea` doesn't already install its own `ContextMenu`; if it does, decide whether to coexist or replace (the three new items are the requirement either way).

## Open Decisions
1. **Failed-token cache key** — token text + document span vs the resolved symbol's display string. Default: token text + span, scoped per editor/tab and cleared on that document's `TextChanged`. Span-based is simplest and the edit-clear covers offset drift.
2. **Search item header** — interpolate the term (`Search solution for "Foo"`) vs a static `Search solution for this`. Default: interpolate when a term exists, since the term is already computed for enablement; fall back to static/disabled when none.
3. **Literal vs regex for search-this** — force `UseRegex = false` for this path so the clicked word is a literal query, vs respect the current toggle. Default: force literal (set `UseRegex = false` before running), to avoid a word with regex metacharacters misbehaving.
4. **Word-boundary definition for the search term** — the identifier run (`[A-Za-z_][A-Za-z0-9_]*`) under the click. Default: identifier run; reuse whatever AvaloniaEdit word helper is idiomatic if one fits.

## Acceptance Criteria
- [ ] Right-clicking inside a code editor opens a context menu with exactly three items, in order: Search solution (for the term), Find usages, Go to definition.
- [ ] Right-clicking moves the caret to the clicked position, **except** when the click lands inside an existing selection, in which case the selection is preserved.
- [ ] In a C# tab, Find usages and Go to definition are **enabled** when the click is on an identifier-shaped token (variable, type name, `IMyInterface` in its declaration, a method name such as `Any` in `list.Any(...)`), and **disabled** when the click is on an operator/punctuation (`==`), whitespace, a keyword, or inside a string literal or comment.
- [ ] In any non-C# tab (`Mode != CSharp`), Find usages and Go to definition are disabled; Search remains governed by its own rule.
- [ ] Search is enabled only when a solution is loaded (`SolutionPath != null`) and a query term exists (an active selection, else a word under the click); otherwise disabled.
- [ ] Choosing Search sets `Find.Query` to the selection if non-empty, else the word under the click, runs the search, and focuses the Find tab.
- [ ] Choosing Find usages on a resolvable symbol populates `Find.Results` with its references (same result shape as Shift+F12).
- [ ] Choosing Go to definition on a symbol with an in-source definition navigates to that location.
- [ ] Choosing Go to definition on a framework symbol (e.g. `Any`) or Find usages on a token that resolves to no symbol performs no navigation/population, sets an explanatory status-bar message, and causes that item to be **disabled** the next time the menu is opened on the same token.
- [ ] Editing the document re-enables items that were retroactively disabled at a token in that document.
- [ ] `WorkspaceService.FindReferencesAsync` reports "no symbol at position" distinctly from "symbol found with zero references."
- [ ] Keyboard F12 / Shift+F12 / Ctrl+Shift+F behavior is unchanged.

## Implementation

### 1. Distinguish "no symbol" from "zero references" in the workspace
In `Services/WorkspaceService.cs`, change `FindReferencesAsync` so that when `root.FindToken(position)` resolves to **no symbol**, it signals that distinctly from a resolved symbol that simply has no references (currently both yield an empty list). Return `null` for the no-symbol case and an empty (possibly non-null) list for the resolved-but-unreferenced case, mirroring how `GoToDefinitionAsync` already returns `null` on no symbol. Update the two callers: `MainWindowViewModel.FindReferencesAsync` (propagate the distinction; set `Status = "No symbol found"` for the null case) and the Shift+F12 code-behind `FindRefsAsync()` (treat null as "no symbol found," clearing results and setting the Find status, without touching any disable cache).

### 2. Expose the classification at an offset
Add a public method to `RoslynColorizer` (bottom of `Views/MainWindow.axaml.cs`) that returns the `ClassificationType` (string) of the cached span containing a given document offset, or `null` if no span covers it. It reads the existing `_spans` field the colorizer already maintains and refreshes on every edit. Do not recompute — this must be synchronous and allocation-light.

### 3. Provide access to the active editor's colorizer
The per-editor `RoslynColorizer` lives in `EditorBinding` inside the `_bindings` dictionary (populated by `BindEditor`). Add a small accessor so the enablement handler can obtain the colorizer for a given `TextEditor` (e.g. look it up in `_bindings`). This keeps the classified-span cache reachable from the menu code without new plumbing.

### 4. Move the caret on right-click (preserving in-selection clicks)
Wire a right-button `PointerPressed` handler on the editor (registered in `BindEditor`, alongside the existing caret-tracking handler; consider tunneling so it runs before AvaloniaEdit's pointer handling). Hit-test the pointer position to a document offset. If the offset lies within the current selection, leave the selection and caret untouched; otherwise set `editor.CaretOffset` to that offset. This makes `CaretOffset` the single source of truth for both symbol actions and the enablement pass.

### 5. Attach the context menu in XAML
In `Views/MainWindow.axaml`, add a `ContextMenu` to the `ae:TextEditor` in the `EditorTabViewModel` `DataTemplate`, mirroring the inline idiom used by the tree and tab-header templates. Three `MenuItem`s — Search solution, Find usages, Go to definition — each wired to a `Click` handler, plus an `Opening` handler on the `ContextMenu` for enablement. Give the items `Name`s (or resolve them from the sender in `Opening`) so their `IsEnabled`/`Header` can be set.

### 6. Compute per-item enablement in the Opening handler
On `ContextMenu.Opening`, synchronously resolve the active editor (`FindActiveEditor()`), read its `CaretOffset`, the tab `Mode`, `SolutionPath`, the char at the offset, `ClassificationAt(offset)`, and the failed-token cache:
- **Search**: enabled iff `SolutionPath != null` and a query term exists (non-empty selection, else an identifier run under the caret). Set its header per Open Decision #2.
- **Find usages / Go to definition**: enabled iff `Mode == CSharp` **and** the char at the offset is an identifier char **and** the classification is not a keyword / string-literal / comment / numeric / operator / punctuation kind **and** the token is not marked failed for this action in the cache. (Plain `Identifier` classification counts as eligible — see Constraints.)

### 7. Search click handler
Compute the term (selection if non-empty, else the identifier run under the caret), set `Find.Query` to it, force `UseRegex = false` (Open Decision #3), execute `Find.SearchCommand`, and call `FocusFind()` to reveal the Find tab. Guard on the same conditions as enablement (defensive — the item should already be disabled otherwise).

### 8. Find-usages and Go-to-definition click handlers
Reuse the existing behavior but capture the outcome so failures can be recorded. Call `Vm.FindReferencesAsync(ActiveTab.FilePath, CaretOffset)` / `Vm.GoToDefinitionAsync(ActiveTab.FilePath, CaretOffset)` (these already `EnsureLoadedAsync` and set status). On success, navigate/populate exactly as `FindRefsAsync()` / `GoToDefinitionAsync()` do today (populate `Find.Results`; `OpenHit` the definition). On the "cannot resolve" outcome — go-to-def `null`, or find-usages "no symbol" (the null from step 1) — record the token in the failed-token cache for that action so step 6 disables it on reopen. The VM wrappers already surface an explanatory `Status`; add a message only if the existing one is insufficient. A resolved symbol with zero references is **not** a failure and is not cached.

### 9. Invalidate the failed-token cache on edit
Clear the relevant failed-token entries when the document changes, so a fixed typo or added `using` re-enables the items. Hook the tab's document `TextChanged` (already subscribed in `BindEditor` for redraw) to clear the cache scoped to that editor/tab. Key details per Open Decision #1.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj` succeeds with no new warnings.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`, open a C# file.
- [ ] Right-click a local variable — caret jumps to it; all three items enabled. Go to definition navigates to its declaration; Find usages fills the Find list; Search opens the Find tab pre-populated with the identifier.
- [ ] Right-click `==` (or other operator), a space, a keyword (`class`), inside a string literal, and inside a comment — Find usages and Go to definition are disabled each time; Search is enabled only where a word/selection term exists.
- [ ] Right-click `IMyInterface` in `public interface IMyInterface` — both symbol items enabled; Go to definition lands on the declaration.
- [ ] Right-click `Any` in a `someList.Any(...)` call — both enabled; Find usages lists in-solution call sites; Go to definition performs no navigation and shows a "no source definition"-style status; reopening the menu on `Any` shows Go to definition disabled.
- [ ] Right-click an intentional typo identifier (no such symbol) — attempt Find usages: no population, status indicates no symbol; reopen menu on the same token — Find usages disabled. Fix the typo (edit) and reopen — item re-enabled.
- [ ] Select a multi-token phrase, right-click inside the selection — selection is preserved (caret not moved); Search uses the full selection as the query.
- [ ] Open an XML or JSON tab, right-click — Find usages and Go to definition are disabled; Search behaves per its rule.
- [ ] With no solution loaded, right-click in any editor — Search is disabled and nothing throws.
- [ ] Regression: F12, Shift+F12, and Ctrl+Shift+F still behave as before, including Shift+F12 on a non-symbol now reporting "No symbol found."
- [ ] No exceptions appear in the Output pane throughout.

## Learnings

### Architectural decisions
- **Deny-list, not allow-list, for symbol-action eligibility** (Constraints / adhoc under-resolves). `IsDeniedClassification(string?)` rejects a classification whose name *contains* `keyword`/`string`/`comment`/`number`/`operator`/`punctuation`/`excluded`/`whitespace`. Substring matching (rather than a `ClassificationTypeNames.X` set) is deliberate: it covers Roslyn's dotted variants in one line (`keyword - control`, `string - verbatim`, `xml doc comment - text`, …). A null classification (no covering span) is treated as *eligible* — the identifier-char gate is the real filter, and the adhoc classifier frequently emits plain `Identifier` (or nothing) for framework/extension members like `Any`.
- **Failed-token cache = `HashSet<(int Start, int End, string Text, CtxAction Action)>` on `EditorBinding`** (Open Decision #1, span + text). Cleared on the document's `TextChanged` **and** on tab rebind — the single `TextEditor` is recycled across tabs (see `BindEditor`'s `ReferenceEquals(b.CurrentTab, tab)` guard), so a per-editor cache would leak one tab's failures into another with the same offsets. Keying includes `Text` so an edit that shifts offsets can't accidentally match a stale entry even before the clear fires.
- **Right-click caret placement via a tunneling `PointerPressed`** registered once in `BindEditor` (Impl step 4). Tunnel so it beats AvaloniaEdit's own pointer handling and runs before the menu opens on release. Inside-selection clicks are preserved (`SelectionStart..SelectionStart+SelectionLength`), else `ClearSelection()` + set `CaretOffset` — because `CaretOffset` alone does not collapse a selection, and a stale selection would hijack the Search term.
- **`WorkspaceService.FindReferencesAsync` now returns `IReadOnlyList<…>?`** — `null` = no symbol at position, empty = symbol with zero refs (Impl step 1). Mirrors `GoToDefinitionAsync`'s existing null-on-no-symbol. Both callers updated: the VM wrapper sets `Status = "No symbol found"` on null; the keyboard `FindRefsAsync()` clears results + sets `"No symbol found"` (this is the one intentional keyboard-path behavior change, called out in the Test Plan). The context handler additionally records the failed token.
- **Search forces `UseRegex = false`** (Open Decision #3) so a clicked identifier with regex metacharacters can't misbehave. **Header interpolates the term** ellipsized to 30 chars (Open Decision #2/#4) — full term still drives the query.
- **`PopulateRefs` extracted** to share the results-population between keyboard `FindRefsAsync()` and `OnCtxFindUsagesClick`. Only dedup introduced; no other refactors (scope held).

### Problems encountered / verification
- **Confirming the AvaloniaEdit API surface offline was the main friction.** `Assembly.LoadFile` + `GetType`/`GetTypes` returned null/empty for both AvaloniaEdit and Avalonia.Controls (dependency load failures swallowed). What worked: (a) ASCII-scanning the DLL bytes for method-name substrings to confirm existence, and (b) `System.Reflection.MetadataLoadContext` with a `PathAssemblyResolver` over the ref assemblies + the running runtime's dir to reflect real signatures. That's how `ContextMenu.Opening`'s type (`System.ComponentModel.CancelEventHandler`) was nailed down.
- **AvaloniaEdit installs no built-in context menu** — byte-scanning `AvaloniaEdit.dll` found zero occurrences of `ContextRequested`/`ContextMenu`/`set_ContextMenu`, so the "don't clobber a built-in menu" constraint is moot: `TextEditor.ContextMenu` is free real estate.

### Interesting tidbits
- Named `MenuItem`s inside a `DataTemplate` are **not** in the window namescope, so `FindControl` can't reach them. The `Opening` handler resolves them from `((ContextMenu)sender).Items` by `mi.Name` instead.
- `TextViewPosition.Line`/`Column` are 1-based and feed `Document.GetOffset(line, col)` directly — same overload already used by `OpenHit`.

### Not verified here (manual Test Plan)
- The interactive Test Plan (right-click behaviors, enablement across token kinds, retroactive-disable, XML/JSON/no-solution cases) requires driving the GUI and was left to a manual pass. Automated checks done: `dotnet build` clean (one pre-existing `WorkspaceFailed` obsolete warning only), XAML compiles (so all `Click`/`Opening` handler signatures resolved), and the app launches with the solution loaded without a startup crash.

### Rejected alternatives
- **Allow-list of `*Name` classifications** for eligibility — rejected because the adhoc classifier under-resolves framework members to plain `Identifier`, which an allow-list would wrongly disable (`Any` in `list.Any(...)` must stay enabled for Find usages).
- **Focusing the Find tab on Find usages** — not done; kept parity with the keyboard Shift+F12 path per Impl step 8 ("exactly as `FindRefsAsync()` does today"). Only Search reveals the Find tab.
