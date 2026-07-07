# Sync Open Editor Buffers Into the Roslyn Workspace Before Symbol Navigation

## Context
**Current behavior**: Go to Definition (F12) and Find References (Shift+F12) resolve against **stale on-disk text**. `WorkspaceService.EnsureLoadedAsync` snapshots the solution from disk once (lazily, on first symbol-nav use) and is never refreshed. `WorkspaceService.UpdateDocumentText(filePath, text)` — the method whose whole purpose is to push a live buffer into the `MSBuildWorkspace` — has **zero callers**. So the moment a file has unsaved edits, the caret `position` (an offset in the live buffer) is applied against the *old* syntax tree: edits above the caret shift every offset (F12 jumps to the wrong token), and a symbol you just typed doesn't exist in the workspace at all (Find usages / Go to definition report "no symbol found").

**New behavior**: F12 / Shift+F12 resolve against the **current editor content**. Immediately after ensuring the workspace is loaded and before resolving, the VM syncs the live text of every open editor tab into the workspace via `UpdateDocumentText`. A symbol typed but not yet saved resolves correctly; a reference added in another open (unsaved) tab is found. No keyboard/menu behavior or status messaging changes otherwise.

## Prerequisites
None. `WorkspaceService.UpdateDocumentText`, `MainWindowViewModel.Tabs`, and `EditorTabViewModel.Document`/`FilePath` all already exist — this ticket wires the existing primitive into the two call sites that need it.

## Scope
### In scope
- `ViewModels/MainWindowViewModel.cs`: a private helper that pushes each open editor tab's current `Document.Text` into `Workspace` via `UpdateDocumentText`, called at the top of both `GoToDefinitionAsync` and `FindReferencesAsync` (after `EnsureLoadedAsync`, before delegating to the workspace).

### Out of scope
- **Push-on-keystroke / `TextChanged` → workspace wiring.** The fix is pull-based (sync on F12/Shift+F12 only). Do not subscribe workspace updates to document edits — it adds per-keystroke cost and a subscription lifecycle for no benefit, since nothing else consumes `_solution`.
- **Syntax highlighting.** Already live — it uses a separate throwaway `AdhocWorkspace` on current text (`SyntaxHighlightService`), not `MSBuildWorkspace`. Leave untouched.
- **Incremental/dirty-only optimization tuning** beyond the default chosen in Open Decisions.
- **Error handling of `EnsureLoadedAsync` failures**, workspace reload on external file changes, or files edited outside the IDE — pre-existing behavior, not this bug.
- Changing `UpdateDocumentText`'s signature or its internal `FindDocument`/`TryApplyChanges` logic.

## Relevant Docs & Anchors
- **Code anchors**:
  - `Services/WorkspaceService.cs` — `UpdateDocumentText(string filePath, string text)` (the unused primitive: early-returns when `_solution`/`_ws` is null, else `FindDocument` → `WithDocumentText` → `TryApplyChanges` → reassigns `_solution`); `EnsureLoadedAsync`; `GoToDefinitionAsync`/`FindReferencesAsync` (the consumers of `_solution` that call `FindDocument` then `GetSemanticModelAsync`/`GetSyntaxRootAsync` at `position`).
  - `ViewModels/MainWindowViewModel.cs` — `GoToDefinitionAsync(file, position)` and `FindReferencesAsync(file, position)` (each sets `Status`, `await Workspace.EnsureLoadedAsync(Solution.SolutionPath)`, then delegates); the `Tabs` collection (`ObservableCollection<TabViewModelBase>`) and `Workspace` property.
  - `ViewModels/EditorTabViewModel.cs` — `Document` (`AvaloniaEdit.Document.TextDocument`; `.Text` is the live buffer) and `FilePath` (absolute; on `TabViewModelBase`). `ImageTabViewModel` has no `Document` — filter to editor tabs.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-05 code-view-context-menu.md` — describes how F12/Shift+F12 and the editor context menu all funnel through these two VM wrappers; its Learnings note `FindReferencesAsync` returning `null` = "no symbol at position". Relevant because the whole point of this fix is that the token *does* now resolve when it only exists in the unsaved buffer.
- **Design docs**: `docs/roslyn.md` — `§Cold-start strategy` (why the `MSBuildWorkspace` is deferred/loaded once) and `§Classifier standalone` (why highlighting is already live and out of scope).

## Constraints & Gotchas
- **Order matters**: sync must run *after* `EnsureLoadedAsync` (so `_solution` is non-null — `UpdateDocumentText` no-ops when it's null) and *before* the `Workspace.GoToDefinitionAsync`/`FindReferencesAsync` call. Placing it before `EnsureLoadedAsync` would silently do nothing.
- **This also fixes edits made *before* first load.** Because the sync pulls current buffer text on every invocation, a file edited before the workspace ever loaded (where an eager push would have no-op'd) is still correct: `EnsureLoadedAsync` reads disk, then the sync overwrites with the live buffer.
- **Files not in any project** (e.g. a `.md` opened in a tab) resolve to `null` in `FindDocument`, so `UpdateDocumentText` no-ops for them — harmless. No pre-filtering needed.
- **Sequential applies chain correctly.** `UpdateDocumentText` reads `_solution`, applies `WithDocumentText`, and reassigns `_solution` on success — so looping over multiple tabs accumulates edits. Each open path is unique (tabs dedupe by path in `OpenFileAsync`), so no doc is synced twice per pass.
- **Not a per-keystroke path.** This runs only on F12/Shift+F12, over the handful of open tabs, so the `FindDocument` scan (projects × documents) per tab is acceptable. Do not "optimize" by caching across invocations — correctness (always current buffer) beats micro-perf here.
- **Build lock**: a running MiniIde instance locks the output DLL; verify compilation with a temp `-o` build. Expect only the pre-existing `CS0618` (`Workspace.WorkspaceFailed` obsolete) warning — introduce no new warnings.

## Open Decisions
1. **Which buffers to sync** — all open editor tabs vs. only the active file. Default: **all open editor tabs**, because `FindReferencesAsync` is inherently cross-file and an unsaved reference can live in another open tab; the cost is trivial on a manual keypress. (Active-file-only would fix Go-to-def and same-file usages but miss cross-file dirty references.)
2. **Dirty-only vs. unconditional** — skip tabs where `IsDirty == false` (buffer already matches disk) vs. sync every open editor tab. Default: **unconditional** (simplest, always correct; syncing a clean buffer is idempotent). Dirty-only is a valid micro-optimization if the implementer prefers it — behavior is identical.
3. **Helper placement** — a private method on `MainWindowViewModel` (e.g. `SyncOpenBuffersToWorkspace()`) called by both wrappers vs. inlining the loop twice. Default: a single private helper (both wrappers need the identical pass).

## Acceptance Criteria
- [ ] `WorkspaceService.UpdateDocumentText` has at least one caller (it is no longer dead code).
- [ ] Both `MainWindowViewModel.GoToDefinitionAsync` and `FindReferencesAsync` push current open-editor-buffer text into the workspace after `EnsureLoadedAsync` and before calling the corresponding `Workspace` method.
- [ ] With unsaved edits in the active file, F12 and Shift+F12 resolve tokens by the buffer's current offsets/content, not the on-disk snapshot (e.g. a symbol declared but unsaved is found; a symbol whose offset shifted due to unsaved edits above it still resolves to the correct token).
- [ ] A reference added in one open editor tab and left unsaved is included in Find References results for a symbol declared in another tab.
- [ ] No document-edit / `TextChanged` subscription drives workspace updates (the sync happens only on the symbol-navigation paths).
- [ ] F12 / Shift+F12 / Ctrl+Shift+F behavior is otherwise unchanged (status messages, navigation, the "no symbol found" path for genuinely unresolved tokens).

## Implementation

### 1. Add a buffer-sync helper on the VM
In `ViewModels/MainWindowViewModel.cs`, add a private method that iterates `Tabs`, and for each `EditorTabViewModel` calls `Workspace.UpdateDocumentText(tab.FilePath, tab.Document.Text)`. Filter to editor tabs (`is EditorTabViewModel` / `OfType<EditorTabViewModel>()`) since `ImageTabViewModel` has no document. Per Open Decisions #1/#2, default to all open editor tabs, unconditionally. Synchronous — `UpdateDocumentText` is not async.

### 2. Call the helper in both symbol-nav wrappers
In the same file, in `GoToDefinitionAsync` and `FindReferencesAsync`, invoke the helper immediately after `await Workspace.EnsureLoadedAsync(Solution.SolutionPath)` and before the `await Workspace.GoToDefinitionAsync(...)` / `FindReferencesAsync(...)` delegation. Leave the surrounding `Status` assignments and the null-return contracts intact.

## Test Plan
- [ ] Compile-check: `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds; only the pre-existing `CS0618` warning.
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`; open a C# file (e.g. `MainWindowViewModel.cs`).
- [ ] **New-symbol repro**: in a class, type a new method `public void Zzz() { }` and a call to it `Zzz();` elsewhere in the same file; do **not** save. Put the caret on the `Zzz()` call and press F12 — it navigates to the new `Zzz` declaration (before the fix: "No definition/symbol found").
- [ ] **Offset-shift repro**: insert several blank lines at the top of a file (unsaved), then F12 on a symbol lower in the file — it lands on the correct declaration, not a location shifted by the stale offsets.
- [ ] **Cross-file dirty reference**: open file A (declares a method `Foo`) and file B (already calls `Foo`). In file B, add another `Foo();` call and leave B unsaved. Shift+F12 on `Foo`'s declaration in A — the results include the new unsaved call site in B.
- [ ] **Regression**: on a saved file with no pending edits, F12/Shift+F12 behave exactly as before; F12 on a framework symbol (e.g. `Any`) still reports no source definition; Shift+F12 on a non-symbol still reports "No symbol found."
- [ ] **Regression**: Ctrl+Shift+F solution search and syntax highlighting are unaffected.
- [ ] No exceptions appear in the Output pane throughout.
