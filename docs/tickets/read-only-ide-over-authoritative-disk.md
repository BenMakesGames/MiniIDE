# Read-only IDE over Authoritative Disk (the "no hand-typed edits" law)

## Context
**Current behavior**: The code editor (`EditorTabViewModel`'s `ae:TextEditor` template) is writable. Typing dirties the tab (`" *"` in the header), Ctrl+S writes the buffer to disk, closing a dirty tab silently saves it, and the Roslyn snapshot is kept correct by overlaying the *editor buffers* onto it before every semantic query. The workspace snapshot is built once at first use and then never refreshed: `WorkspaceService.EnsureLoadedAsync` early-returns while `_solution` is non-null, and "Reload solution" only rebuilds the tree/project list — so after an external tool changes a file, go-to-definition/find-refs/Problems resolve against stale text until the process restarts.

**New behavior**: The IDE becomes a read-only window onto an authoritative disk that external tools (the agentic AI, CLI git, etc.) own and mutate. The code editor is `IsReadOnly`; there is no hand typing, no dirty state, no save. In exchange, the view now *reflects* disk: on window focus and before every operation the code snapshot and any open tabs are reconciled against current file contents, so the window is never frozen on stale text. Reconciliation is strictly one-directional (disk → view) — the view can never hold an edit that disk doesn't, so there is no conflict UX. Operation-driven writes (NuGet add/remove today, refactors later) are unaffected; the law removes only *hand* typing.

**Deliberately accepted consequence**: fixing a one-character typo now requires the agent or an external editor. This is intended, not an oversight.

## Prerequisites
None. Builds directly on:
- `docs/tickets/complete/2026-07-11 unify-tab-editor-binding.md` — introduced the `DocumentTabViewModel : TabViewModelBase` intermediate, the `TabEditorBinder`, and the two disjoint per-kind editor registries this ticket touches.

## Scope
### In scope
- **View markup**: make the code editor read-only (`Views/MainWindow.axaml`).
- **Subtractive VM/code-behind cleanup**: delete the entire save/dirty apparatus — Ctrl+S, `SaveActiveAsync`, `IsDirty`, `SaveCommand`, abstract/overridden `SaveAsync`, `OnIsDirtyChanged`, the `" *"` header marker, the save-on-close.
- **Disk-reflection**: repurpose the existing `WithDocumentText` overlay machinery from "reflect the editor buffer" to "reflect disk"; add focus-time and pre-operation reconciliation; reload open tabs from disk when drifted; content-hash drift detection; fix the load-once staleness bug and split cheap content drift from expensive structural drift.
- **Tests**: re-found `WorkspaceServiceTests` on the fresh-disk premise.
- **Docs**: record the law in `README.md`.

### Out of scope
- **The safe-rename feature** — owned by a sibling ticket. Do not touch it.
- **Any write path other than deletion of the hand-edit path.** NuGet add/remove and other operation-driven writes stay exactly as they are.
- **A file-system watcher / push notifications.** Reconciliation is pull-based (focus + pre-operation). A `FileSystemWatcher` is a possible future optimization, explicitly not this ticket.
- **Conflict resolution / merge UX.** Impossible by construction under a read-only view; do not add any.
- **A view-layer (Avalonia.Headless) test project.** Still none; this ticket does not create one.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/roslyn.md §Cold-start strategy` / `§.slnx` — the split-workspace model and the cheap `Microsoft.VisualStudio.SolutionPersistence` metadata parse (lists projects without MSBuild eval) that a structural-drift check can lean on.
  - `docs/CLAUDE.md` — one topic per file, fragments over prose (for the README/doc edits).
- **Related tickets** (read for context, not structure):
  - `docs/tickets/complete/2026-07-11 unify-tab-editor-binding.md` — `TabEditorBinder`, the `DocumentTabViewModel` base, and how the code editor's `Document` is assigned imperatively.
- **Code anchors** (verify against source; symbols, not line numbers):
  - `Views/MainWindow.axaml` — the `DataTemplate DataType="vm:EditorTabViewModel"` `ae:TextEditor` (no `IsReadOnly` yet). The sibling `vm:OutputTabViewModel` template already carries `IsReadOnly="True"` — copy that.
  - `Views/MainWindow.axaml.cs` — `OnGlobalKeyDown` (the `Ctrl+S` branch) and `SaveActiveAsync` (**both live in the code-behind, not the VM**); the `MainWindow` constructor (where `KeyDown += OnGlobalKeyDown` is wired — the reconcile-on-focus hook goes here); `OnReloadSolutionClick`.
  - `ViewModels/TabViewModelBase.cs` — `Header`, `IsDirty` (`[ObservableProperty] private bool _isDirty`), `SaveCommand`, abstract `SaveAsync`, `OnIsDirtyChanged`.
  - `ViewModels/EditorTabViewModel.cs` — the `Document.TextChanged += (_, _) => IsDirty = true;` in the ctor and the `SaveAsync` override that does `File.WriteAllTextAsync`.
  - `ViewModels/OutputTabViewModel.cs`, `ViewModels/ImageTabViewModel.cs` — the no-op `SaveAsync() => Task.CompletedTask` overrides. (`OutputTabViewModel.Header` is a fixed string override with no dirty marker — leave it.)
  - `ViewModels/MainWindowViewModel.cs` — `EnsureWorkspaceReadyAsync` (the pre-op funnel every semantic query passes through), `UnsavedBuffers()`, `CloseTabAsync`, the `WorkspaceService` construction in the ctor.
  - `Services/WorkspaceService.cs` — `EnsureLoadedAsync` (the `if (_solution is not null) return;` early-out), `SyncDocumentsAsync` (the `WithDocumentText` overlay + `ContentEquals` skip), `FindDocument`.
  - `Views/TabEditorBinder.cs` — the code-tab `Document.TextChanged` handler (the debounced re-highlight; **keep it** — see Constraints).

## Constraints & Gotchas
- **Two separate `Document.TextChanged` subscriptions exist, and they are not the same thing.** One in `EditorTabViewModel`'s ctor sets `IsDirty` (**delete this one** — dirty is gone). One in `TabEditorBinder`'s code-tab wiring debounces and re-runs the Roslyn colorizer (**keep this one**). Under the law it loses its "the user typed" meaning but keeps its "the document changed, re-render" meaning — which is now what fires when a tab is reloaded from disk. Setting `Document.Text` on a reload must still trip the re-highlight.
- **Reconciliation is unidirectional (disk → view) and must never write disk.** `SyncDocumentsAsync`'s no-write discipline (`WithDocumentText` forks the immutable snapshot; never `TryApplyChanges`) is now load-bearing for a *different* reason: a read-only view has no edits to push back, so any disk write would be a bug.
- **Fingerprint drift by CONTENT HASH, never mtime.** Operation-driven writes and same-size overwrites make mtime unreliable in both directions (an operation-write can bump mtime without a meaningful content change; a same-size overwrite changes content the file length can't reveal). Only a content hash distinguishes "actually changed" from "touched." `SyncDocumentsAsync` already compares by content (`ContentEquals`); do not regress that to an mtime check when adding the open-tab reload path.
- **The UI-thread-affinity note on the old overlay flips into a simplification.** `EnsureWorkspaceReadyAsync` currently snapshots `TextDocument.Text` *before the first await* because AvaloniaEdit's document text is thread-affine. Once the overlay source is disk (`File.ReadAllText`), the read is no longer thread-affine and may move off the UI thread / into the service. Reloading an open tab's `Document`, however, is still a UI-thread mutation — marshal it (mirror how `OutputTabViewModel.Append` uses `Dispatcher.UIThread`).
- **Respect GTFO #2 ("no scanning the entire solution on startup").** The reconcile is a focus-time / pre-op refresh, not a startup scan — keep it that way. Do not add solution-wide eager reads to the startup path.
- **AvaloniaEdit read-only preserves selection, copy, F12/Shift+F12, and the context menu.** `IsReadOnly="True"` blocks only text mutation; the code editor's navigation and menu affordances must all still work (this is exactly how the Output pane already behaves).
- **Build lock**: a running MiniIde instance locks the output DLL; build to a temp `-o` dir. Expect the pre-existing warnings (CS0618 `Workspace.WorkspaceFailed`, IL3000 in `SyntaxHighlightService`, AVLN5001 `TextBox.Watermark`); introduce no new ones.

## Open Decisions
1. **Reconcile scope / cost** — how much to re-read per focus/op. Default: reconcile the **open editor tabs** (reload drifted ones and overlay their fresh text into the snapshot) plus whatever the pending operation touches; do not re-read the whole solution. Content-hash the compared files so an unchanged file costs at most a read, never a needless `WithDocumentText`. A `FileSystemWatcher`-driven incremental cache is a later optimization — leave it out. Implementer may tune, but must keep content-hash-not-mtime and must not violate GTFO #2.
2. **Structural-drift detection mechanism** — how to notice files/projects added or removed (→ full MSBuild reload) vs. mere text edits (→ `WithDocumentText`). Default: compare the current solution's project/document manifest (the cheap `SolutionPersistence` parse from `docs/roslyn.md`, plus each project's document set) against the loaded snapshot; a changed manifest is structural. Implementer's call on the exact comparison.
3. **Where the reconcile method lives** — a new `WorkspaceService` method (e.g. `ReconcileWithDiskAsync`) that reads disk itself, vs. keeping the `(Path, Text)` buffer-passing shape of `SyncDocumentsAsync` and feeding it disk text from the VM. Default: move the disk read into the service so the overlay source is unambiguously disk; repurpose `SyncDocumentsAsync` rather than adding a parallel path. Implementer's call.
4. **Tab-reload granularity** — replace the whole `Document.Text`, vs. a minimal diff. Default: replace whole text (simplest; the editor is read-only so there is no caret/edit to preserve carefully). Accept that scroll/caret may reset on reload.

## Acceptance Criteria
- [ ] The code editor is read-only: attempting to type or paste into an open `.cs`/`.csproj`/`.json` tab changes nothing on screen and nothing on disk. Selection, copy, right-click context menu, F12 (go-to-def) and Shift+F12 (find-refs) still work.
- [ ] No `IsDirty`, `SaveCommand`, `SaveAsync`, `OnIsDirtyChanged`, `SaveActiveAsync`, or Ctrl+S save binding remains anywhere in `src/` (grep-clean). `TabViewModelBase` no longer declares an abstract `SaveAsync`; no subclass overrides it.
- [ ] A tab's `Header` is exactly its file name — no `" *"` dirty suffix is ever appended (there is no code path that could).
- [ ] Closing a tab never writes to disk.
- [ ] After an external tool edits a file that is open in a tab, focusing the MiniIde window shows the new content in that tab, and a subsequent go-to-definition/find-references/Problems refresh resolves against the new content — with no manual "reload" action.
- [ ] After an external tool adds or removes a `.cs` file or a project, the next operation that needs the workspace reflects the change (the new file's symbols resolve; a removed file's don't) — i.e. structural drift triggers a real reload, not just an overlay.
- [ ] Reconciliation never writes to disk (`SyncDocumentsAsync`/its successor still forks the snapshot via `WithDocumentText`; no `TryApplyChanges`, no `File.Write*`).
- [ ] Drift is decided by file content, not modification time: an operation-write that leaves a file's content unchanged does not force a tab reload or a `WithDocumentText`; a same-size content change does.
- [ ] `WorkspaceServiceTests` no longer asserts the unsaved-buffer premise; its go-to-def/no-op/ignored-file cases are re-founded on "resolves against fresh disk / reflects an external edit."
- [ ] `README.md` records the "no hand-typed edits / read-only window onto disk" law alongside the existing "no custom window chrome" entry.

## Implementation

### 1. Make the code editor read-only
In `Views/MainWindow.axaml`, add `IsReadOnly="True"` to the `ae:TextEditor` in the `DataTemplate DataType="vm:EditorTabViewModel"`, matching the sibling `OutputTabViewModel` template that already has it. Nothing else in that template changes — the colorizer, caret tracking, and context menu stay.

### 2. Remove the Ctrl+S save path (code-behind)
In `Views/MainWindow.axaml.cs`, delete the `else if (ctrl && e.Key == Key.S) { … await SaveActiveAsync(); }` branch from `OnGlobalKeyDown`, and delete the `SaveActiveAsync` method. Leave the other `OnGlobalKeyDown` branches (Ctrl+O, Ctrl+Shift+F, F5, F12, Shift+F12) intact.

### 3. Strip the dirty/save apparatus from `TabViewModelBase`
Remove `IsDirty` (the `[ObservableProperty] private bool _isDirty` field), `SaveCommand`, the `SaveCommand = new AsyncRelayCommand(SaveAsync)` ctor line, the abstract `SaveAsync`, and `OnIsDirtyChanged`. Simplify `Header` to just `Path.GetFileName(FilePath)` (drop the `+ (IsDirty ? " *" : "")`). `CloseAsync`/`RequestClose`/`TabId`/`FilePath`/`FileId`/`CreateForFile` are unaffected.

### 4. Drop the write path from `EditorTabViewModel`
Remove the `Document.TextChanged += (_, _) => IsDirty = true;` line from the ctor and delete the `SaveAsync` override (the `File.WriteAllTextAsync` one). The ctor's `Mode` assignment stays. Note the *other* `Document.TextChanged` subscription — the re-highlight one in `TabEditorBinder` — is untouched and now carries the reload-triggered re-render.

### 5. Remove the no-op `SaveAsync` overrides
Delete `SaveAsync() => Task.CompletedTask;` from both `OutputTabViewModel` and `ImageTabViewModel`. They compiled only to satisfy the now-deleted abstract member.

### 6. Drop save-on-close
In `MainWindowViewModel.CloseTabAsync`, remove the `if (tab.IsDirty) await tab.SaveAsync();` line. The live-run-stop logic above it and the tab-removal below it stay.

### 7. Repurpose the overlay source from editor buffers to disk
The `WithDocumentText` fork-in-place machinery is kept, but its *source* flips from the (now impossible) editor buffer to disk. Per Open Decision 3, prefer moving the disk read into `WorkspaceService`: repurpose `SyncDocumentsAsync` (or a successor `ReconcileWithDiskAsync`) so that, for the documents it reconciles, it reads current file text from disk, content-compares against the snapshot (keep the `ContentEquals` skip — it is already content-based and must not become an mtime check), and `WithDocumentText`-forks `_solution` for the drifted ones. In `MainWindowViewModel`, `EnsureWorkspaceReadyAsync` stops collecting `UnsavedBuffers()` (there are none) and instead drives this disk reconcile; repurpose or remove `UnsavedBuffers()` accordingly. Update the now-stale doc comments on `EnsureWorkspaceReadyAsync`/`SyncDocumentsAsync` (they describe overlaying "unsaved editor buffers"); the thread-affinity caveat that justified snapshotting before the first await no longer applies to a disk read (see Constraints).

### 8. Fix the load-once staleness bug; split content drift from structural drift
`EnsureLoadedAsync` may keep its build-once early-return (constructing the `MSBuildWorkspace` is the expensive cold start we don't want to repeat needlessly), but it can no longer be the *only* refresh path. Add a reconcile that runs on every `EnsureWorkspaceReadyAsync`:
- **Content drift** (same set of files/projects, changed text) → cheap `WithDocumentText` overlay (step 7).
- **Structural drift** (files or projects added/removed, project file changed) → a real reload: tear down and rebuild `_solution` (re-`OpenSolutionAsync`), since `WithDocumentText` cannot add/remove documents. Detect it per Open Decision 2.
Wire `OnReloadSolutionClick` (code-behind) so "Reload solution" also refreshes the code snapshot — today it only reloads the tree. Route it through the same reload path so a user-invoked reload picks up structural changes too.

### 9. Refresh on window focus and reload drifted open tabs
Add a reconcile trigger on window activation: in the `MainWindow` constructor, subscribe the window's `Activated` event (alongside the existing `KeyDown += OnGlobalKeyDown`) to a handler that (a) reconciles the workspace against disk (step 7/8) and (b) reloads any open editor tab whose file content has drifted. Reloading a tab means re-reading the file and replacing the tab's `Document.Text` (content-hash the comparison so an unchanged file is a no-op); because the editor is read-only there is no user edit to clobber. Marshal the `Document` mutation onto the UI thread. The `TabEditorBinder` re-highlight fires off the resulting `TextChanged`, so no extra redraw wiring is needed. The same reconcile also runs before operations via `EnsureWorkspaceReadyAsync` (already the funnel) — focus-time is the additional trigger that keeps the *view* fresh even when no operation is pending.

### 10. Re-found `WorkspaceServiceTests` on the fresh-disk premise
`src/MiniIde.Tests/WorkspaceServiceTests.cs` is currently built on the unsaved-buffer model; the law invalidates that premise. Re-found the affected cases so they assert disk-truth instead of buffer-overlay:
- `GoToDefinition_ResolvesAgainstTheUnsavedBuffer_NotTheFileOnDisk` → recast as "resolves against a *fresh external disk edit*": write the edited text to the file on disk, reconcile, and assert go-to-def lands on the shifted line. (The point flips from "buffer beats disk" to "disk is truth after reconcile.")
- `SyncDocuments_NeverWritesToDisk` → keep the invariant (reconcile/overlay must not write disk) but drive it from the disk-reconcile entry point rather than a passed-in buffer.
- `SyncDocuments_IsANoOpForTextThatAlreadyMatches` → recast around the content-hash skip: reconciling when disk matches the snapshot forks nothing and changes no result.
- `SyncDocuments_IgnoresOpenFilesThatArentPartOfTheSolution` → recast as "a file with no Roslyn document (e.g. a `.md`) is ignored without throwing" under the new disk-driven path.
Keep `GoToDefinition_ResolvesAgainstTheOnDiskTextWhenNothingIsDirty`, `FindReferences_ReturnsNullWhenNoSymbolResolves`, and `FindReferences_FindsTheCallSite` (adjust only if the reconcile API shape changed). If step 8 adds structural-vs-content-drift logic, add a case covering an externally-added file becoming resolvable after reconcile. The existing fixture (real on-disk `Lib.slnx`/`Lib.csproj`/`Code.cs` in a temp dir, MSBuildLocator, `EnsureLoadedAsync`) is the right harness — edit files on disk to simulate the external tool.

### 11. Record the law in `README.md`
Add the "no hand-typed edits" law next to the existing "custom window chrome" entry under `### my GTFOs` (a numbered list, not a table — match that format). Phrase it as the design law: the editor is a read-only window onto an authoritative disk; external tools do the writing; operation-driven writes (NuGet, refactors) remain. Keep it to a fragment or two per `docs/CLAUDE.md` style. (If a `feature | status` must-haves row reads better to you, that's fine, but the GTFO list is where "no custom window chrome" lives, so default there.)

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with 0 errors and only the three pre-existing warnings.
- [ ] `dotnet test MiniIde.slnx` passes, including the re-founded `WorkspaceServiceTests`.
- [ ] Grep `src/` for `IsDirty`, `SaveCommand`, `SaveAsync`, `SaveActiveAsync` → no production references remain (test-name substrings aside).
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`; open a `.cs` file. Try to type / paste → nothing changes on screen or on disk. Select text and Ctrl+C → copy works. Right-click → context menu opens; F12 on a symbol navigates; Shift+F12 lists references.
- [ ] Confirm no tab header ever shows a `" *"`; press Ctrl+S → nothing happens (no save, no error).
- [ ] **External edit, open file**: with a `.cs` file open in MiniIde, edit it with an external editor (or the agent) and save. Click back into the MiniIde window → the tab shows the new content, and syntax highlighting re-runs. Then F12 on a symbol whose position moved → the caret lands correctly (snapshot reconciled).
- [ ] **External edit, unopened file feeding a query**: edit an unopened `.cs` file externally, then F12 into it from an open file → navigation resolves against the new content.
- [ ] **Structural drift**: externally add a new `.cs` file (and/or a project) to the solution, then invoke a workspace operation → the new file's symbols resolve. Remove a file externally → its symbols stop resolving. Also verify "Reload solution" refreshes the code snapshot, not just the tree.
- [ ] **Content-hash, not mtime**: perform an operation-write (e.g. NuGet add) that touches project files but leaves an open source tab's content identical → that tab does not visibly reload/flicker. Overwrite a file with same-length but different content externally → focus reflects the change.
- [ ] Close a tab (including one whose file was externally modified) → nothing is written to disk; the external content is preserved.
- [ ] No exceptions in the status bar or debug output throughout.
