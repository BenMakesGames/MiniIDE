# Find Panel — literal-search defaults & symbol-search context banner

## Context
**Current behavior**: The Find panel (`Views/MainWindow.axaml`, the `FindTab` grid bound to `Find`) always shows one input row — a query `TextBox`, a Search button, and Regex / Case-sensitive checkboxes — above the results list and a status line. Every result set, whether from a text search (`FindResultsViewModel.SearchAsync`) or a symbol query (find-usages, and — after the prerequisite ticket — go-to-implementation / go-to-subclasses, all via `ShowResults`), fills the same list with the input row unchanged. Right-clicking a token → "Search solution for 'X'" (and Ctrl+Shift+F on a word) both run `MainWindow.TrySearchTermInEditor`, which forces `UseRegex = false` and sets the query, but does not touch Case-sensitive.

**New behavior**: Two Find-panel refinements.
1. **Literal search from the right-click "Search solution" item** additionally turns **Case-sensitive on** and (as today) **Regex off** — a clicked token is an exact literal. The **Ctrl+Shift+F** shortcut no longer forces either checkbox; it sets only the query and keeps the user's current Regex / Case-sensitive state (a behavior change from today's forced regex-off).
2. **A symbol/navigation search** (find-usages, implementations, subclasses — anything not a plain-text search) **replaces** the input row (text box, Search button, checkboxes) with a **context banner**: a close button on the left followed by descriptive text (e.g. `Usages of "Foo"`). Clicking close reverts to the normal input row with **all inputs cleared** (query blank, both checkboxes off) while **leaving the results list in place**.

## Prerequisites
- **`go-to-implementation-and-subclasses`** (`docs/tickets/go-to-implementation-and-subclasses.md`). That ticket generalizes the Find panel's result entry point to `ShowResults(hits, noun)` and adds the `FindImplementationsAsync` / `FindSubclassesAsync` VM flow methods + their view handlers. This ticket extends that same `ShowResults` into the context-banner entry point and adds the banner subject to all three symbol-query flows, so it must land second. If it hasn't landed, `ShowResults` and the impl/subclasses paths this ticket edits won't exist.

## Scope
### In scope
- Splitting `MainWindow.TrySearchTermInEditor` so the checkbox defaults apply on the **context-menu** path only, not the Ctrl+Shift+F path.
- A **context-result mode** on `FindResultsViewModel`: an observable flag + banner-label string, a `CloseContext` command, and routing all symbol queries through it while the text search (`SearchAsync`) stays in normal mode.
- Plumbing the **searched-for subject** (the clicked identifier) from the view handlers through the three symbol-query VM methods into the banner label.
- The **XAML swap**: the existing input `DockPanel` and a new banner panel share Find-panel row 0, mutually exclusive via `IsVisible`.

### Out of scope
- Changing text-search behavior itself (regex/case semantics of `SearchService`), the results-list template, navigation-on-select, or the status line's count wording.
- Re-running or altering results when the banner is closed — results are left exactly as they were.
- A banner for the **no-symbol** outcome (`ShowNoResults`) — when nothing resolves under the caret the panel stays in normal mode with a plain status (see Open Decisions).
- Persisting checkbox state across sessions, or any settings UI.

## Relevant Docs & Anchors
Read before coding:
- **`docs/avalonia.md`** — `{Binding !SomeBool}` negates inline (a two-state area is two elements, `IsVisible="{Binding X}"` / `IsVisible="{Binding !X}"`, **no converter**). This is exactly the input-vs-banner swap. Also the `ObjectConverters.IsNotNull` note (already used in this file) for reference.
- **`docs/tickets/go-to-implementation-and-subclasses.md`** (prerequisite) — the `ShowResults(hits, noun)` generalization and the impl/subclasses VM methods + handlers this ticket extends.
- **`docs/tickets/complete/2026-07-05 code-view-context-menu.md`** — how the "Search solution" item and `TrySearchTermInEditor` / Ctrl+Shift+F sharing were built.
- **Code anchors**:
  - `ViewModels/FindResultsViewModel.cs` — `SearchAsync` (text path), `ShowResults` (symbol path, from the prereq), `ShowReferences`/`ShowNoResults`, the `[ObservableProperty]` fields (`Query`/`UseRegex`/`CaseSensitive`/`Status`), `Plural.Of`, and the `[RelayCommand]` pattern (CommunityToolkit MVVM) for `SearchCommand` — mirror it for the new close command.
  - `Views/MainWindow.axaml.cs` — `TrySearchTermInEditor` (the method to split), `OnCtxSearchClick` (context-menu caller), the Ctrl+Shift+F branch in `OnGlobalKeyDown` (the other caller), `FindRefsAsync` + the impl/subclasses handlers (from the prereq) that must pass the subject, `CodeSymbolContext.At(editor, _codeEditors.StateFor(editor)).Term` (the subject source), `FocusFind`.
  - `Views/MainWindow.axaml` — the `FindTab` grid `RowDefinitions="Auto,*,Auto"` with `DataContext="{Binding Find}"`; row 0 is the input `DockPanel` (Regex / Case-sensitive checkboxes, Search button, `FindBox`). This is what the banner swaps against.
  - `ViewModels/MainWindowViewModel.cs` — `FindReferencesAsync` and (from the prereq) `FindImplementationsAsync` / `FindSubclassesAsync`; the `Find` property.

## Constraints & Gotchas
- **Don't mutate checkboxes when no search runs.** `TrySearchTermInEditor` returns `false` (no term / no solution) and the Ctrl+Shift+F path then just focuses the box. When splitting, set the context-menu checkbox defaults **only when a term will actually be searched** — i.e. after the guard passes — so a failed context-menu invoke doesn't silently flip the user's checkboxes.
- **Ctrl+Shift+F is an intentional behavior change.** Today it forces `UseRegex = false`; after this ticket it leaves both checkboxes as the user set them. Call this out in the code comment where the shared core drops the checkbox writes, so it doesn't read as a regression.
- **Inline `!` binding, not a converter** (per `docs/avalonia.md`) for the input-vs-banner `IsVisible` swap. No `IValueConverter`.
- **Roslyn stays inside `Services`** (unchanged rule): the banner subject is the view-side clicked identifier text (`CodeSymbolContext.Term`), a plain string — no `ISymbol` crosses into the VM/view. See Open Decisions for the subject-source choice.
- **`SearchAsync` must leave context mode.** A text search after a symbol search (or after closing the banner) must clear the context flag so the input row is the one shown — set it alongside the existing `Results.Clear()`.

## Open Decisions
Defer to the implementer (raise with the user only if genuinely blocking):
1. **Banner copy** — the label phrasings and the count-noun for the status. Default: banner `Usages of "X"` / `Implementations of "X"` / `Subclasses of "X"`; status nouns `reference` / `implementation` / `subclass` (so status reads "3 references", banner reads `Usages of "Foo"`). Matches the user's `Usages of "bla-bla-bla"` example.
2. **Banner subject source** — the clicked identifier text (`CodeSymbolContext.Term`, view-side, trivially available on every invoke path) vs. the resolved symbol's `Name` (more accurate for edge cases, but not currently surfaced from the service). Default: **clicked identifier text**; it's what the user sees and matches the example.
3. **Split shape** — a `bool` parameter on `TrySearchTermInEditor` gating the checkbox writes vs. a shared core method + a context-menu-only wrapper. Default: whichever reads cleanest; both are fine as long as the guard-ordering gotcha holds.
4. **Close-button glyph & styling** — a text `✕`, the icon font (`{StaticResource IconFont}`, see the NuGet/tree items), or a bare button. Default: minimal, matching the panel's dark styling; implementer's eye.
5. **Status after close** — keep the count status describing the still-shown results, or blank it. Default: **keep** (the results are still there; the status still describes them).

## Acceptance Criteria
- [ ] Right-clicking a token and choosing "Search solution for 'X'" runs the search with **Regex off and Case-sensitive on**, and the query populated with the token — regardless of the checkboxes' prior state.
- [ ] Pressing **Ctrl+Shift+F** with a word/selection under the caret sets the query to that term and searches **without changing** the Regex or Case-sensitive checkboxes; with no term under the caret it still just focuses the search box and changes nothing.
- [ ] Running any symbol query (find-usages, go-to-implementation, go-to-subclasses) puts the panel in **context mode**: the query box, Search button, and both checkboxes are hidden and replaced by a banner showing a close affordance on the left and descriptive text naming what was searched (e.g. `Usages of "Foo"`); the results list and status show that query's results.
- [ ] Running a **plain-text** search (Search button, Enter in the box, right-click item, or Ctrl+Shift+F) shows the **normal input row** (not the banner), even immediately after a symbol query.
- [ ] Clicking the banner's close button returns the panel to the normal input row with the **query blank and both checkboxes off**, and the **results list unchanged** from the symbol query.
- [ ] A symbol query that resolves a symbol but finds **zero** results still shows the banner (e.g. `Implementations of "IFoo"`) with a "0 …" status; a query where **no symbol resolves** shows the normal input row with a "No symbol found" status and **no** banner.
- [ ] `FindResultsViewModel` exposes a context-mode flag, a banner-label string, and a close command; `ShowResults` sets the flag/label and `SearchAsync` clears the flag (verifiable by inspection / VM unit test).

## Implementation

### 1. Split the literal-search checkbox defaults onto the context-menu path
In `Views/MainWindow.axaml.cs`, change `TrySearchTermInEditor` so its shared body sets **only** `Vm.Find.Query = term`, executes `SearchCommand`, focuses, and returns the bool — dropping the current unconditional `UseRegex = false`. Have `OnCtxSearchClick` apply the literal defaults (`UseRegex = false`, `CaseSensitive = true`) **only when a term will run** (after the same guard), via a `bool` parameter or a thin wrapper (Open Decision 3). Leave the Ctrl+Shift+F branch in `OnGlobalKeyDown` calling the shared core so it no longer forces checkbox state. Add a one-line comment noting the deliberate Ctrl+Shift+F change.

### 2. Add context-result state to `FindResultsViewModel`
Add an `[ObservableProperty]` bool (context-mode flag) and an `[ObservableProperty]` string (banner label), both defaulting to normal/empty. Follow the existing `[ObservableProperty]` style in the class.

### 3. Route symbol queries through the banner, text search through normal mode
Extend `ShowResults` (from the prereq) to also take the banner label, set the context flag true, set the label, and keep setting `Results` + `Status` (`Plural.Of(count, noun)`) as before. In `SearchAsync`, clear the context flag (and label) where it clears `Results`, so a text search always shows the input row. Leave `ShowNoResults` in normal mode (no banner) — it's the no-symbol outcome.

### 4. Add the close command
Add a `[RelayCommand]` (e.g. `CloseContext`) that clears the context flag and label, and resets the inputs — `Query = ""`, `UseRegex = false`, `CaseSensitive = false` — **without** touching `Results` or `Status` (Open Decision 5: keep status). Mirror the `SearchCommand` relay-command style.

### 5. Plumb the searched-for subject into the banner label
Give the three symbol-query VM methods (`FindReferencesAsync`, and the prereq's `FindImplementationsAsync` / `FindSubclassesAsync`) a subject parameter (the clicked identifier). Each builds its banner label (Open Decision 1 copy) and passes it plus the count-noun to `ShowResults`. In the view handlers (`FindRefsAsync` and the impl/subclasses handlers), compute the subject with `CodeSymbolContext.At(editor, _codeEditors.StateFor(editor)).Term` — the same primitive the search path uses — and pass it in. Null/empty subject is acceptable (label degrades gracefully, e.g. quotes around an empty string); the count/results are unaffected.

### 6. XAML — swap the input row for the banner
In `Views/MainWindow.axaml`, in the `FindTab` grid, keep the existing input `DockPanel` at row 0 and add `IsVisible="{Binding !IsContextResult}"` (exact property name per step 2). Add a sibling element at the same `Grid.Row="0"` — a horizontal panel with the close `Button` (`Command="{Binding CloseContextCommand}"`, on the **left**) followed by a `TextBlock` bound to the banner label (with `TextTrimming="CharacterEllipsis"` so a long subject can't overflow) — bound `IsVisible="{Binding IsContextResult}"`. Match the panel's dark styling; close-button glyph per Open Decision 4.

### 7. Update docs if warranted
If `docs/` documents the Find panel's behavior (e.g. `docs/global-find.md`), add a fragment noting the literal-search checkbox defaults and the symbol-search context banner. Keep it terse, matching the file's style.

## Test Plan
- [ ] `dotnet build` succeeds and `dotnet test` passes.
- [ ] **VM unit test** (if `FindResultsViewModel` constructs cheaply in isolation — its ctor takes `SearchService`, `SolutionService`, `Func<SourceLocation,Task>`; the synchronous state methods don't exercise search): `ShowResults` sets the context flag + label + status; `CloseContext` clears the flag/label/query/checkboxes but leaves `Results` intact; `ShowNoResults` leaves the flag false. If isolation is impractical, cover these manually and note it.
- [ ] **Manual — literal search defaults**: with Regex checked and Case-sensitive unchecked, right-click a token → "Search solution for 'X'"; confirm Regex is now off, Case-sensitive on, query populated, results shown in normal-input mode.
- [ ] **Manual — Ctrl+Shift+F unchanged checkboxes**: set Regex on / Case-sensitive on, put the caret on a word, press Ctrl+Shift+F; confirm it searches that word and **leaves both checkboxes as they were**. With the caret on whitespace, confirm it just focuses the box.
- [ ] **Manual — banner appears**: right-click a symbol → Find usages; confirm the input row is replaced by `Usages of "<name>"` with a close button on the left, and the results/status reflect the usages. Repeat for Go to Implementation and Go to Subclasses (banner text `Implementations of …` / `Subclasses of …`).
- [ ] **Manual — close reverts, keeps results**: with the banner showing, click close; confirm the normal input row returns with an empty query and both checkboxes off, and the results list is unchanged.
- [ ] **Manual — text search after symbol search**: run a symbol query (banner shows), then run a plain-text search (via close→type→Search, or the right-click item); confirm the normal input row is shown and results update.
- [ ] **Manual — zero vs. no-symbol**: invoke Go to Implementation on an interface with no implementers → banner shows with a "0 implementations" status; invoke a symbol action on a non-symbol (string literal) → normal input row with "No symbol found", no banner.
- [ ] **Regression**: plain-text search via the Search button and Enter-in-box still work; navigation-on-select still opens hits.

## Learnings

### Verification status
`dotnet build` (MiniIde) and `dotnet test` both pass — **54 passed, 0 failed**, including the 4 new
`FindResultsViewModelTests`. The **automated** VM Test-Plan item is fully covered. The **manual GUI** items
(right-click checkbox defaults, Ctrl+Shift+F checkbox preservation, the banner appearing/closing, zero-vs-no-
symbol) were **not** driven — same call as the prerequisite (`go-to-implementation-and-subclasses`): scripting
Avalonia context menus + checkbox observation is out of proportion to the change. The XAML wiring is validated
indirectly by the axaml build resolving the new `IsContextResult` / `ContextLabel` / `CloseContextCommand`
bindings. A human should run the manual items once.

### Architectural decisions
- **Open Decision 3 (split shape) → `bool literalDefaults = false` parameter on `TrySearchTermInEditor`**, not a
  wrapper. The context-menu caller passes `literalDefaults: true`; the Ctrl+Shift+F caller passes nothing
  (false). One method, one guard, defaults applied only past the guard — the guard-ordering gotcha holds by
  construction (a failed invoke returns before any checkbox write).
- **`ShowResults` gained `bannerLabel` before the defaulted `pluralSuffix`.** Signature is now
  `ShowResults(hits, noun, bannerLabel, pluralSuffix = "s")`. Every symbol path builds its own label
  (`Usages of "X"` / `Implementations of "X"` / `Subclasses of "X"`) in the VM flow method and passes it
  through — the panel stays origin-agnostic, only now it also carries the banner text.
- **`ShowNoResults` defensively clears context mode** (sets `IsContextResult = false`), not just "leaves it
  false". A banner from query A followed by a no-symbol query B must revert to the input row — clearing handles
  that transition. On a fresh VM it's a no-op, so the "leaves the flag false" contract still holds.
- **Open Decisions 1/2/4/5 kept at defaults**: banner copy `Usages/Implementations/Subclasses of "X"`; subject =
  clicked identifier text (`CodeSymbolContext.Term`, view-side, no `ISymbol` crosses into the VM); close glyph a
  bare transparent `✕` `Button`; status kept on close (results are still shown).

### Interesting tidbits
- **The subject is the same primitive the Search path already uses** — `CodeSymbolContext.At(editor, state).Term`.
  Factored the three handlers' identical call into one `SubjectAt(editor)` helper. Null/empty degrades to
  `Usages of ""`, which is acceptable (count/results unaffected).
- **`FindResultsViewModel` constructs cheaply in isolation** — `new SearchService()`, `new SolutionService()`,
  and a `_ => Task.CompletedTask` open callback. The synchronous state methods (`ShowResults`, `ShowNoResults`,
  `CloseContext`) never touch search/disk, so the VM test needed no fixture or mocking.

### Related areas affected
- The three VM flow methods (`FindReferencesAsync` / `FindImplementationsAsync` / `FindSubclassesAsync`) grew a
  `string? subject` parameter — the only callers are the three view handlers (+ Shift+F12 via `FindRefsAsync`),
  all updated. The `WorkspaceService.FindReferencesAsync` service method is a distinct overload and was
  untouched.
- `docs/global-find.md` gained a "Find panel input row" section documenting the literal-search defaults and the
  context banner.

### Rejected alternatives
- **A converter for the input-vs-banner swap** — rejected per `docs/avalonia.md`: two sibling elements on
  `Grid.Row="0"` gated inline with `{Binding !IsContextResult}` / `{Binding IsContextResult}`, no
  `IValueConverter`.
