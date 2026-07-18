# Global find

In-process via `BenMakesGames.FileGrepper` (`src/BenMakesGames.FileGrepper/`). No external CLI dep — no `rg`, no PATH detection.

- Grep engine only: parallel dir walk + per-file text scan. No `.gitignore` / ignore semantics.
- Regex checkbox → `GrepOptions.Regex`. Regex uses .NET `Regex` with `NonBacktracking` (flavor differs from rg's Rust regex — accepted).
- Literal mode → `Span.IndexOf`; regex mode → `Regex.Match`. UTF-8 only (BOM sniff); non-UTF-8 / binary (NUL in first 8 KB) files skipped.
- Streams `IAsyncEnumerable<GrepHit>` under a `CancellationToken`; new search cancels the prior one.

Exclusions live in the **caller**, not the lib. IDE-side skip list is the shared `IdeDirectories.Pruned` set (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages` — see `docs/file-classification.md`); `SearchService` passes it as a `SkipDirectory` predicate over full paths. Same set gates the solution-tree walk, so tree and find can't drift.

Roslyn only earns keep for **symbol-aware** search — that's find-usages, covered by `SymbolFinder`.

## Find panel input row

- **Right-click "Search solution for X"** treats the clicked token as an exact literal: forces Regex **off** and Case-sensitive **on** (`TrySearchTermInEditor(literalDefaults: true)`). Defaults apply only past the term/solution guard, so a failed invoke never flips checkbox state.
- **Ctrl+Shift+F** on a word sets only the query and **keeps** the user's Regex / Case-sensitive state (no forced checkboxes).
- A **symbol query** (find-usages / implementations / subclasses) swaps the input row for a **context banner** — a close `✕` + `Usages of "X"` / `Implementations of "X"` / `Subclasses of "X"` — driven by `FindResultsViewModel.IsContextResult`. Close (`CloseContextCommand`) clears the inputs but keeps the results/status; a text search (`SearchAsync`) leaves context mode. Gated with inline `{Binding !X}` / `{Binding X}`, no converter.
