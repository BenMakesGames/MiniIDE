# Global find

In-process via `BenMakesGames.FileGrepper` (`src/BenMakesGames.FileGrepper/`). No external CLI dep — no `rg`, no PATH detection.

- Grep engine only: parallel dir walk + per-file text scan. No `.gitignore` / ignore semantics.
- Regex checkbox → `GrepOptions.Regex`. Regex uses .NET `Regex` with `NonBacktracking` (flavor differs from rg's Rust regex — accepted).
- Literal mode → `Span.IndexOf`; regex mode → `Regex.Match`. UTF-8 only (BOM sniff); non-UTF-8 / binary (NUL in first 8 KB) files skipped.
- Streams `IAsyncEnumerable<GrepHit>` under a `CancellationToken`; new search cancels the prior one.

Exclusions live in the **caller**, not the lib. IDE-side skip list (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages`) is hardcoded in `Services/SearchService.cs` and passed as a `SkipDirectory` predicate over full paths.

Roslyn only earns keep for **symbol-aware** search — that's find-usages, covered by `SymbolFinder`.
