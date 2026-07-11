# Unify the Tab↔TextEditor Binding; Unbind on Detach

## Context
**Current behavior**: `Views/MainWindow.axaml.cs` carries two parallel, near-identical binding mechanisms for the two `ae:TextEditor` `DataTemplate`s in the file-area `TabControl`. `BindEditor` (+ `EditorBinding`, `_bindings`) serves `EditorTabViewModel`; `BindOutputEditor` (+ `OutputBinding`, `_outputBindings`) serves `OutputTabViewModel`. Both run the same state machine — get-or-create per-control state from a `Dictionary<TextEditor, …>`, early-out on `ReferenceEquals(state.CurrentTab, tab)`, detach the previous document's handlers, re-point `editor.Document`, attach the new document's handlers — and differ only in *what* they attach afterwards (colorizer + 200 ms reclassify debounce + caret tracking + right-click caret placement for code tabs; `Changing`/`Changed` tail-follow for output tabs). The `output-as-file-tabs` ticket instructed "mirror `BindEditor`", and the implementation mirrored it by duplication.

Neither dictionary is ever pruned: nothing unbinds on `DetachedFromVisualTree`. Every `TextEditor` the `TabControl` has ever realized stays reachable for the process lifetime, together with its `TextDocument`, its still-subscribed document handlers, and (for code tabs) an undisposed `CancellationTokenSource`. Switching between a code tab and an output tab resolves a *different* `DataTemplate` and therefore realizes a *different* control, so entries accumulate as the user works.

That untrustworthy registry is also why `FindActiveEditor` can't use it: it scans the entire visual tree (`GetVisualDescendants().OfType<TextEditor>()`) and — since output tabs began realizing `TextEditor`s too — carries a bolted-on `DataContext is EditorTabViewModel` type guard plus a three-line comment defending the invariant that a non-null result implies a file-backed tab.

**New behavior**: One binder type, instantiated once per editor kind, owns the bind/rebind/unbind lifecycle; it is extracted out of the window code-behind. Both document-backed tab VMs expose their `TextDocument` through a single shared contract, so the document swap is written once and the two variants supply only their own attach/detach wiring. Binding is torn down on `DetachedFromVisualTree` — the exact inverse of setup — so no control, document, handler, or `CancellationTokenSource` outlives its editor. `FindActiveEditor` then asks the *code-editor* binder's registry instead of walking the visual tree, and the type guard disappears structurally: that registry only ever holds code-tab editors, because the two templates realize disjoint control instances. **No user-visible behavior change** — same highlighting, same debounce, same tail-follow, same shortcuts, same menu enablement.

## Prerequisites
None. Builds directly on:
- `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — introduced the second binder and the `FindActiveEditor` type guard.
- `docs/tickets/complete/2026-07-05 image-preview-tabs.md` — established the polymorphic tab base.

## Scope
### In scope
- **ViewModels**: a shared document-tab contract exposing `TextDocument Document`, adopted by `EditorTabViewModel` and `OutputTabViewModel`. `ImageTabViewModel` stays off it (no document).
- **New file under `Views/`**: the single binder type — per-control binding state, bind-on-attach/DataContextChanged, unbind-on-detach — plus the two per-kind attach/detach wirings.
- **`Views/MainWindow.axaml.cs`**: delete `BindEditor`, `BindOutputEditor`, `EditorBinding`, `OutputBinding`, and both dictionaries; reduce the four attach/DataContextChanged handlers to thin forwarders and add two detach forwarders; re-source `FindActiveEditor` and `ColorizerFor` from the code-editor binder.
- **`Views/MainWindow.axaml`**: `DetachedFromVisualTree` hooks on both `ae:TextEditor` templates.
- **`Views/RoslynColorizer.cs`**: move the existing `RoslynColorizer` class out of `MainWindow.axaml.cs` into its own file, **unchanged** — the binder now owns colorizer lifetime, and leaving the type defined inside the window file it no longer belongs to is gratuitous. Pure file move, no logic change.
- **`docs/avalonia.md`**: two bullets become obsolete on completion (see Implementation step 8).

### Out of scope
- **The rest of the code-behind decomposition** (review §1): the symbol-text helpers (`IsIdentifierChar`, `IdentifierRunAt`, `TermAt`, `IsDeniedClassification`, `Ellipsize`) stay in `MainWindow.axaml.cs`. Only `RoslynColorizer` moves, because this ticket changes who owns it. Everything else is a separate ticket.
- **The three imperative `ContextMenu.Opening` enablement handlers** (`OnSolutionCtxOpening`, `OnCodeCtxOpening`, `OnTabHeaderCtxOpening`) — still parked for the imperative-menu-state ticket. `OnCodeCtxOpening` is touched *only* insofar as it calls `ColorizerFor`/`FindActiveEditor`; its logic is unchanged.
- **Behavioral tuning**: the 200 ms debounce, the immediate initial highlight, the 1.0-px at-bottom epsilon, the 5000-line output cap, and the highlight-mode switch (`RefreshAndRedraw`) are all preserved as-is.
- **`ImageTabViewModel`** and the image `DataTemplate`.
- **A view-layer test project.** None exists; this ticket does not create one (`MiniIde.Tests` is source-scanning only).

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/avalonia.md` — two bullets are the load-bearing context. *"One realized control is shared across all same-`DataType` items in a `TabControl`"* documents exactly the `Dictionary<Control, Binding>` + `ReferenceEquals` + detach-previous pattern this ticket generalizes (and names the one-shot-`HashSet` trap it replaced). *"`DataContext == ActiveTab` is ambiguous once two tab kinds render the same control type"* documents the type-guard workaround this ticket **deletes** — both bullets need rewriting on completion.
  - `docs/avaloniaedit.md` — `TextEditor.Document` is a CLR property, not an `AvaloniaProperty`, so XAML `{Binding Document}` silently no-ops and it **must** be assigned imperatively. That is the entire reason a binder exists; it cannot become a XAML binding. Also: `Changing` fires pre-mutation / `Changed` post (tail-follow captures at-bottom on `Changing`), the `ILogicalScrollable` cast for `Offset`/`Extent`/`Viewport`, and the rule that `editor.SyntaxHighlighting` and a `DocumentColorizingTransformer` must never both be live on one editor.
- **Related tickets** (read the Constraints/Learnings, not the structure):
  - `docs/tickets/complete/2026-07-10 output-as-file-tabs.md` — its Constraints spell out *why* rebinding on `DataContextChanged` is mandatory for same-`DataType` tabs, and the live-run-tab kill-race.
  - `docs/tickets/complete/2026-07-05 output-panel-plain-text.md` — the original tail-follow + 5000-line-cap semantics being preserved.
  - `docs/tickets/complete/2026-07-05 xml-json-syntax-highlighting.md` — the mode switch and the color-bleed hazard between the xshd path and the colorizer path.
- **Code anchors**:
  - `MainWindow.BindEditor` and `MainWindow.BindOutputEditor` — the two copies to collapse; their `EditorBinding` / `OutputBinding` state classes sit beside them.
  - `MainWindow.FindActiveEditor` — the visual-tree scan + type guard to replace; its callers are `OpenHit`, `GoToDefinitionAsync`, `FindRefsAsync`, `TrySearchTermInEditor`, `OnCodeCtxOpening`.
  - `MainWindow.ColorizerFor` — the other reader of `_bindings`.
  - `MainWindow.DebouncedRefreshAsync` / `RefreshAndRedraw` / `OnEditorPointerPressed` — the code-tab wiring the editor variant must carry over.
  - `ViewModels/TabViewModelBase.cs`, `EditorTabViewModel.cs` (has `Document`, `Mode`, `CaretOffset`), `OutputTabViewModel.cs` (has `Document`, `Append`, `Clear`).
  - `Views/MainWindow.axaml` — the two `ae:TextEditor` entries under `TabControl.DataTemplates` carrying `AttachedToVisualTree` / `DataContextChanged`.

## Constraints & Gotchas
- **Keep the two registries separate.** Instantiate the binder once per editor kind; do **not** merge code-tab and output-tab editors into one dictionary. The two `DataTemplate`s realize disjoint `TextEditor` instances, so a per-kind registry means the code-editor registry *structurally* contains only code-tab editors — which is precisely what lets `FindActiveEditor` drop its `DataContext is EditorTabViewModel` guard. Merging the registries would resurrect the ambiguity and the guard.
- **Unbind must be the exact inverse of bind's one-time setup.** Bind currently does control-level setup that is not per-tab: `Options.ConvertTabsToSpaces`/`IndentationSize`, adding the `RoslynColorizer` to `TextArea.TextView.LineTransformers`, subscribing `TextArea.Caret.PositionChanged`, and `AddHandler(PointerPressedEvent, …, Tunnel)`. Unbind must undo all of it, so that an attach → detach → attach on the same control instance re-initializes cleanly instead of double-adding a colorizer (double-colorized text) or double-firing handlers. **The caret `PositionChanged` subscription is currently an anonymous lambda and therefore cannot be unsubscribed — store it** (same for anything else you subscribe).
- **`DebounceCts` must be cancelled *and* disposed** on tab-switch and on unbind. Today the tab-switch path calls `Cancel()` without `Dispose()`.
- **`FindActiveEditor` timing parity.** Callers assume the binder has already run for the newly-active tab. Both the old visual-tree scan and the new registry lookup are updated by the same synchronous `AttachedToVisualTree` / `DataContextChanged` pass that sets the `DataContext` the old code matched on, so parity should hold — but the *brand-new-tab* path is the one to prove: `OpenHit` sets `ActiveTab` via `OpenFileAsync` and then immediately calls `FindActiveEditor` to place the caret (this is Go-to-definition into a file that isn't open yet). Verify it explicitly; see Test Plan.
- **Never run both highlight paths at once.** `RefreshAndRedraw` clears the colorizer when switching to an xshd mode and nulls `SyntaxHighlighting` when switching to C#. Preserve that discipline through the refactor or color from the prior mode bleeds through (`docs/avaloniaedit.md`).
- **Output editors must not get code wiring, and vice versa.** Output tabs are `IsReadOnly` and get only tail-follow; code tabs get colorizer + debounce + caret + right-click caret placement and no tail-follow.
- **`Append` runs on background threads** (the `RunService` stdout/stderr readers). `OutputTabViewModel` already marshals via `Dispatcher.UIThread.Post`; the binder must not add any assumption that document mutation originates on the UI thread.
- **Build lock**: a running MiniIde instance locks the output DLL, so an in-place `dotnet build` can fail at the file-copy step. Build to a temp `-o` dir. Expect the three pre-existing warnings (CS0618 `Workspace.WorkspaceFailed`, IL3000 in `SyntaxHighlightService`, AVLN5001 `TextBox.Watermark`); introduce no new ones.

## Open Decisions
1. **Shared contract shape** — an abstract intermediate `DocumentTabViewModel : TabViewModelBase` exposing `TextDocument Document`, vs. an `IDocumentTab` interface implemented by both VMs. Default: **abstract intermediate class** — a code tab and an output tab genuinely *are* document-backed tabs, and it avoids a paired generic constraint (`where T : TabViewModelBase, IDocumentTab`) on the binder. Implementer's call.
2. **Binder shape** — one concrete class parameterized by an "attach returns its own detach" delegate (`Func<TextEditor, TTab, Action>`, so setup and teardown are written adjacently and can't drift), vs. an abstract base with `OnBind`/`OnUnbind` hooks and two subclasses. Default: **the delegate form** — fewer types, and the two variants are small.
3. **Binder name/location** — e.g. `Views/TabEditorBinder.cs`. Default as written; rename for local consistency if something reads better.
4. **Whether unbind also nulls `editor.Document`.** Default: **no** — the control is being discarded; nulling buys nothing and AvaloniaEdit tolerates neither more nor less.

## Acceptance Criteria
- [ ] `EditorTabViewModel` and `OutputTabViewModel` expose their `TextDocument` through one shared contract (abstract base or interface); `ImageTabViewModel` does not participate in it.
- [ ] Exactly one type implements the get-or-create → `ReferenceEquals` early-out → detach-previous → re-point `Document` → attach-new sequence. `MainWindow.axaml.cs` contains no second copy of it, and `EditorBinding`/`OutputBinding` no longer exist as two state classes with duplicate lifecycles.
- [ ] Both `ae:TextEditor` templates unbind on `DetachedFromVisualTree`. After a detach, the binder holds no state for that control: its document handlers are unsubscribed, its colorizer is removed from `LineTransformers`, its caret/pointer handlers are removed, and any pending debounce `CancellationTokenSource` is cancelled and disposed.
- [ ] `FindActiveEditor` resolves the active code editor from the code-editor binder rather than `GetVisualDescendants()`, and the `DataContext is EditorTabViewModel` type guard (and the comment defending it) are gone — the invariant is enforced by which registry is consulted.
- [ ] `RoslynColorizer` lives in its own file under `Views/`, with no change to its logic.
- [ ] No user-visible behavior change: C#/XML/JSON highlighting and mode switching, the immediate initial highlight, the 200 ms edit debounce, output tail-follow and the 5000-line cap, right-click caret placement, F12 / Shift+F12 / Ctrl+Shift+F, and code context-menu enablement all behave exactly as before.
- [ ] Compiles with no new warnings (temp-`-o` build).

## Implementation

### 1. Introduce the shared document-tab contract
Give the binder one typed thing to talk to. Per Open Decision 1, default to an abstract `DocumentTabViewModel : TabViewModelBase` exposing `TextDocument Document`; have `EditorTabViewModel` (whose `Document` comes from `File.ReadAllText`) and `OutputTabViewModel` (whose `Document` is the streamed buffer, with `Append`/`Clear`/the line cap) derive from it. `ImageTabViewModel` continues to derive from `TabViewModelBase` directly. `TabViewModelBase`'s existing members (`TabId`, `FilePath`, `Header`, `IsDirty`, `SaveAsync`, `CloseAsync`, `FileId`, `CreateForFile`) are unchanged.

### 2. Move `RoslynColorizer` to its own file
Cut the `RoslynColorizer` class out of the bottom of `MainWindow.axaml.cs` into `Views/RoslynColorizer.cs`, verbatim — same `internal` accessibility, same namespace, no logic change. It is about to be owned by the binder rather than the window.

### 3. Create the binder
A single type under `Views/` owning a `Dictionary<TextEditor, …>` of per-control state and exposing three operations:
- **Bind(editor)** — called from both `AttachedToVisualTree` and `DataContextChanged`. Bail unless the editor's `DataContext` is the tab type this binder serves. Get-or-create the per-control state; on first creation run the one-time control setup for this kind. Then early-out if the state's current tab is already this tab (`ReferenceEquals`); otherwise detach the previous tab's document wiring, assign `editor.Document = tab.Document`, and attach the new tab's document wiring.
- **Unbind(editor)** — called from `DetachedFromVisualTree`. Detach the current tab's document wiring, undo the one-time control setup, and drop the dictionary entry. Must be the exact inverse of Bind (see Constraints).
- **A lookup for the active editor** — given the active tab, return the bound `TextEditor` whose current tab is that tab, else null.

Per Open Decision 2, default to expressing the per-kind difference as an "attach returns its own detach" delegate so each variant's setup and teardown sit next to each other and cannot drift apart. The per-control state needs, at minimum: the current tab, the current document, and whatever the variant's teardown closure captures (colorizer, debounce CTS).

### 4. Wire the code-editor variant
Carry over today's `BindEditor` behavior exactly: one-time setup sets `ConvertTabsToSpaces`/`IndentationSize`, constructs a `RoslynColorizer` over `Vm.Highlight.ClassifyAsync`, adds it to `TextArea.TextView.LineTransformers`, subscribes `TextArea.Caret.PositionChanged` to push `editor.CaretOffset` into the tab's `CaretOffset` (**store the handler so it can be removed**), and registers the tunnelling right-button `PointerPressed` handler for caret placement. Per-tab wiring subscribes `Document.TextChanged` to the cancel-and-restart debounce (`DebouncedRefreshAsync`, 200 ms) and kicks off an immediate `RefreshAndRedraw` for the newly-shown document; its teardown unsubscribes `TextChanged` and cancels **and disposes** the pending CTS. The colorizer must remain reachable for `ColorizerFor` (used by `OnCodeCtxOpening` for classification-based menu enablement).

### 5. Wire the output-editor variant
Carry over today's `BindOutputEditor` behavior exactly: no one-time setup beyond what the template already declares; per-tab wiring subscribes `Document.Changing` to capture whether the view was at the bottom (`ILogicalScrollable` cast on `TextArea.TextView`, 1.0-px epsilon) and `Document.Changed` to `ScrollToLine(LineCount)` when it was; teardown unsubscribes both. The at-bottom flag lives in the per-control state and should default to true (fresh output follows).

### 6. Rewire `MainWindow.axaml.cs`
Hold the two binder instances as fields. Reduce `OnEditorAttached` / `OnEditorDataContextChanged` / `OnOutputEditorAttached` / `OnOutputEditorDataContextChanged` to one-line forwarders into the matching binder's Bind, and add the two new detach forwarders calling Unbind. Delete `BindEditor`, `BindOutputEditor`, `EditorBinding`, `OutputBinding`, `_bindings`, and `_outputBindings`. Re-source `ColorizerFor` from the code-editor binder's state. Rewrite `FindActiveEditor` as a lookup into the code-editor binder for `Vm.ActiveTab` — deleting the `GetVisualDescendants()` scan, the `DataContext is EditorTabViewModel` guard, and the comment above it. The callers (`OpenHit`, `GoToDefinitionAsync`, `FindRefsAsync`, `TrySearchTermInEditor`, `OnCodeCtxOpening`) keep their existing null-checks and need no other change; the two `FilePath!` null-forgiving comments in `GoToDefinitionAsync`/`FindRefsAsync` remain correct (and are now backed by the registry rather than a predicate). Drop `using Avalonia.VisualTree;` and any other now-unused usings.

### 7. Add the detach hooks in `MainWindow.axaml`
On both `ae:TextEditor` entries under `TabControl.DataTemplates`, add `DetachedFromVisualTree="…"` alongside the existing `AttachedToVisualTree` / `DataContextChanged`. No other markup changes.

### 8. Update `docs/avalonia.md`
Two bullets are now wrong:
- *"One realized control is shared across all same-`DataType` items in a `TabControl`"* — the rebind-on-`DataContextChanged` guidance stands, but it must gain the other half this ticket adds: **also unbind on `DetachedFromVisualTree`**, because switching to a different `DataType` realizes a *different* control and the old one, its document, its handlers and any pending CTS leak otherwise. Note that teardown must be the exact inverse of setup so a re-attach can't double-add transformers/handlers.
- *"`DataContext == ActiveTab` is ambiguous once two tab kinds render the same control type"* — the recommended fix (constrain the predicate by VM type) is superseded. Rewrite it: don't search the visual tree at all; keep a **per-kind** binding registry and ask the one belonging to the kind you want. The ambiguity then can't arise, because the two kinds resolve different templates and therefore own disjoint control instances.

## Test Plan
- [ ] Compile-check: `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with 0 errors and only the three pre-existing warnings.
- [ ] `dotnet test MiniIde.slnx` still passes (the icon-font coverage guard is unaffected but must stay green).
- [ ] Launch via `scripts/run.ps1`; open `MiniIde.slnx`.
- [ ] Open a `.cs` file → Roslyn colors appear immediately. Type a burst of characters → reclassify happens once, after you pause (~200 ms), not per keystroke.
- [ ] Open a `.csproj` and a `.json` → xshd colors. Switch repeatedly between the `.cs` and the `.csproj` tab → no color bleed in either direction (the `xml-json-syntax-highlighting` hazard).
- [ ] **Detach/re-attach:** with a `.cs` tab open, press F5 to open an output tab, then switch back to the `.cs` tab — repeat several times. Text must not become double-colorized, and typing must still trigger exactly one reclassify. (This is the check that unbind/rebind is symmetric; a duplicate `LineTransformer` or duplicate `TextChanged` handler shows up here.)
- [ ] **Two output tabs:** F5 a project (output streams into `<project> - Output`, view follows the tail), then run a NuGet restore (`NuGet - Output`). Switch back and forth between the two output tabs → each shows its own buffer, and the tab you land on resumes tail-following its own document. (This is the exact regression the original one-shot-`HashSet` binder had.)
- [ ] Mid-run, scroll up in the output tab → it stops auto-scrolling; scroll back to the bottom → tail-follow resumes.
- [ ] Let a run produce >5000 lines → the buffer trims from the top and stays responsive.
- [ ] **Brand-new-tab caret placement (the timing check):** in an open `.cs` file, put the caret on a symbol defined in a file that is **not** currently open, press F12 → the file opens in a new tab **and** the caret lands on the definition line (not line 1). Repeat Shift+F12 → the Find panel lists references and clicking one opens/positions correctly.
- [ ] F12 / Shift+F12 while an **output** tab is active → nothing happens, no exception (the invariant the deleted type guard used to enforce).
- [ ] Right-click a token in a `.cs` file → the caret moves to it; **Search solution** shows the token in its header, **Find usages** / **Go to definition** are enabled. Right-click inside a keyword/string/comment → the symbol items are disabled. Right-click with a selection → the selection is preserved and used as the search term.
- [ ] Close an output tab whose run is still live → the process stops (unchanged behavior).
- [ ] No exceptions surfaced in the status bar or the debug output throughout.

## Learnings

### Architectural decisions
- **Open Decision 1 — shared contract: abstract `DocumentTabViewModel : TabViewModelBase`** (the default). It takes the `TextDocument` as a **constructor parameter** rather than exposing a settable/`init` property. That keeps `Document` non-null by construction for both derivations (`EditorTabViewModel` passes `new TextDocument(File.ReadAllText(path))`, `OutputTabViewModel` passes `new TextDocument()`), so the binder never has to null-check it. `ImageTabViewModel` stays on `TabViewModelBase`.
- **Open Decision 2 — binder shape: the delegate form** (the default), but with **two** delegates rather than one, because bind has two distinct lifetimes:
  - `Func<TextEditor, (TState, Action)> setUpControl` — one-time per realized control; returns the per-control state **and** its teardown.
  - `Func<TextEditor, TState, TTab, Action> attachTab` — per tab; returns its detach.
  Each returns its own undo, so setup/teardown sit adjacently and cannot drift. `Unbind` is then mechanically the inverse: invoke the tab detach, invoke the control teardown, drop the entry.
- **The binder is generic over the per-control state (`TabEditorBinder<TTab, TState>`), not just the tab type.** This is what keeps `ColorizerFor` type-safe: `CodeEditorState` holds a **non-null** `RoslynColorizer`, so `_codeEditors.StateFor(editor)?.Colorizer` needs no cast and no `null!`. A single-type-param binder would have forced either an `object?` tag (unsafe cast at the call site) or a second registry in the window. `OutputEditorState` carries the `WasAtBottom` flag, so the second type param earns its keep on both sides.
- **The per-kind wirings moved into `Views/TabEditorBinder.cs` with the binder**, and `DebouncedRefreshAsync` / `RefreshAndRedraw` / `OnEditorPointerPressed` went with them — they are the code-tab wiring, not window concerns, and the ticket's scope called for the new file to carry "the two per-kind attach/detach wirings". `MainWindow.axaml.cs` shrank by ~230 lines and now holds only forwarders plus the symbol-text helpers (which stay, per Out of scope).
- **Open Decisions 3 & 4** took their defaults: `Views/TabEditorBinder.cs`; unbind does **not** null `editor.Document`.

### Problems encountered
- **`VisualTreeAttachmentEventArgs` lives in the root `Avalonia` namespace**, not `Avalonia.VisualTree`. Deleting `using Avalonia.VisualTree;` (now dead — the visual-tree scan is gone) is correct, but dropping `using Avalonia;` alongside it breaks all six attach/detach handler signatures. Five `CS0246`s, nothing subtler.
- The tab-switch path previously called `DebounceCts.Cancel()` **without** `Dispose()` (the ticket flagged this). The new detach closure does both, and nulls the field.

### Interesting tidbits
- **The caret `PositionChanged` subscription had to stop being an anonymous lambda.** It was the one subscription in the old `BindEditor` that was structurally unremovable — which is a decent tell that the old code never intended to unbind. `Caret.PositionChanged` is a plain `EventHandler`, so an `EventHandler` local stored and used for both `+=` and `-=` is all it needs.
- **`OutputEditorState.WasAtBottom`'s initial value is unobservable in practice**: `Changed` only ever fires after `Changing`, which recomputes the flag. Keeping it as per-control state (defaulting true) matches the old behavior exactly; a per-attach captured local would have been equivalent.
- **A double-added `RoslynColorizer` would not be visible** — two instances write the same brushes, so the rendered text looks identical. The detach/re-attach test therefore proves absence of *exceptions* and *lost* highlighting, not absence of duplicates; the duplicate is ruled out structurally by `Unbind` removing the transformer it added.

### Verification notes
Exercised in the running app (not just by build/test): fresh `.cs` tab highlights immediately; F5 opens an output tab that streams and tail-follows (which detaches the code editor); switching back re-attaches and re-highlights correctly; `.csproj` renders xshd XML and flipping back to the `.cs` tab shows no color bleed in either direction; right-click places the caret and enables **Find usages** / **Go to definition** (proving `ColorizerFor` re-sources from the binder's state and `FindActiveEditor` resolves from the registry); **Go to definition on a symbol in a not-yet-open file opened the new tab with the caret on the definition line** — the brand-new-tab timing check the Constraints flagged. Parity holds because the registry is updated by the same synchronous `AttachedToVisualTree` / `DataContextChanged` pass that used to set the `DataContext` the visual-tree scan matched on.

### Related areas affected / follow-ups
- **No view-layer test project exists** (Out of scope, deliberately), so all of the above was verified by driving the real window with ad-hoc Win32 `SendInput`/screenshot scripting. That is not repeatable. A follow-up ticket for an **`Avalonia.Headless`** test project would make this class of view-layer regression (bind/unbind symmetry, tail-follow, mode switching) testable in-process.
- The rest of the `MainWindow.axaml.cs` decomposition (symbol-text helpers) and the three imperative `ContextMenu.Opening` enablement handlers remain parked for their own tickets, untouched here.

### Rejected alternatives
- **Sourcing `ColorizerFor` from `editor.TextArea.TextView.LineTransformers.OfType<RoslynColorizer>()`** instead of from binder state. It works and needs no `TState`, but it re-introduces "search the world for the thing you already own" — the exact instinct this ticket exists to remove.
- **An abstract binder base with `OnBind`/`OnUnbind` hooks and two subclasses.** More types, and it separates each variant's setup from its teardown — the drift the delegate form is designed to prevent.
- **One merged registry across both editor kinds.** Explicitly ruled out by the Constraints: it would resurrect the `DataContext is EditorTabViewModel` ambiguity that the per-kind split eliminates structurally.
