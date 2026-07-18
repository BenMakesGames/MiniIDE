# Go to Implementation & Go to Subclasses (Roslyn navigation from the code context menu)

## Context
**Current behavior**: The code-editor context menu (`Views/MainWindow.axaml`, the `ContextMenu` with `Opening="OnCodeCtxOpening"` inside the `EditorTabViewModel` `DataTemplate`) offers Search solution / Find usages / Go to definition / Rename…. Navigation is limited to a symbol's *definition* (F12) and its *references* (Shift+F12). There is no way to jump from an interface or abstract/virtual member to the code that implements/overrides it, nor from a base type to its derived types.

**New behavior**: The context menu gains two always-present navigation items — **Go to Implementation** and **Go to Subclasses** — placed after Go to definition. Like Find usages / Go to definition, they are always in the menu and dim out when not applicable; unlike those two, their dimming is *kind-aware* (see below). **Go to Implementation** lists the symbols that implement an interface type/member (`SymbolFinder.FindImplementationsAsync`) or override an abstract/virtual member (`SymbolFinder.FindOverridesAsync`). **Go to Subclasses** lists the derived classes of a class (`FindDerivedClassesAsync`, transitive) or the derived interfaces of an interface (`FindDerivedInterfacesAsync`, transitive). Both surface their results (0..N) in the existing Find panel, exactly as Find usages does.

## Scope
### In scope
- Two `WorkspaceService` query methods mirroring `FindReferencesAsync` — resolve the caret symbol against the fresh-disk `_solution` snapshot, run the relevant `SymbolFinder` query, map in-source result locations to `FindHit`s, return `IReadOnlyList<FindHit>?` (null = no symbol under the caret).
- Two `MainWindowViewModel` flow methods mirroring `FindReferencesAsync` (guard, cold-load status, hand results to the Find panel).
- Two context-menu items + click handlers mirroring `CtxFindUsagesItem` / `OnCtxFindUsagesClick` / `FindRefsAsync` (reveal the Find tab after).
- **Kind-aware synchronous dimming**: extend the cheap `CodeSymbolContext` gate so each new item enables only on classifications where it could plausibly resolve, using the already-cached semantic classifications (no async, no workspace load at menu-open time).
- Generalizing the Find panel's result entry point so its status can read "N implementations" / "N subclasses" instead of only "N references".

### Out of scope
- **Go to Implementation** listing implementers of an *abstract class* itself as a type (that's derived-classes territory — use Go to Subclasses). Implementation covers interface types/members and overridable *members*, not "concrete subclasses of an abstract base type".
- Overlap dedup between the two commands — implementers of an interface belong to Go to Implementation; derived interfaces belong to Go to Subclasses. They intentionally cover different sets; don't merge them.
- Single-result auto-navigation (jump straight to the sole result like Go to definition). Default is to always list in the Find panel — see Open Decisions.
- Keybindings. Menu-only per the request — see Open Decisions for an optional `Ctrl+F12`.
- Metadata / decompiled results — only in-source locations are shown (framework/NuGet implementers are filtered out), matching the rename service's in-source discipline.
- Any change to the existing Search / Find usages / Go to definition / Rename behavior or their (coarse) enablement.

## Relevant Docs & Anchors
Read before coding:
- **`docs/roslyn.md`** — the `SymbolFinder` API map (Jump-to-def / Find usages rows), the Roslyn `5.6.0` package pins, and the cold-start "first use may take a while" cost that applies to the first invocation of these exactly as it does to find-usages. **Add two rows** to its API-map table for the new features when done.
- **`docs/tickets/complete/2026-07-17 safe-rename.md`** — closest analogue: the two-level eligibility gate (cheap synchronous menu gate vs. authoritative async check), the "Roslyn types stay inside `Services`; the VM gets plain records" boundary, and the "confirm the linchpin Roslyn API against the restored assembly first" discipline. Mirror the approach; do **not** copy verbatim.
- **`docs/tickets/complete/2026-07-05 code-view-context-menu.md`** — how the original symbol actions and the synchronous `Opening` enablement handler were built.
- **Code anchors**:
  - `Services/WorkspaceService.cs` — `FindReferencesAsync` (the shape to mirror: resolve → SymbolFinder → map to `FindHit` → return nullable list), `ResolveSymbolAsync` (public since safe-rename; the shared caret→`ISymbol` resolver), `ToSourceLocation`, `FindDocument`, and the `_solution` snapshot discipline in the class doc-comment (never writes disk).
  - `ViewModels/MainWindowViewModel.cs` — `FindReferencesAsync` / `GoToDefinitionAsync` (flow + status idioms, `EnsureWorkspaceReadyAsync`, the "Loading workspace (first use may take a while)…" cold-load message), and `Find` (the `FindResultsViewModel`).
  - `ViewModels/FindResultsViewModel.cs` — `ShowReferences` (the entry point to generalize) and `ShowNoResults`; the panel is already origin-agnostic (only the status string says "reference"). `Plural.Of(count, noun)` builds the status.
  - `Views/MainWindow.axaml` — the `ContextMenu Opening="OnCodeCtxOpening"` with `CtxSearchItem` / `CtxFindUsagesItem` / `CtxGoToDefItem` / `CtxRenameItem`. Add the two items after `CtxGoToDefItem`.
  - `Views/MainWindow.axaml.cs` — `OnCodeCtxOpening` (the enablement switch), `OnCtxFindUsagesClick` + `FindRefsAsync` (caret invoke + `ShowBottomTab(FindTab)`), `FindActiveEditor`, `_codeEditors.StateFor(editor)` (colorizer lookup), and `OnGlobalKeyDown` (F12 / Shift+F12 wiring, for the optional shortcut).
  - `Views/CodeSymbolContext.cs` — `CodeSymbolContext.At()` computes `Term` + `SymbolEligible` synchronously from `colorizer.ClassificationsAt(offset)`. Extend here for the two kind-aware gates.
  - `Services/SymbolClassifications.cs` — `AllowSymbolActions` + the `Resolvable` allowlist of `ClassificationTypeNames` constants. Add the two kind-specific allowlists here, same allowlist philosophy.
  - `src/MiniIde.Tests/WorkspaceServiceTests.cs` — the real-on-disk MSBuild fixture harness (xunit + Shouldly, offsets computed from fixture text via `IndexOf`, per-test temp solution). Mirror its `FindReferences*` tests for the new queries.

## Constraints & Gotchas
- **Confirm the `SymbolFinder` overloads against the restored `5.6.0` assembly before building on them.** Documented signatures (Workspaces 5.x): `FindImplementationsAsync(ISymbol, Solution, IImmutableSet<Project>, CancellationToken)`; `FindOverridesAsync(ISymbol, Solution, IImmutableSet<Project>, CancellationToken)`; `FindDerivedClassesAsync(INamedTypeSymbol, Solution, IImmutableSet<Project>, CancellationToken)` **and** a `transitive`-bool overload `FindDerivedClassesAsync(INamedTypeSymbol, Solution, bool, IImmutableSet<Project>, CancellationToken)`; `FindDerivedInterfacesAsync(INamedTypeSymbol, Solution, bool, IImmutableSet<Project>, CancellationToken)`. All return `Task<IEnumerable<ISymbol>>`. The `projects` set is optional — pass `null` to search the whole solution. Confirm the exact overloads (and whether `transitive` is a distinct overload vs. defaulted) against the restored DLL; if a needed overload is absent, adapt (e.g. call the non-transitive overload and recurse) or report.
- **`FindImplementationsAsync` does not cover class members.** It finds implementers of an *interface type or interface member* only. Overrides of an `abstract`/`virtual` class member come from `FindOverridesAsync`. Go to Implementation must call **both** and union the results (this is the confirmed design), so an abstract base method resolves to its overrides.
- **`FindDerivedClassesAsync` excludes interface implementations** (documented). So for an interface caret, "subclasses" means `FindDerivedInterfacesAsync` (sub-interfaces) — implementers stay under Go to Implementation. For a struct / enum / delegate / record-struct, there are no subclasses → empty result.
- **Map result symbols to in-source locations only.** Each result is an `ISymbol`; take `symbol.Locations.Where(l => l.IsInSource)` (a partial type yields several — that's fine) and skip metadata locations. Build each `FindHit` from the location's `GetLineSpan()` + the tree/document text line, mirroring the preview-line construction in `FindReferencesAsync`. Dedup identical `SourceLocation`s.
- **Roslyn stays inside `Services`.** The VM and view receive only `FindHit` / `SourceLocation`; no `ISymbol` crosses the service boundary (same rule the whole codebase draws, reaffirmed by safe-rename).
- **Kind-aware dimming is approximate by design.** Classifications can't see `abstract`/`sealed`/`virtual` modifiers or distinguish a declaration from a use, and before the classifier has run the covering set is empty. So a concrete method still lights Go to Implementation (it'll just find nothing), and a just-opened file lights both leniently. This matches `AllowSymbolActions`'s existing "empty → allow" leniency — do not try to make it exact (that needs the async semantic model, which must not run in the synchronous `Opening` handler).
- **Cold MSBuild on first invocation.** The first call pays the one-time workspace-load cost, like the first find-usages. Reuse the existing "Loading workspace (first use may take a while)…" status idiom.
- **Queries run against the fresh-disk snapshot.** Resolve and query through the existing `_solution` snapshot the reconcile keeps current; do not introduce any buffer overlay. No freshness/offset gate is needed here — unlike rename, these are read-only navigations, so the worst case of a stale offset is landing on the wrong token and listing its results, not a destructive write. Mirror `FindReferencesAsync`, which already has no freshness gate.

## Open Decisions
Defer to the implementer (raise with the user only if genuinely blocking):
1. **Single result → jump vs. list** — auto-navigate like Go to definition when exactly one result, or always list in the Find panel. Default: **always list** (KISS, one consistent surface); a single-result fast-path can be added later.
2. **Transitive depth for subclasses** — whole tree vs. immediate children. Default: **transitive** (matches VS "Find All Derived Types").
3. **Keybinding for Go to Implementation** — none (menu-only, per the request) vs. `Ctrl+F12` (VS-consistent) wired in `OnGlobalKeyDown`. Default: **none**; add only if desired. Go to Subclasses has no conventional default shortcut.
4. **Where the kind-gate lists live** — new `AllowImplementationActions` / `AllowSubclassActions` methods on `SymbolClassifications` (mirrors `AllowSymbolActions`) vs. two extra bools computed inline in `CodeSymbolContext`. Default: **methods on `SymbolClassifications`**, keeping the classification-name knowledge in one place.
5. **Menu labels & mnemonics** — e.g. "Go to _Implementation" / "Go to Su_bclasses". Default: those, avoiding mnemonic clashes with existing items.

## Acceptance Criteria
- [ ] The code-editor context menu contains **Go to Implementation** and **Go to Subclasses** items positioned after Go to definition, always present.
- [ ] **Go to Implementation** is enabled when the caret's cached classifications include an interface name or a member name (method / property / event), and disabled on locals, parameters, string literals, keywords, and non-C# tabs. Before the classifier has run (empty classifications) it is enabled (lenient), matching `AllowSymbolActions`.
- [ ] **Go to Subclasses** is enabled when the caret's cached classifications include a class, record-class, or interface name, and disabled otherwise (same lenient empty-set behavior).
- [ ] Invoking Go to Implementation on an interface type lists every in-source class/struct implementing it; on an interface member, every in-source member implementing it; on an `abstract`/`virtual` member, every in-source override — verified by a `WorkspaceService` unit test against a real on-disk fixture.
- [ ] Invoking Go to Subclasses on a class lists its in-source derived classes (transitively); on an interface, its in-source derived interfaces — verified by a unit test.
- [ ] Both queries return `null` when no symbol resolves under the caret (the Find panel shows "No symbol found"), and an **empty** list (not null) when a symbol resolves but has no implementations/subclasses (the panel shows "0 implementations" / "0 subclasses").
- [ ] Results appear in the Find panel with a status reading "N implementations" / "N subclasses" (via the generalized entry point + `Plural.Of`); selecting a result navigates to that location, and the Find tab is revealed on invocation.
- [ ] Only in-source locations are listed; a framework/metadata implementer or derived type contributes no entry.
- [ ] Search / Find usages / Go to definition / Rename and their existing (coarse) enablement are unchanged.

## Implementation

### 1. Confirm the `SymbolFinder` navigation APIs
Before writing the queries, restore and inspect the `5.6.0` `Microsoft.CodeAnalysis.Workspaces` assembly and confirm the overloads listed in Constraints — in particular whether `FindDerivedClassesAsync` / `FindDerivedInterfacesAsync` expose the `transitive` bool as a distinct overload, and that the `projects` set accepts `null`. Record the confirmed signatures in the new methods' doc-comments. If an overload is missing, adapt or halt and report.

### 2. Add the kind-aware classification gates
In `Services/SymbolClassifications.cs`, add two allowlists alongside `Resolvable`, using exact `ClassificationTypeNames` constants (no substring matching), and two predicate methods mirroring `AllowSymbolActions` (same "empty covering set → return true" leniency):
- **Implementation** allowlist: `InterfaceName`, `MethodName`, `PropertyName`, `EventName`. (Rationale: only these name something with implementations/overrides. Exclude `ExtensionMethodName` — extension methods aren't overridable.)
- **Subclasses** allowlist: `ClassName`, `RecordClassName`, `InterfaceName`. (Structs, record-structs, enums, delegates have no derived types.)

### 3. Extend `CodeSymbolContext`
In `Views/CodeSymbolContext.cs`, compute two more booleans in `At()` from the same `colorizer.ClassificationsAt(offset)` covering set already used for `SymbolEligible` — `ImplementationEligible` and `SubclassEligible` — each `= tab.Mode == HighlightMode.CSharp && identifier-char-under-caret && SymbolClassifications.AllowImplementation/SubclassActions(covering)`. Keep the existing `SymbolEligible` untouched. Add them to the record's parameters and `None`.

### 4. Add the `WorkspaceService` queries
In `Services/WorkspaceService.cs`, add `FindImplementationsAsync(filePath, position, ct)` and `FindSubclassesAsync(filePath, position, ct)`, each mirroring `FindReferencesAsync` exactly:
- Resolve via `ResolveSymbolAsync`; return `null` if no symbol.
- **Implementations**: union of `SymbolFinder.FindImplementationsAsync(symbol, _solution, projects: null, ct)` and — when the symbol is an overridable member (`symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride`) — `SymbolFinder.FindOverridesAsync(symbol, _solution, null, ct)`.
- **Subclasses**: if `symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } c` → `FindDerivedClassesAsync(c, _solution, transitive: true, null, ct)`; if `{ TypeKind: TypeKind.Interface } i` → `FindDerivedInterfacesAsync(i, _solution, transitive: true, null, ct)`; else return an empty list.
- Map each result `ISymbol`'s in-source locations to `FindHit`s through one shared private helper (line text for the preview, `ToSourceLocation` for the location), deduping identical `SourceLocation`s. Return the list (empty is a valid, non-null result). Follow CLAUDE.md defensive style — null-check the symbol, the document, and each location's `SourceTree`.

### 5. Generalize the Find panel entry point
In `ViewModels/FindResultsViewModel.cs`, generalize `ShowReferences(IReadOnlyList<FindHit>)` to take a result noun (e.g. `ShowResults(hits, noun)`), setting `Status = Plural.Of(hits.Count, noun)`. Update the single existing find-refs caller to pass `"reference"`. The panel stays origin-agnostic.

### 6. Add the ViewModel flow methods
In `ViewModels/MainWindowViewModel.cs`, add `FindImplementationsAsync(file, caretOffset)` and `FindSubclassesAsync(file, caretOffset)` mirroring `FindReferencesAsync`: no-solution guard, `EnsureWorkspaceReadyAsync` with the cold-load status message, call the workspace query, `null` → `Find.ShowNoResults("No symbol found")`, else `Find.ShowResults(results, "implementation" | "subclass")`; set `Status` from `Find.Status`.

### 7. Wire the menu items and handlers
In `Views/MainWindow.axaml`, add two named `MenuItem`s (e.g. `CtxGoToImplItem`, `CtxGoToSubclassesItem`) after `CtxGoToDefItem`, with labels/mnemonics per Open Decision 5 (add an `InputGesture` hint only if a shortcut is wired). In `Views/MainWindow.axaml.cs`: add click handlers mirroring `OnCtxFindUsagesClick` → a `FindImpls`/`FindSubclasses` helper that resolves the active editor + caret via `FindActiveEditor`, calls the VM method, then `ShowBottomTab(FindTab)`. In `OnCodeCtxOpening`, add the two cases to the switch, enabling each from `context.ImplementationEligible` / `context.SubclassEligible` respectively (do not fold them into the shared `SymbolEligible` case). Optionally wire `Ctrl+F12` in `OnGlobalKeyDown` (Open Decision 3).

### 8. Update `docs/roslyn.md`
Add two rows to the API-map table: Go to Implementation → `SymbolFinder.FindImplementationsAsync` (+ `FindOverridesAsync`); Go to Subclasses → `FindDerivedClassesAsync` / `FindDerivedInterfacesAsync`. Note the interface-vs-class split and the in-source-only filtering as one or two fragment lines, matching the file's terse style.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes.
- [ ] **Implementations unit test** (mirror `WorkspaceServiceTests` fixtures: temp on-disk solution, xunit + Shouldly, offsets via `IndexOf`). Fixture with `interface IFoo { void Bar(); }`, a class `A : IFoo` and a class `B : IFoo`, plus an `abstract class Base { public abstract void Baz(); }` with `Derived : Base`. Assert: caret on `IFoo` lists `A` and `B`; caret on `IFoo.Bar` lists both `Bar` implementations; caret on `Base.Baz` lists `Derived.Baz` (exercises the `FindOverridesAsync` union).
- [ ] **Subclasses unit test**: fixture with `class Animal`, `class Dog : Animal`, `class Puppy : Dog`, and `interface IShape` / `interface IRound : IShape`. Assert: caret on `Animal` lists `Dog` and `Puppy` (transitive); caret on `IShape` lists `IRound`.
- [ ] **No-symbol test**: caret on a string literal / whitespace → both queries return `null`.
- [ ] **Empty-result test**: caret on a `sealed`/leaf class with no derived types, or an interface with no implementers → returns an empty (non-null) list.
- [ ] **In-source-only test**: caret on a type implementing a framework interface (e.g. `IDisposable`) → Go to Implementation from the *framework* interface side lists only the in-source implementer(s), no metadata entries. (If resolving a framework symbol under the caret, confirm no metadata locations leak in.)
- [ ] **Manual — happy path**: open a solution, right-click an interface name → confirm Go to Implementation is enabled and lists implementers in the Find panel with "N implementations"; selecting one navigates. Right-click a base class → Go to Subclasses lists derived types.
- [ ] **Manual — dimming**: right-click a local variable / parameter → both items dimmed; right-click a class name → Go to Subclasses enabled, Go to Implementation dimmed (unless the classifier hasn't run); right-click in a non-C# tab → both dimmed.
- [ ] **Regression**: Search / Find usages / Go to definition / Rename still work and their enablement is unchanged.

## Learnings

### Verification status
Unlike the safe-rename ticket (which ran headless with no SDK), this ran on a Windows box with the .NET SDK
present. `dotnet build` (MiniIde) and `dotnet test` (**50 passed, 0 failed** — including the 9 new
`WorkspaceNavigationTests`) both succeeded. The **automated** Test-Plan items are all covered by
`WorkspaceNavigationTests`. The **manual GUI** items (menu dimming, right-click → Find panel, click-to-navigate)
were **not** exercised — driving Avalonia context menus programmatically is out of proportion to the change, and
the menu wiring is validated indirectly by the compiled-bindings axaml build resolving the new named items and
`Click`/`Opening` handlers. A human should still run the three manual items once.

### API confirmation (done against the restored DLL, not from memory)
Reflected the `5.6.0` `Microsoft.CodeAnalysis.Workspaces.dll` surface via a `MetadataLoadContext` throwaway
before writing any query. Confirmed:
- `FindImplementationsAsync(ISymbol, Solution, IImmutableSet<Project>? projects = null, CancellationToken = default)` — the `ISymbol` overload (a separate `INamedTypeSymbol, …, bool transitive, …` overload also exists; the `ISymbol`-typed argument binds to the former unambiguously).
- `FindOverridesAsync(ISymbol, Solution, IImmutableSet<Project>? = null, CancellationToken = default)`.
- `FindDerivedClassesAsync` / `FindDerivedInterfacesAsync(INamedTypeSymbol, Solution, bool transitive = <opt>, IImmutableSet<Project>? = null, CancellationToken = default)` — `transitive` **is** a defaulted parameter (passed named `transitive: true`), not a distinct overload for interfaces; `FindDerivedClassesAsync` has both a no-`transitive` overload and the `transitive` one.
- `projects` is optional/nullable on all four → `projects: null` searches the whole solution.

### Architectural decisions
- **Union guard narrowed to member kinds (deviation from the literal ticket condition).** The ticket said union
  `FindOverridesAsync` "when the symbol is an overridable member (`symbol.IsAbstract || symbol.IsVirtual ||
  symbol.IsOverride`)". Taken literally, an **interface or abstract *type*** also reports `IsAbstract == true`,
  which would fire a needless whole-solution overrides scan and pass a *type* symbol where `FindOverridesAsync`
  expects a member. Guarded to `symbol is IMethodSymbol or IPropertySymbol or IEventSymbol && (…IsAbstract…)`.
  Strictly correct (only members have overrides) and cheaper; the `IFoo`-type test confirms the type path still
  returns just its implementers.
- **Open Decision 4 → predicate methods on `SymbolClassifications`.** Added `AllowImplementationActions` /
  `AllowSubclassActions` beside `AllowSymbolActions`, each backed by its own allowlist (`Implementable` =
  Interface/Method/Property/Event; `Subclassable` = Class/RecordClass/Interface). Factored the three predicates'
  shared "empty → allow, else any-match" body into one private `Allow(covering, allowlist)` — keeps the
  classification-name knowledge in one file and the leniency identical across all three.
- **Open Decision 1 → always list; Decision 2 → transitive; Decision 3 → no keybinding.** All defaults kept. No
  `Ctrl+F12` wired (menu-only per the request).
- **One shared `MapSymbolsToHitsAsync` helper** maps result `ISymbol`s → `FindHit`s (in-source locations only,
  deduped on `SourceLocation`), so implementations and subclasses can't drift in how they build previews. Preview
  line text comes from `loc.SourceTree.GetTextAsync()` (not a `FindDocument` round-trip) — the tree is already in
  hand on the location.
- **`ShowReferences` → `ShowResults(hits, noun, pluralSuffix = "s")`.** The ticket's suggested `ShowResults(hits,
  noun)` + `Plural.Of(count, noun)` would render "0 subclasss" — `Plural.Of` already takes a suffix, so the pass-
  through `pluralSuffix` param ("subclass" + "es") was the pit-of-success fix. Find usages passes `"reference"`
  (default "s"); implementations `"implementation"`; subclasses `"subclass", "es"`.

### Interesting tidbits
- **"No symbol" is genuinely hard to hit.** A caret on almost any word resolves *something* (even `namespace Lib`
  resolves the namespace). The reliable null case is a caret **inside a string literal** — used for the
  both-queries-return-null test, mirroring `FindReferences`'s numeric-literal trick.
- **`FindDerivedClassesAsync` excludes interface implementations by design**, so an interface caret's "subclasses"
  are its derived *interfaces* (`FindDerivedInterfacesAsync`); implementers stay under Go to Implementation. The
  two commands intentionally cover disjoint sets — no dedup between them (Scope: out).
- **Framework interfaces don't leak metadata implementers.** `FindImplementationsAsync(IDisposable, solution)`
  only searches the solution's source, and `MapSymbolsToHitsAsync` filters `IsInSource` on top — so the
  `IDisposable` test lists only the in-source `Res`, zero metadata entries.

### Related areas affected
- `FindResultsViewModel.ShowReferences` renamed to `ShowResults` (the sole find-refs caller updated); the panel
  stays origin-agnostic. `CodeSymbolContext` grew two record fields (`ImplementationEligible`,
  `SubclassEligible`) and its `None` constant — every construction site is the single `At()` factory.

### Rejected alternatives
- **Folding the two items into the shared `SymbolEligible` menu case** — rejected: their dimming is kind-aware
  (interfaces/members vs. class/record/interface), so each gets its own `case` reading its own eligibility bool.
- **A `switch` expression with `await` arms in `FindSubclassesAsync`** — used explicit `if/else` instead; the
  best-common-type across `IEnumerable<INamedTypeSymbol>` arms and an empty `ISymbol[]` is fiddly, and the
  `if/else` reads plainly.
