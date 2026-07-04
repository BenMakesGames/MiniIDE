# Global find

Shell `rg` (ripgrep). Not Roslyn.

- Roslyn parses everything → orders slower for text search.
- Regex checkbox → `--regex` flag.
- Detect `rg` on PATH; fall back to `System.Text.RegularExpressions` + file walk if missing.

Roslyn only earns keep for **symbol-aware** search — that's find-usages, covered by `SymbolFinder`.
