# Output Panel as Plain Text

## Context
**Current behavior**: Output tab renders build/run output as `ListBox` rows bound to `ObservableCollection<string>`. Selection is per-row-object; Ctrl+C copies item `ToString()`. Cannot select mid-line or drag across rows to copy a range.

**New behavior**: Output tab renders as a read-only `AvaloniaEdit.TextEditor` — one continuous text stream. Standard text selection (drag, shift-click, Ctrl+A) and Ctrl+C copy work. Auto-scrolls to bottom on append while the user is already at the bottom; pauses tail-follow if the user has scrolled up. No word-wrap; horizontal scroll for long lines. `OutputViewModel.Append(string)` / `Clear()` signatures unchanged — no consumer changes.

## Scope
### In scope
- Swap `ListBox` for `ae:TextEditor` in `MainWindow.axaml` Output tab.
- Rework `OutputViewModel` to back with `AvaloniaEdit.Document.TextDocument`; keep public `Append`/`Clear`.
- Code-behind binding of `TextEditor.Document` (per `docs/avaloniaedit.md` — `Document` is CLR, not AvaloniaProperty).
- Tail-follow: auto-scroll to bottom on append only when viewport was at bottom pre-insert.
- Preserve 5000-line cap.

### Out of scope
- ANSI escape stripping / colorization of `\e[...]` codes emitted by MSBuild.
- Colorized error/warn lines.
- Search-in-output.
- Font size / theming controls.

## Relevant Docs & Anchors
- `docs/avaloniaedit.md` — `Document` set imperatively; theme StyleInclude already present in `App.axaml`.
- `src/MiniIde/Views/MainWindow.axaml.cs` — `BindEditor` shows the code-behind binding pattern used by editor tabs. Reuse the `AttachedToVisualTree` + `DataContextChanged` handler pair as the exemplar for wiring the Output editor; differ in that Output has no colorizer, no per-tab switching, and no caret-tracking.
- `src/MiniIde/ViewModels/OutputViewModel.cs` — current model; sole consumers are `MainWindowViewModel.PlayAsync` (`Output.Clear()`, `Run.RunAsync(..., Output.Append)`) and the axaml binding.
- `src/MiniIde/Views/MainWindow.axaml` — Output `TabItem` is the target; existing editor `TextEditor` at the code-tabs `ContentTemplate` is the styling exemplar (Consolas, `#000000` bg, `#DCDCDC` fg).

## Constraints & Gotchas
- `TextEditor.Document` is a CLR property — `{Binding Document}` in XAML silently no-ops. Assign in code-behind on `AttachedToVisualTree`.
- `Append` is called from background threads (stdout reader in `Run.RunAsync`). Current impl marshals via `Dispatcher.UIThread.Post`. Preserve that — `TextDocument` mutation must run on UI thread.
- Line-cap trim: use `Document.GetLineByNumber(1)` → `Document.Remove(line.Offset, line.TotalLength)` so the newline is included; do not slice the raw string.
- Tail-follow decision must be captured *before* insert (comparing offset vs. extent), then re-applied *after* insert. Capturing after insert would always report "not at bottom" if the insert grew the extent.
- Selection preservation: `Document.Insert(Document.TextLength, ...)` preserves user selection/caret. Do not use `Document.Text = ...` for appends.

## Open Decisions
1. **Line-cap trim granularity** — trim exactly at overflow (each append) vs. batched (only when line count exceeds cap by N). Default: trim on every overflow, matching current 1-in-1-out behavior.
2. **Scroll-at-bottom epsilon** — small tolerance (e.g. a few pixels) when deciding "viewport is at bottom" to account for line-height rounding. Default: 1.0.
3. **`OutputViewModel` field type for the document** — expose `TextDocument Document { get; }` on the VM vs. keep everything private and expose only `Append`/`Clear` with the view resolving the document some other way. Default: expose `Document` as a `get;`-only property; view code-behind reads it directly (mirrors how `EditorTabViewModel.Document` is consumed by `BindEditor`).

## Acceptance Criteria
- [ ] Output tab hosts an `ae:TextEditor` with `IsReadOnly=True`, no line numbers, no word-wrap, `Background="#000000"`, `Foreground="#DCDCDC"`, `FontFamily="Consolas,monospace"`.
- [ ] User can drag-select across multiple lines in Output and Ctrl+C copies exactly the selected text (including newlines).
- [ ] `OutputViewModel.Append(string)` and `OutputViewModel.Clear()` retain identical public signatures; `MainWindowViewModel.PlayAsync` compiles without changes.
- [ ] After a `Play`, Output shows streamed build/run lines in the same order as before; `Clear` resets the panel to empty.
- [ ] With Output at max cap, appending one line removes exactly the topmost line (net line count stays at cap).
- [ ] When the user has scrolled up mid-stream, subsequent appends do not jump the viewport to the bottom; when the viewport was at the bottom pre-append, it stays pinned to the bottom after append.
- [ ] `ObservableCollection<string> Lines` is removed from `OutputViewModel` (no lingering dead field).

## Implementation

### 1. Rework `OutputViewModel` around `TextDocument`
Replace `ObservableCollection<string> Lines` with `AvaloniaEdit.Document.TextDocument Document { get; } = new()`. Rewrite `Append(string line)` to `Dispatcher.UIThread.Post` a call that inserts `line + "\n"` at `Document.TextLength`, then trims from the top while line count exceeds 5000 by removing the first `DocumentLine` (offset + `TotalLength`, so the trailing newline goes too). Rewrite `Clear()` to `Dispatcher.UIThread.Post(() => Document.Text = "")`. Keep the 5000 constant inline (matches current shape).

### 2. Swap the Output tab element in `MainWindow.axaml`
Replace the `ListBox` inside `<TabItem Header="Output">` with an `<ae:TextEditor>` styled to match the code-editor exemplar: `IsReadOnly="True"`, `ShowLineNumbers="False"`, `WordWrap="False"`, `Background="#000000"`, `Foreground="#DCDCDC"`, `FontFamily="Consolas,monospace"`, `FontSize="13"`, plus `Name="OutputEditor"`, `AttachedToVisualTree="OnOutputEditorAttached"`. No `DataTemplate` needed (single instance, not per-item).

### 3. Wire `Document` in code-behind
In `MainWindow.axaml.cs`, add `OnOutputEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)`. Cast `sender` to `TextEditor`, read `Vm.Output.Document`, assign to `editor.Document`. Simpler than `BindEditor` — no per-tab switching, no colorizer, no caret handling. If `Vm` isn't yet materialized on first attach, guard with a null check and re-run in a `DataContextChanged` handler mirroring `OnEditorDataContextChanged`.

### 4. Tail-follow on append
Also in the attach handler, subscribe to `editor.Document.Changed` (or a `TextChanged` on the editor). Before applying auto-scroll, need to know whether the viewport was at the bottom *before* the insert. Cheapest approach: subscribe to `Document.Changing` to capture `wasAtBottom = editor.TextArea.TextView.VerticalOffset >= editor.TextArea.TextView.ExtentHeight - editor.TextArea.TextView.Viewport.Height - epsilon`, then on `Document.Changed` (post-mutation) if `wasAtBottom` call `editor.ScrollToLine(editor.Document.LineCount)`. Epsilon = 1.0 (see Open Decision #2). If `Document.Changing`/`Changed` don't fit cleanly, an equivalent is to wrap the append logic in `OutputViewModel` and raise a small event the view listens to — but prefer the pure-`TextDocument` event route to keep the VM UI-framework-agnostic.

### 5. Confirm consumer compat
Grep for `Output.Lines` — must return zero hits after step 1. Grep for `Output.Append` / `Output.Clear` — call sites in `MainWindowViewModel.PlayAsync` should be unchanged. No other file should need edits.

## Test Plan
- [ ] Build passes: `dotnet build src/MiniIde/MiniIde.csproj`.
- [ ] App launches (no blank Output tab — regression check on the AvaloniaEdit theme StyleInclude).
- [ ] Open a runnable project, press F5. Build/run output streams into the Output tab, same lines, same order as before the change.
- [ ] Drag-select across 3+ lines in Output, press Ctrl+C, paste into another app — pasted text matches the selection exactly, including newlines.
- [ ] Ctrl+A in Output selects all text; Ctrl+C copies the full buffer.
- [ ] Press F5 twice in a row on a runnable project — Output clears at the start of the second run.
- [ ] Trigger a long-running or verbose build (e.g. `dotnet build` of the whole solution as the runnable target if possible). While it's streaming, scroll up manually — new appends do NOT yank the view back to the bottom.
- [ ] Scroll back to the bottom mid-stream — subsequent appends keep the view pinned at the bottom.
- [ ] Force >5000 lines of output (a chatty test run, or temporarily lower the cap to 50 for the test). Confirm the top lines drop and total line count holds at the cap.
- [ ] Output tab is read-only: clicking in it and typing does nothing; no caret-insert behavior.
- [ ] Editor tabs (C# / XML / JSON) still colorize and behave normally — regression check that shared `TextEditor` styling wasn't perturbed.

## Learnings

### Architectural decisions
- Open Decision #1 (trim granularity): went with per-append trim in a `while` loop (`while (Document.LineCount > 5000)`) rather than an `if`. `while` is defensive against future callers that might insert multi-line chunks in one call — a single `if` would leak lines above the cap in that case.
- Open Decision #2 (epsilon): 1.0 pixel. Matches ticket default.
- Open Decision #3 (Document exposure): exposed `TextDocument Document { get; }` as a `get;`-only auto-prop, mirroring `EditorTabViewModel.Document`. VM remains UI-framework-agnostic aside from the `AvaloniaEdit.Document` reference (already a project-wide dep).
- Tail-follow via `TextDocument.Changing` + `Changed` events rather than a VM event: keeps the tail-follow logic in the view where it belongs (viewport is a view concern) and leaves the VM ignorant of scroll state.
- Kept a `HashSet<TextEditor> _outputBound` for idempotent binding on both `AttachedToVisualTree` and `DataContextChanged`, matching the guard pattern used by `BindEditor` for code-tab editors.

### Problems encountered
- **`TextView.ExtentHeight` / `Viewport` don't exist as direct properties.** First compile failed with `CS1061`. `AvaloniaEdit.Rendering.TextView` exposes those values through `Avalonia.Controls.Primitives.ILogicalScrollable`. Cast to that interface, then read `Offset.Y`, `Extent.Height`, `Viewport.Height`. Documented below.

### Interesting tidbits
- `TextDocument` raises `Changing` **before** the mutation applies — perfect for pre-insert viewport capture. `Changed` fires after. Both are needed for correct tail-follow: capturing after would always report "not at bottom" if the insert grew the extent.
- `Document.Insert(Document.TextLength, "...")` preserves caret and selection; `Document.Text = "..."` blows both away. Only use the latter for `Clear()`.

### Related areas affected
- `MainWindow.axaml.cs` — added `OnOutputEditorAttached`, `OnOutputEditorDataContextChanged`, `BindOutputEditor`. Sibling to the existing `BindEditor` helper for code-tab editors. Deliberately kept separate rather than folded into one polymorphic `Bind` — the concerns (colorizer, caret sync, tab switching vs. tail-follow, single-instance) don't overlap.

### Rejected alternatives
- Raising a custom event on `OutputViewModel` for tail-follow ("append happened"): rejected. `TextDocument.Changing`/`Changed` already gives us pre/post hooks with no VM plumbing.
- Batched line-cap trim: rejected per ticket default. Per-append matches existing 1-in-1-out semantics; simpler and no perceptible cost at these line rates.
- Binding `Document` via XAML: called out in ticket & `docs/avaloniaedit.md`; silent no-op.
