# Incremental Solution-Tree Refresh (Preserve Expansion / Selection / Scroll)

## Context
**Current behavior**: A structural or overflow disk-change signal from the `SolutionWatcher` rebuilds the entire left solution tree from scratch — `MainWindowViewModel.ApplyDiskChangeToViewAsync` calls `LoadTreeAsync`, which does `Tree.Clear()` then re-adds brand-new `TreeNode` instances from `SolutionService.LoadAsync`. Because expanded/collapsed state, selection, and scroll position all live in Avalonia's `TreeViewItem` containers (nothing is model-bound — `TreeNode.IsExpanded` exists but is dead, and the `TreeView` binds neither `IsExpanded` nor `SelectedItem`), replacing every item discards all of it. So every external add/remove/rename of a `.cs` file, every `.csproj`/NuGet change, and every event-buffer overflow during an agent burst collapses the whole tree back to its default state, drops the selection, and jumps scroll to the top.

**New behavior**: A structural refresh updates the tree **in place** — surviving nodes keep their identity, so expansion, selection, and scroll are preserved and there is no rebuild flicker. `LoadTreeAsync` stops calling `Tree.Clear()`; instead it diff-merges the freshly-loaded nodes into the existing `Tree`, reusing the existing `TreeNode` instance wherever a node's `(Kind, full-path)` still matches and recursing into its children, inserting genuinely-new nodes and removing vanished ones. Expansion is additionally made robust by reviving `TreeNode.IsExpanded` as a two-way-bound, notifying property (the Avalonia-idiomatic mechanism), so a survivor's expanded state is model-backed rather than dependent on container recycling. Content-only changes still don't touch the tree (unchanged). The whole-rebuild path effectively disappears — initial open is just the degenerate "all inserts" case of the same merge.

## Prerequisites
- `docs/tickets/complete/2026-07-16 os-driven-disk-reconcile.md` — introduced `SolutionWatcher`, `DiskChangeSignal`, and the `ApplyDiskChangeToViewAsync` → `LoadTreeAsync` structural-refresh route this ticket rewrites. Its Learnings → "Workarounds / limitations" flagged this exact tree-collapse as a deliberately-deferred follow-up and named both mechanisms this ticket fuses (bind `IsExpanded` two-way, or targeted node insert/remove).
- `docs/tickets/complete/2026-07-17 safe-rename.md` — introduced `ReplaceTreeFileNode` / `TryReplaceFileNode`, the existing targeted single-node swap the merge generalizes. Read its "No in-place identity mutation" constraint.

## Scope
### In scope
- Reworking `MainWindowViewModel.LoadTreeAsync` to diff-merge fresh nodes into the existing `Tree` instead of `Tree.Clear()` + re-add.
- A pure, unit-testable tree-merge routine that aligns an existing `ObservableCollection<TreeNode>` to a freshly-loaded ordered node list, reusing matched instances and recursing.
- Reviving `TreeNode.IsExpanded` as a notifying, two-way-bound property, and binding `TreeViewItem.IsExpanded` in the `TreeView`'s `ItemContainerTheme`.
- Tests for the merge routine.

### Out of scope
- **Suppressing the structural refresh.** "Prevent the reset" means *preserve state across the refresh*, not stop refreshing — the refresh is what makes an externally-added file appear at all (the `os-driven-disk-reconcile` reason it exists). Do not gate or skip it.
- **The `Projects` list rebuild** (the runnable-project dropdown, `Projects.Clear()` + re-add in `LoadTreeAsync`) and `NuGetVm.SetProjects` — a flat list with no expansion/selection state to preserve; leave as-is. The startup-project re-resolution in `LoadTreeAsync` also stays unchanged.
- **`SearchService` / `FileGrepper`** — untouched, as in the reconcile ticket.
- **Content-only (non-structural) change handling** — already correct (tree untouched); no change.
- **A headless (Avalonia.Headless) view-test project** — still none. The merge logic is tested as a pure routine; container-level preservation (expansion/selection/scroll actually surviving on screen) is manual GUI verification.
- **Virtualization / lazy child population** — `LoadAsync` still eagerly populates the whole tree via `PopulateProjectFiles`; not revisiting that here.

## Relevant Docs & Anchors
- **Design doc**: `docs/disk-watching.md` — the watcher/reconcile overview; `docs/CLAUDE.md` (one topic per file, fragments over prose) for any doc edits.
- **Related tickets**: the two Prerequisites above — read the reconcile ticket's "Workarounds / limitations" and the rename ticket's "No in-place identity mutation" note before coding.
- **Code anchors** (symbols; verify against source):
  - `ViewModels/MainWindowViewModel.cs` — `LoadTreeAsync` (the `Tree.Clear()` + re-add to rewrite), `ApplyDiskChangeToViewAsync` (the structural/overflow caller), `ReplaceTreeFileNode` / `TryReplaceFileNode` (the targeted-swap precedent: walks from `Tree` roots, matches by full path case-insensitively, replaces leaf nodes rather than mutating — the merge generalizes this to insert/remove/recurse), `Tree` (`ObservableCollection<TreeNode>`).
  - `Services/SolutionService.cs` — `LoadAsync` (returns freshly-built, **canonically-ordered** project nodes: projects alphabetical by filename; within a folder, `BuildFolder` emits subdirectories then files in `Directory.Enumerate*` order), `PopulateProjectFiles`, `BuildFolder`, `IdeDirectories.Pruned` exclusion.
  - `Models/SolutionNode.cs` — `TreeNode`: `Name`/`Path`/`Kind` are `init`-only; `IsExpanded` is currently a plain unbound `{ get; set; }`; the class is not `INotifyPropertyChanged`; `IsLoaded` guards population.
  - `Views/MainWindow.axaml` — the `SolutionTree` `TreeView` and its `TreeView.ItemContainerTheme` (`ControlTheme TargetType="TreeViewItem"`, currently only a `VerticalAlignment` setter) where the `IsExpanded` binding is added.
  - CommunityToolkit.Mvvm is already in use (`[ObservableProperty]` in the VMs) if an `ObservableObject`-based notify is preferred for `TreeNode`.

## Constraints & Gotchas
- **Preserve, don't suppress.** The structural refresh must still pick up added/removed/renamed files and `.csproj` changes — do not skip it. The whole change is in *how* the tree is updated, not *whether*.
- **`LoadTreeAsync` is shared with initial solution open** (`OpenSolutionAsync`). The merge must degrade cleanly to "all inserts" when `Tree` is empty, producing an identical result to today's build-from-scratch. No separate first-open path.
- **Match identity by `(Kind, full-path)`, case-insensitive** — mirror `TryReplaceFileNode` / `FileId` / `FindDocument` normalization (`Path.GetFullPath` + `OrdinalIgnoreCase`). A path that changes `Kind` (file↔folder) is a remove + add, not a reuse.
- **Never mutate a survivor's identity fields.** `Name`/`Path`/`Kind` are `init`-only and matched-on — a survivor by definition has the same path/kind, so nothing to mutate. A rename arrives as remove-old + add-new (distinct paths). Only `IsExpanded` becomes mutable+notifying; keep the "replace leaf nodes, never re-home by assignment" discipline from the rename ticket for everything else.
- **Ordering is inherited from the incoming list, not re-derived.** `LoadAsync` already returns nodes in canonical order; the merge should make each parent's `Children` match the incoming order and membership (reusing instances), so a newly-added file lands in its correct sorted slot without the merge re-implementing `BuildFolder`'s sort.
- **Expansion mechanism is belt-and-suspenders by design.** Instance reuse preserves the container (and thus expansion/selection) in practice; the two-way `IsExpanded` binding makes expansion model-backed so it survives robustly even if a node is ever replaced. This is the Avalonia-blessed pattern (AvaloniaUI discussions [#12397](https://github.com/AvaloniaUI/Avalonia/discussions/12397), [#13903](https://github.com/AvaloniaUI/Avalonia/discussions/13903)): store `IsExpanded` on the node, two-way bind it in a `TreeViewItem` style/theme.
- **`TreeNode` needs change notification on `IsExpanded`** for the two-way binding to be well-behaved (it's a plain class today). Scope notification to the bound property; do not gratuitously convert every field.
- **Threading.** `LoadTreeAsync` already runs on the UI thread (posted from `OnDiskChanged` via `Dispatcher.UIThread`). The merge mutates `Tree` and its `Children` (`ObservableCollection`s) on that thread — keep it there.
- **No new build warnings.** The reconcile ticket enumerates the three pre-existing/expected warnings; introduce none.

## Open Decisions
1. **Where the merge routine lives** — a static helper (e.g. `Services/TreeMerge.cs` or a static method on `SolutionService`) vs. a private method on `MainWindowViewModel`. Default: a static, VM-free helper so it's unit-testable without a view. Implementer's call.
2. **`IsExpanded` notification mechanism** — manual `INotifyPropertyChanged` on `TreeNode` vs. deriving `TreeNode` from CommunityToolkit `ObservableObject` with `[ObservableProperty]`. Default: whichever is lighter given `TreeNode`'s `init`-only fields (`ObservableObject` if it composes cleanly). Implementer's call.
3. **Selection preservation** — rely on instance reuse to keep the container's selection, vs. add an explicit `SelectedItem` two-way binding now. Default: rely on identity (matches the "no `SelectedItem` binding today" baseline); add the binding only if selection proves flaky in the running app. Implementer's call.
4. **Scroll preservation** — treated as a free rider on identity preservation; no explicit handling unless it visibly jumps. Default: no extra work. Implementer's call.

## Acceptance Criteria
- [ ] `TreeNode.IsExpanded` is a notifying property and `TreeViewItem.IsExpanded` is two-way bound to it in the `SolutionTree` `TreeView` — toggling a folder's chevron updates the node's `IsExpanded`, and setting the node's `IsExpanded` updates the UI.
- [ ] `LoadTreeAsync` contains no `Tree.Clear()` of the node tree; the tree is updated by an in-place diff-merge against the freshly-loaded nodes.
- [ ] The merge reuses the **same `TreeNode` instance** (assertable by reference equality) for every node whose `(Kind, full-path)` is present both before and after; a node only in the new set is inserted; a node only in the old set is removed; the merge recurses into a survivor's `Children`.
- [ ] After a merge, each parent's `Children` matches the incoming list's order and membership (a newly-added file appears in its canonical sorted position, not appended).
- [ ] With a solution open and a folder expanded and a node selected, an external structural change elsewhere (add/remove/rename a `.cs` file, edit a `.csproj`) leaves the expanded folder expanded and the selection intact; a newly-added folder appears collapsed.
- [ ] Initial `OpenSolutionAsync` still populates the full tree (the empty-`Tree` merge is all-inserts, identical to prior behavior).
- [ ] Content-only external edits still do not rebuild or reorder the tree.
- [ ] The build produces no new warnings.

## Implementation

### 1. Make `TreeNode.IsExpanded` a notifying property
In `Models/SolutionNode.cs`, revive `IsExpanded` from a dead plain property into a change-notifying one (Open Decision 2). Leave `Name`/`Path`/`Kind`/`Children`/`IsLoaded` as-is. Intent: let a two-way binding both read the user's expand/collapse and let the model drive expansion, per the Avalonia-idiomatic pattern.

### 2. Two-way bind `TreeViewItem.IsExpanded`
In `Views/MainWindow.axaml`, in the existing `SolutionTree` `TreeView.ItemContainerTheme` `ControlTheme` (the one setting `VerticalAlignment`), add a setter binding `IsExpanded` to the node's `IsExpanded` in `TwoWay` mode. Intent: expansion state now round-trips through the model, so it's preserved robustly across a refresh.

### 3. Write the pure diff-merge routine
Add the merge (Open Decision 1). Signature intent: given an existing `ObservableCollection<TreeNode>` and an incoming `IReadOnlyList<TreeNode>` (already canonically ordered), mutate the existing collection in place so it matches the incoming order and membership, reusing instances. Algorithm (prose):
- Index existing children by `(Kind, GetFullPath(Path))`, case-insensitive.
- Walk the incoming list by index. For each incoming node: if a matching existing instance is found, **keep that instance** (discard the incoming duplicate), ensure it sits at the current index (move within the collection if needed), and **recurse** merging its `Children` against the incoming node's `Children`; otherwise **insert** the incoming (new) node at this index.
- After the walk, **remove** any existing child not matched by an incoming node.
- Root level (`Tree` = project nodes) uses the same routine as any folder's `Children`.
Mirror `TryReplaceFileNode`'s path normalization. Keep it side-effect-free beyond the passed collections so it's unit-testable without a view.

### 4. Rewire `LoadTreeAsync` to merge instead of clear+rebuild
In `MainWindowViewModel.LoadTreeAsync`, keep building the incoming `projectNodes` via `Solution.LoadAsync(path)` and keep the `Projects` rebuild + startup re-resolution exactly as they are. Replace the `Tree.Clear()` + `foreach … Tree.Add(n)` with a single call to the step-3 merge (`Tree` ← `projectNodes`). Intent: same inputs, incremental application; empty `Tree` yields all-inserts (initial open unchanged).

### 5. Tests
Add unit tests for the merge routine (pure, no view needed) — build small `TreeNode` trees by hand:
- Add: an incoming tree with one extra file yields one insert at the correct index; all other nodes are the *same instances* (reference-equal).
- Remove: an incoming tree missing a node removes exactly that node; siblings are reused instances.
- Rename (as remove+add): old path gone, new path present at its sorted slot; unrelated survivors reused.
- Reorder/insert position: a new file lands in the incoming list's position, not appended.
- Recurse: a change deep in a folder reuses every ancestor instance and only touches the changed leaf's parent.
- Degenerate: merging into an empty collection inserts everything (initial-open equivalence).
- Expansion carry: a survivor folder with `IsExpanded = true` still has `IsExpanded = true` after a merge that adds a sibling.

## Test Plan
- [ ] `dotnet build src/MiniIde/MiniIde.csproj -o <temp>` succeeds with only the pre-existing warnings; `dotnet test MiniIde.slnx` passes including the new merge tests.
- [ ] Launch via `scripts/run.ps1`, open `MiniIde.slnx`. Expand several folders, select a file. Externally (agent or another editor) **add** a new `.cs` file in an unrelated folder → the new file appears in sorted position, and every previously-expanded folder stays expanded, selection intact, no scroll jump, no flicker.
- [ ] Externally **delete** a file and **rename** another → the tree reflects both, expansion/selection preserved for untouched nodes.
- [ ] Edit a `.csproj` (or add a NuGet package) → tree refresh preserves expansion (structural signal, but no file set change).
- [ ] Burst test: run an agent rewriting many files (trigger overflow) → tree stays responsive; expansion of surviving folders preserved; new/removed files reflected after the burst.
- [ ] Toggle a folder's chevron, then trigger any structural refresh → the folder's state (whatever you left it) is what remains, confirming the two-way `IsExpanded` binding round-trips.
- [ ] Close and reopen the solution → full tree builds correctly (all-inserts path).
- [ ] Content-only external edit to an open file → tab updates (existing behavior), tree does not reorder or collapse.
