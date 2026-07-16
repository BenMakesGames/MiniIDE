# Safe Rename (solution-wide refactor from the code context menu)

## Context
**Current behavior**: The code-editor context menu (`Views/MainWindow.axaml`, the `ContextMenu` with `Opening="OnCodeCtxOpening"` inside the `EditorTabViewModel` `DataTemplate`) offers three read-only symbol actions — Search solution / Find usages / Go to definition. Nothing in the app *changes* code. There is no way to rename a symbol, and there are no custom dialogs at all: `MainWindow` is the only `Window`, and every interaction today is context-menu / status-bar / panel.

**New behavior**: Right-clicking a code symbol **defined in the open solution** additionally offers **Rename…**. Choosing it opens the app's first modal dialog asking for a new name; a valid new name performs a safe, solution-wide Roslyn refactor — updating every reference and, when the symbol is a type whose file name matches (`Foo`→`Foo.cs`), renaming the file too. Changes are written to disk and the read-only view reconciles from disk (Ticket 1's model). The refactor runs against a **fresh on-disk snapshot**, re-resolving the clicked symbol at invoke time so a stale view can't silently rename the wrong symbol. Roslyn conflict annotations (new name collides with an existing member) are surfaced rather than blindly applied.

## Prerequisites
- **Ticket 1 — "Read-only IDE over authoritative disk"** (owned by a sibling agent; not yet a file in `docs/tickets/`). This ticket depends on it for three concrete artifacts:
  - A **fresh-disk solution snapshot** for semantic queries — replacing today's `WorkspaceService.SyncDocumentsAsync` unsaved-buffer overlay. Rename must resolve and refactor against disk truth, never a buffer overlay.
  - The **view-reconciles-from-disk machinery** — after rename writes changed files, the open tabs and solution tree re-read from disk through Ticket 1's path rather than this ticket inventing its own reload.
  - The **removal of editing** — with no dirty buffers, this ticket carries **no** autosave-confirm, keep-mine/take-theirs conflict resolution, or "block if dirty" logic. If Ticket 1 has not landed, those absences are invalid and this ticket must not be implemented.

## Scope
### In scope
- A **Rename…** item added to the existing code-editor context menu, enabled only where a symbol action can resolve.
- A **RenameService** (new) wrapping `Microsoft.CodeAnalysis.Rename.Renamer.RenameSymbolAsync`, producing the set of changed documents + any file-rename, plus surfacing conflict annotations. Lives beside `WorkspaceService` in `Services/`.
- Surfacing `WorkspaceService`'s symbol resolution so the rename path resolves "the symbol under the caret" identically to Find usages / Go to definition (today `ResolveSymbolAsync` is **private**).
- The app's **first custom modal**: a new-name prompt with validation (non-empty, changed, valid C# identifier).
- **Disk apply**: write changed closed files directly (mirroring `NuGetService.SetVersion`'s `XDocument.Save` / `File.WriteAllText` pattern), `File.Move` the type-matched file, then let Ticket 1 reconcile the view.
- The **file-move ripple**: replace the moved file's solution-tree node and re-home or close its open tab.
- An **invoke-time freshness check** guarding symbol *resolution* against disk drift.

### Out of scope
- Rename-in-comments, rename-in-strings, and cascading rename of **overloads** — all explicitly excluded via `SymbolRenameOptions`.
- Renaming symbols **not** defined in the open solution (framework/NuGet symbols) — no metadata-as-source rewrite.
- Cross-file **undo** — there is none; git is the safety net (see Constraints). No in-app multi-file undo stack.
- `.csproj` edits for the rename itself — SDK-style projects glob their sources, so a file move needs no project-file change. (If a non-SDK project with explicit `<Compile Include>` is ever loaded, that's a separate concern.)
- Inline rename (rename-in-place with live overlay). This is a modal-prompt refactor only.
- Editing the file content in the editor — the IDE is read-only after Ticket 1.

## Relevant Docs & Anchors
Read before coding:
- **`docs/roslyn.md`** — package pins (Roslyn `5.6.0`), cold-start behavior, and the "first use may take a while" cost that applies to the first rename exactly as it does to find-usages.
- **`docs/tickets/complete/2026-07-05 code-view-context-menu.md`** — how the three existing symbol actions were added: the synchronous `Opening` enablement handler, the caret-on-right-click primitive, and the "impossible-only-after-attempt → status bar" pattern. Mirror its structure; do **not** copy verbatim.
- **Code anchors**:
  - `Views/MainWindow.axaml` — the `ContextMenu Opening="OnCodeCtxOpening"` with `CtxSearchItem` / `CtxFindUsagesItem` / `CtxGoToDefItem`. Add the new item here.
  - `Views/MainWindow.axaml.cs` — `OnCodeCtxOpening` (the enablement switch), `OnCtxFindUsagesClick` / `GoToDefinitionAsync` (caret-based invoke reuse), `FindActiveEditor`, the `_codeEditors.StateFor(editor)` colorizer lookup.
  - `Views/CodeSymbolContext.cs` — `CodeSymbolContext.At()` computes `Term` + `SymbolEligible` synchronously (identifier char under caret + `SymbolClassifications.AllowSymbolActions` allowlist). This is the cheap gate the other actions use.
  - `Services/WorkspaceService.cs` — `ResolveSymbolAsync` (private; the reuse target), `FindDocument`, and `SyncDocumentsAsync` (the buffer overlay Ticket 1 removes). Note the `_solution` snapshot discipline in the class doc-comment.
  - `Services/NuGetService.cs` — `SetVersion` is the closed-file direct-disk-write exemplar to mirror for writing changed files.
  - `ViewModels/TabViewModelBase.cs` — `FileId(path)` (`"file:" + full lowercased path`) is the tab identity; `TabId` and `FilePath` are **get-only** (set in the constructor).
  - `ViewModels/MainWindowViewModel.cs` — `Tabs` (`ObservableCollection<TabViewModelBase>`), the `Tabs.FirstOrDefault(t => t.TabId == id)` open-dedup, `CloseTabAsync` (Remove + reset `ActiveTab`), and `Tree` (`ObservableCollection<TreeNode>`, rebuilt from `Solution.LoadAsync`).
  - `Models/SolutionNode.cs` — `TreeNode` with `init`-only `Name` / `Path`; a node cannot be mutated in place after a move.
  - `src/MiniIde.Tests/WorkspaceServiceTests.cs` — the real-on-disk MSBuild fixture harness (xunit + Shouldly, offsets computed from text, per-test temp solution) to mirror for a `RenameService` test.

## Constraints & Gotchas
- **Linchpin API — NEEDS-CONFIRM before implementing**: `Renamer.RenameSymbolAsync(Solution, ISymbol, SymbolRenameOptions, string newName, CancellationToken)` returning a new `Solution` must be public in the restored Roslyn `5.6.0` assemblies (`Microsoft.CodeAnalysis.Features` + `Microsoft.CodeAnalysis.CSharp.Workspaces`, both pinned `5.6.0` in `MiniIde.csproj`). Confirm the exact overload against the restored assembly (or `RenameDocumentAsync` / older `RenameSymbolAsync(Solution, ISymbol, string, OptionSet)` shapes) *before* building on it. If the `SymbolRenameOptions` overload is absent, stop and report — the whole compute step rests on it.
- **Synchronous menu gate vs. async in-solution check**: the menu `Opening` handler is synchronous and the existing `SymbolEligible` only consults the classification allowlist (it lights up on framework symbols too). Confirming "defined in the open solution" needs `SymbolFinder.FindSourceDefinitionAsync` resolving **in source**, which is async and needs the (possibly cold) workspace. Resolve this by gating at **two levels**: menu-time uses the cheap synchronous eligibility (so *Rename…* appears wherever Find usages would); the **authoritative** in-source gate runs at invoke, after resolving the symbol on fresh disk — a framework symbol (no in-source definition) becomes a no-op with a status message, never a dialog. See Open Decisions for whether to attempt any synchronous strengthening.
- **Three stale-state surfaces, none with existing plumbing**: a rename must reconcile (1) the solution **tree** node, (2) any open **tab**, and (3) the cached Roslyn **`WorkspaceService._solution` snapshot** — `FindDocument` matches on the *old* `FilePath` case-insensitively and won't see the moved/renamed file until the workspace is refreshed. Don't forget (3): a second rename resolving against a snapshot that still holds the pre-move paths will misbehave.
- **No in-place identity mutation**: `TreeNode.Name`/`Path` are `init`-only and the node has no `INotifyPropertyChanged`; `TabViewModelBase.TabId`/`FilePath` are get-only (set once in the constructor, along with `Header`/`Mode`). After `File.Move`, the moved file's tree node must be **replaced** in its parent's `Children` (and `TreeNode` has no parent back-pointer, so finding the parent means walking from the `Tree` roots), and its open tab (keyed `file:<oldpath>`) must be **closed and reopened** at the new path (or the tab collection rebuilt) — neither can be re-homed by assignment. Prefer routing all of this through Ticket 1's disk-reconcile so this ticket doesn't hand-roll a parallel refresh. Note: a **case-only** rename leaves `FileId` (case-folded) unchanged, so tab dedup identity is stable in that edge case.
- **Cold MSBuild on first rename**: the first rename pays the same one-time workspace-load cost as the first find-usages. Reuse the existing "Loading workspace (first use may take a while)…" status idiom from `MainWindowViewModel.GoToDefinitionAsync`.
- **No cross-file undo → git is the safety net**: a solution-wide rename touches many files with no in-app undo. Consider recommending (or checking for) a clean working tree before applying, and/or naming git as the recovery path in the confirmation/status copy. Do not build an undo stack.
- **Fresh disk, not overlay**: every resolution and the refactor itself must run on Ticket 1's fresh-disk snapshot. Do not resurrect `SyncDocumentsAsync`-style buffer overlay for the rename path.

## Open Decisions
Defer to the implementer (raise with the user only if genuinely blocking):
1. **Dialog UX** — exact layout/copy of the new-name modal (label, OK/Cancel, inline validation vs. disabled-OK, where to note "git is your undo"). Default: minimal single-field prompt, OK disabled until the name is a valid, changed, non-empty C# identifier.
2. **Conflict handling: block vs. warn** — when Roslyn returns conflict annotations (new name collides with an existing member), block the apply outright, or warn-and-let-proceed. Default: block, surface the conflicting location(s) in the status bar; revisit if too strict.
3. **Tree-refresh + moved-tab mechanics** — whether to drive the node-replace / tab-reopen purely through Ticket 1's reconcile, or nudge it explicitly for the moved path. Default: lean on Ticket 1's reconcile; add a targeted nudge only if the reconcile doesn't observe an external `File.Move` promptly.
4. **Synchronous gate strengthening** — whether to attempt any cheap synchronous narrowing at menu-time (e.g. excluding obvious framework classifications) beyond the shared `SymbolEligible`, or rely wholly on the invoke-time in-source gate. Default: rely on the invoke-time gate; keep the menu gate identical to Find usages.
5. **`SymbolRenameOptions` shape** — exact flags for "code references + file rename on type match, no comments/strings/overloads". Default: `RenameFile = true`, all of `RenameInComments`/`RenameInStrings`/`RenameOverloads` false; confirm field names against the restored `5.6.0` type.

## Acceptance Criteria
- [ ] The code-editor context menu contains a **Rename…** item alongside Search / Find usages / Go to definition; it is enabled under the same synchronous condition as Find usages (a resolvable identifier under the caret in a C# document with a solution open) and disabled otherwise.
- [ ] Invoking Rename on a symbol **not** defined in the open solution (e.g. a framework type) performs **no** rename and reports why via the status bar — no dialog is shown or, if shown, no changes are written.
- [ ] Invoking Rename on an in-solution symbol opens a modal new-name prompt. A blank name, the unchanged current name, or a string that is not a valid C# identifier results in **no change** to any file.
- [ ] A valid new name updates **every** code reference to the symbol across the solution (verified by a `RenameService` unit test asserting the changed-document set against a real on-disk fixture), writing the results to disk.
- [ ] Renaming a type whose file name matches the type name (`Foo` in `Foo.cs`) renames the file to `<NewName>.cs` on disk via `File.Move`; the pre-move file no longer exists and the new file does.
- [ ] After a file rename, the solution tree shows the file under its new name and any tab that was open on the old path is either showing the new path or closed — no tab remains keyed to the vanished `file:<oldpath>`.
- [ ] Comments, string literals, and overloaded members are **not** renamed (asserted by a fixture whose symbol name also appears in a comment/string and as an overload).
- [ ] When the new name produces a Roslyn conflict annotation, the chosen policy (block or warn, per Open Decisions) is applied and the conflict is surfaced to the user rather than silently written.
- [ ] Symbol resolution for rename runs against the fresh-disk snapshot; when the clicked file's view text differs from disk, the rename does not proceed against a guessed offset (see freshness step).

## Implementation

### 1. Confirm the Roslyn rename API
Before any code: restore and inspect the `5.6.0` `Microsoft.CodeAnalysis.Features` / `Microsoft.CodeAnalysis.CSharp.Workspaces` assemblies and confirm `Renamer.RenameSymbolAsync(Solution, ISymbol, SymbolRenameOptions, string, CancellationToken)` and the `SymbolRenameOptions` field names. Record the confirmed signature in the `RenameService` doc-comment. If the overload is missing, halt and report — the rest of the ticket depends on it.

### 2. Surface symbol resolution on `WorkspaceService`
Expose the "symbol under the caret" resolution that `ResolveSymbolAsync` performs today (currently private, consumed by `GoToDefinitionAsync` / `FindReferencesAsync`) so the rename path resolves identically — same `FindDocument` + `GetSymbolInfo`/`GetDeclaredSymbol` logic, against the same `_solution` snapshot. Prefer a small public method returning the `ISymbol?` (and keep the two existing callers on it) over duplicating the resolution. Keep Roslyn types inside the service where practical; the rename service is a sibling in `Services/` and may share the `ISymbol`.

### 3. Build the `RenameService`
New `Services/RenameService.cs`. Given the fresh-disk `Solution`, the resolved `ISymbol`, and a new name: gate on `SymbolFinder.FindSourceDefinitionAsync` resolving **in source** (else return an "not defined in this solution" outcome, no changes); call `Renamer.RenameSymbolAsync` with `SymbolRenameOptions` set for code references + file-rename-on-type-match and comments/strings/overloads off; diff the returned `Solution` against the input snapshot to produce the set of changed documents (path + new text) and any file rename (old path → new path). Collect Roslyn **conflict annotations** from the result and return them as part of the outcome. Return a plain result record (changed files, optional move, conflicts) — keep Roslyn types from leaking to the view. Follow CLAUDE.md defensive style: null-check the symbol, the definition, and the diff; never assume a document changed.

### 4. The first custom modal — new-name prompt
Add a small modal `Window` (e.g. `Views/RenameDialog.axaml` + code-behind, or an equivalent Avalonia dialog) shown via `ShowDialog` over `MainWindow`. Single text field pre-filled with the current name, OK/Cancel. Validate: non-empty, changed from the current name, and a valid C# identifier (use Roslyn's `SyntaxFacts.IsValidIdentifier`). Blank / unchanged / invalid → dialog yields "no rename" and the caller no-ops. This is the app's first `Window` besides `MainWindow`; keep it minimal and self-contained, matching the app's dark styling.

### 5. Wire the menu item and invoke path
In `Views/MainWindow.axaml`, add the **Rename…** `MenuItem` (named, e.g. `CtxRenameItem`) after Go to definition. In `OnCodeCtxOpening`, enable it under the same `context.SymbolEligible` + solution-open condition as `CtxFindUsagesItem`. Add a click handler mirroring `OnCtxFindUsagesClick` / `GoToDefinitionAsync`: resolve the active editor + caret via `FindActiveEditor`, then hand off to a `MainWindowViewModel` rename method (like `GoToDefinitionAsync`/`FindReferencesAsync`, owning the whole flow and the status-bar reporting). Reuse the "Loading workspace (first use may take a while)…" status idiom for the cold-load case.

### 6. Invoke-time freshness check (guards resolution, not just the write)
The right-click landed in a possibly-stale view; the refactor runs on fresh disk, so the clicked offset may point at a different symbol — or nothing — on disk. In the ViewModel rename flow, before resolving: obtain the fresh-disk snapshot (Ticket 1) and compare the clicked file's current view text to its disk text. If they match (common case), the caret offset is valid → resolve and proceed. If they differ, do **not** guess: trigger Ticket 1's view reconcile for that file and report a status asking the user to re-invoke Rename at the (now-refreshed) token. The symbol is resolved against disk truth, not the stale buffer.

### 7. Apply to disk
On a valid, conflict-clear (or user-accepted) result: write each changed document's new text to disk directly, mirroring `NuGetService.SetVersion`'s closed-file write (`File.WriteAllText`/equivalent; no editor round-trip). For the type-matched file, `File.Move` old→new. Order the operations so no write targets a path that a move is about to invalidate. No `.csproj` edit (SDK glob). Do not call `MSBuildWorkspace.TryApplyChanges` (the WorkspaceService doc-comment explains why disk-persistence goes through explicit writes here).

### 8. File-move ripple — tree node and open tab
After the `File.Move`, the moved path must propagate to the UI. Because `TreeNode.Name`/`Path` are `init`-only and `TabViewModelBase.TabId`/`FilePath` are get-only, neither can be mutated in place:
- **Tree**: replace the moved file's `TreeNode` in its parent's `Children` (or let Ticket 1's reconcile rebuild the affected subtree).
- **Open tab**: any tab whose `TabId == FileId(oldPath)` must be closed (`CloseTabAsync`) and, if it was open, reopened at the new path — or the reconcile re-homes it. Ensure no tab is left keyed to the vanished path.
- **Roslyn snapshot**: ensure the cached `WorkspaceService._solution` is refreshed for the moved path (its `FindDocument` still matches the old `FilePath`), so a subsequent rename resolves against post-move disk truth rather than a stale snapshot.
Prefer driving all of this through Ticket 1's disk-reconcile machinery; only add a targeted nudge for the moved path if the reconcile doesn't pick up the external move promptly.

### 9. Conflicts and safety copy
When the result carries conflict annotations, apply the chosen policy (Open Decision 2) — default block, surfacing the colliding location(s) in the status bar, writing nothing. Since there is no cross-file undo, include git as the recovery path in the user-facing copy (dialog note and/or status), and consider surfacing a hint when the working tree is dirty before a large rename.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes.
- [ ] **`RenameService` unit test** (mirror `WorkspaceServiceTests`: real temp solution on disk, xunit + Shouldly, offsets computed from fixture text). Rename a symbol referenced across two files; assert the changed-document set and new texts; assert the fixture's occurrences inside a comment, a string literal, and an overloaded member are **untouched**.
- [ ] **File-rename test**: fixture type `Foo` in `Foo.cs`; rename to `Bar`; assert `Foo.cs` is gone, `Bar.cs` exists with updated content, and the outcome reports the move.
- [ ] **Not-in-solution test**: resolve a framework symbol (e.g. `string`/`Console`) and assert the service returns the "no in-source definition" outcome with zero changed files.
- [ ] **Conflict test**: rename to a name that collides with an existing member; assert conflict annotations are reported and (per policy) nothing is written.
- [ ] **Manual — happy path**: open a solution, right-click an in-solution symbol, confirm *Rename…* is enabled; rename it; confirm every usage updates on disk, the tree/tab reflect any file rename, and the status bar reports success.
- [ ] **Manual — invalid input**: confirm blank / unchanged / non-identifier names are rejected by the dialog with no file changes.
- [ ] **Manual — framework symbol**: right-click a framework symbol; confirm *Rename…* either doesn't apply or reports "only symbols defined in this solution can be renamed" with no changes.
- [ ] **Manual — freshness drift**: with a file changed on disk out from under a stale view, invoke Rename; confirm it reconciles and asks for re-invoke rather than renaming the wrong symbol.
- [ ] **Regression**: Search / Find usages / Go to definition still work and their enablement is unchanged.
