# File Grepper Library

## Context
**Current behavior**: Global find shells out to `rg` (ripgrep) via `Services/SearchService.cs`. Users without `rg` on `PATH` get a `Win32Exception` on Search click: *"The system cannot find the file specified."*

**New behavior**: Global find runs in-process via a new managed grep library, `BenMakesGames.FileGrepper`. No external CLI dep. IDE hardcodes a skip list (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages`) and passes it to the lib. The lib itself has no baked-in prune list — pure grep engine, caller decides exclusions.

## Scope
### In scope
- New csproj `src/BenMakesGames.FileGrepper/BenMakesGames.FileGrepper.csproj` in the solution.
- Public API: `FileGrepper.GrepAsync` streaming `IAsyncEnumerable<GrepHit>`, `GrepOptions` record with regex/case toggles and skip predicates.
- Managed impl: parallel dir walk (`System.IO.Enumeration.FileSystemEnumerable`), per-file NUL sniff for binary skip, UTF-8 decode, literal via `Span.IndexOf` or regex via `RegexOptions.NonBacktracking`.
- Wire `MiniIde.csproj` → `ProjectReference` to the new lib.
- Rewrite `Services/SearchService.cs` to wrap `FileGrepper`, build hardcoded skip predicates, map `GrepHit` → `FindHit`.
- Update `MiniIde.slnx` to include the new project.
- Refresh `docs/global-find.md` — drop rg reference, describe new lib.

### Out of scope
- `.gitignore` parsing (spin-off ticket).
- Test project for the lib (spin-off ticket).
- NuGet packaging / publishing.
- Case-sensitivity, glob include/exclude, whole-word — no UI toggles for these yet.
- Encoding beyond UTF-8 (with BOM detect).
- Multi-line regex spanning line breaks.
- Symlink loop protection beyond skipping reparse points.

## Relevant Docs & Anchors
- **Design docs**:
  - `docs/global-find.md` — spec being replaced; update as part of this ticket.
  - `docs/stack.md` — global-find line references `rg`; update to reflect new lib.
- **Related tickets**:
  - `docs/tickets/complete/2026-07-04 top-bar-and-explorer-flatten.md` — example of Learnings section shape and how prior tickets record Open Decision outcomes.
- **Code anchors**:
  - `Services/SearchService.cs` — current `Process.Start("rg")` impl; the entire class gets replaced.
  - `Models/FindHit.cs` — record consumed by the UI layer; stays put, unchanged.
  - `ViewModels/FindResultsViewModel.SearchAsync` — sole consumer of `SearchService.SearchAsync`. Streams hits under cancellation. API shape must be preserved.
  - `ViewModels/MainWindowViewModel` — constructs `SearchService` (`Search = new SearchService();`). Constructor of the new `SearchService` must remain parameterless (or wire through DI if the impl needs a `FileGrepper` instance).

## Constraints & Gotchas
- **Regex flavor change**: rg used the Rust `regex` crate; new impl uses .NET `Regex`. Some patterns will behave differently (Unicode classes, escapes). This is an accepted user-facing behavior change — do not attempt to translate patterns.
- **Column semantics change**: rg reported byte-based column; managed impl reports char-based column. `FindHit.Column` consumers pass it to the AvaloniaEdit caret, which is char-based — new value is actually more correct. No mapping needed.
- **Encoding**: UTF-8 only, with BOM sniff. Files that fail UTF-8 decode should be skipped silently, not throw.
- **Binary skip**: sample first 8 KB; presence of NUL byte = skip. Keep the sample buffer small — most binary files fail on byte 0–4.
- **Parallelism**: use `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = Environment.ProcessorCount`. Emit hits via `Channel<GrepHit>` so streaming works under fan-out.
- **Cancellation**: every long-running loop (dir walk, per-file scan, channel read) must honor `CancellationToken`. Current UI cancels on new search — do not regress this.
- **Skip predicates run on full paths**: caller passes full-path predicates, not relative. Simplifies lib; keeps caller in charge of root semantics.
- **Symlinks**: `FileSystemEnumerable.EnumerationOptions.AttributesToSkip` should include `ReparsePoint` to avoid loop hazards on Windows junction points.
- **TFM**: lib targets `net10.0` to match IDE. Nullable enabled, `LangVersion` matching IDE.
- **Project reference direction**: `MiniIde` → `BenMakesGames.FileGrepper`. Lib depends on nothing in `MiniIde`.

## Open Decisions
1. **`SearchService` constructor injection** — keep `new SearchService()` parameterless and construct `FileGrepper` inside, or inject `FileGrepper` and update `MainWindowViewModel`. Default: parameterless — matches existing DI style, `FileGrepper` is stateless.
2. **Channel bounded vs unbounded** — bounded gives back-pressure; unbounded is simpler for MVP. Default: unbounded — hit volume is small (typed queries, human latency).
3. **Regex cache** — cache last-compiled `Regex` on `FileGrepper` instance for repeat queries, or compile fresh each call. Default: compile fresh; caching is premature optimization for interactive search.
4. **Line preview trim** — mirror the current `TrimEnd('\n', '\r')`. Default: yes, preserves existing UI.

## Acceptance Criteria
- [ ] `src/BenMakesGames.FileGrepper/BenMakesGames.FileGrepper.csproj` exists; targets `net10.0`; nullable enabled.
- [ ] `MiniIde.slnx` lists the new project alongside `MiniIde`.
- [ ] `MiniIde.csproj` has a `<ProjectReference>` to `BenMakesGames.FileGrepper.csproj`.
- [ ] `BenMakesGames.FileGrepper` public surface exposes at minimum: `FileGrepper` class with `GrepAsync(string root, string pattern, GrepOptions options, CancellationToken ct)` returning `IAsyncEnumerable<GrepHit>`; `GrepOptions` record with `Regex`, `CaseSensitive`, `SkipDirectory`, `SkipFile`; `GrepHit` record with `File`, `Line`, `Column`, `Preview`.
- [ ] `Services/SearchService.cs` no longer references `System.Diagnostics.Process` and does not spawn `rg`.
- [ ] `SearchService.SearchAsync(string root, string query, bool regex, CancellationToken ct)` signature unchanged from the consumer's perspective (`FindResultsViewModel.SearchAsync` compiles without change).
- [ ] Running Search on a solution root with `rg` NOT installed produces results (no `Win32Exception`).
- [ ] Cancelling an in-flight search (starting a new one) surfaces `OperationCanceledException` to `FindResultsViewModel`, matching prior behavior.
- [ ] Directories in the hardcoded skip list (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages`) are not descended into during search.
- [ ] Binary files (containing NUL in first 8 KB) return zero hits regardless of query.
- [ ] `docs/global-find.md` describes the new lib-based approach; no longer references `rg` or PATH detection.

## Implementation

### 1. Add the FileGrepper project
Create `src/BenMakesGames.FileGrepper/BenMakesGames.FileGrepper.csproj`. `Microsoft.NET.Sdk`, `TargetFramework=net10.0`, `Nullable=enable`, `LangVersion` matching IDE (implicit from TFM is fine). No package references needed — everything lives in BCL. Register the project in `MiniIde.slnx` next to `src/MiniIde/MiniIde.csproj`.

### 2. Define public API surface
In the new project, add three files:
- `GrepHit.cs`: `public sealed record GrepHit(string File, int Line, int Column, string Preview);`
- `GrepOptions.cs`: `public sealed record GrepOptions(bool Regex = false, bool CaseSensitive = true, Predicate<string>? SkipDirectory = null, Predicate<string>? SkipFile = null);` — predicates receive full paths.
- `FileGrepper.cs`: `public sealed class FileGrepper` with `public IAsyncEnumerable<GrepHit> GrepAsync(string rootPath, string pattern, GrepOptions options, CancellationToken ct = default)`.

### 3. Implement directory walk
Inside `FileGrepper.GrepAsync`, enumerate files via `System.IO.Enumeration.FileSystemEnumerable<string>` with a custom transform (`FindTransform`) returning full paths. Use `EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }`. Set `ShouldRecursePredicate` to invoke `options.SkipDirectory` on the entry's full path and return false when the predicate reports skip. Set `ShouldIncludePredicate` to filter files via `options.SkipFile`.

### 4. Fan out per-file scan
Wrap the enumerable in `Parallel.ForEachAsync(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, async (path, token) => { ... })`. Each file scan writes hits into a `Channel<GrepHit>.CreateUnbounded()`. Await the Parallel loop in a background `Task`, then `Complete()` the channel writer; the outer `GrepAsync` iterates `channel.Reader.ReadAllAsync(ct)` and `yield return`s each hit.

### 5. Per-file scan: binary skip + line iteration
Open the file with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan)`. Peek up to 8 KB into a stack `byte[8192]`; if any byte in the peeked prefix is `0x00`, return. Rewind (via re-open or `Seek`), wrap in `StreamReader` with `Encoding.UTF8` (BOM detect on). Loop `ReadLineAsync(token)`. For each non-null line, run the matcher; emit `GrepHit` with 1-based line number (counter starts at 1, increment before check) and the matched column (see step 6). Swallow `IOException` and `UnauthorizedAccessException` silently — one bad file must not kill the search.

### 6. Matching logic
Build the matcher once outside the per-file loop.
- Literal mode (`options.Regex == false`): capture the pattern into a `string` (or `char[]`) and search each line via `line.AsSpan().IndexOf(pattern.AsSpan(), options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)`. Column = index + 1 when non-negative.
- Regex mode (`options.Regex == true`): compile `new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.Compiled | (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase))`. Per line, call `Match(line)` and read `.Index + 1` for column. Compile failures surface as `RegexParseException` — let it propagate to the caller.

Preview = the raw line trimmed of trailing `\r` / `\n` (mirrors current behavior).

### 7. Replace SearchService
Rewrite `src/MiniIde/Services/SearchService.cs`. Keep the class name and `public async IAsyncEnumerable<FindHit> SearchAsync(string root, string query, bool regex, CancellationToken ct)` signature — the current call site in `FindResultsViewModel.SearchAsync` must be unchanged. Internally: construct a `FileGrepper` (see Open Decision #1 for lifetime), build a hardcoded `HashSet<string>` of dir names to skip (`.git`, `bin`, `obj`, `node_modules`, `.vs`, `packages`), and pass a `SkipDirectory` predicate that returns true when `Path.GetFileName(path)` matches any entry (`OrdinalIgnoreCase`). Leave `SkipFile` null. Map each `GrepHit` → `FindHit` — same fields, straight passthrough — and yield.

### 8. Wire the project reference
Add `<ItemGroup><ProjectReference Include="..\BenMakesGames.FileGrepper\BenMakesGames.FileGrepper.csproj" /></ItemGroup>` to `MiniIde.csproj`. Confirm build succeeds and no `System.Diagnostics.Process` reference lingers in `SearchService.cs`.

### 9. Refresh docs
Rewrite `docs/global-find.md`: describe the managed lib, its scope (grep only, no ignore semantics), and where the IDE-side skip list lives. Follow `docs/CLAUDE.md` conventions (fragments, one idea per line). Update the `docs/stack.md` global-find bullet from *"shell `rg`"* to a short pointer to the new lib. Leave the `.NET 9` line alone — separate spin-off ticket (see Learnings once implemented).

## Test Plan
- [ ] `dotnet build MiniIde.slnx` succeeds; both projects compile clean.
- [ ] `scripts/run.ps1` launches the IDE without runtime errors.
- [ ] Open the MiniIde solution itself. Search for `Process.Start` — expect hits in `RunService`, `NuGetService`, but NOT in `SearchService` (post-rewrite).
- [ ] Search for `SearchService` — expect hits in `MainWindowViewModel.cs`, `FindResultsViewModel.cs`, `SearchService.cs`.
- [ ] Toggle Regex, search for `\bSearchAsync\b` — expect same hits as plain `SearchAsync`.
- [ ] Search on a fresh Windows machine (or with `rg` removed from PATH) — results appear, no error dialog.
- [ ] Type in the search box, click Search, then click Search again with a different query mid-run — no crash, no leaked results, status returns to `N match(es)` or `Canceled`.
- [ ] Search yields no hits from `bin/`, `obj/`, `.git/`, `.vs/` — verify by choosing a query known to appear in a build artifact (e.g., a compiled resource string).
- [ ] Search returns zero hits when querying inside a binary file (e.g., an ico or dll deposited into the solution tree).
- [ ] Clicking a hit still navigates the editor to the correct line + column (regression check against existing `Selected` handler).

## Follow-ups (spin-off tickets, out of scope here)
- **Test project** — `src/BenMakesGames.FileGrepper.Tests/` with xUnit. Cover literal, regex, cancellation, skip predicate, binary skip, empty stream.
- **`.gitignore`-aware search** — IDE-side reader that materializes a `Predicate<string>` from repo `.gitignore` and hands it to `FileGrepper`.
- **`docs/stack.md` .NET version drift** — file says `.NET 9`; `MiniIde.csproj` targets `net10.0`. Doc fix.

## Learnings

### Architectural decisions
- **Open Decisions — all four taken at default.** Parameterless `SearchService` ctor (`FileGrepper` constructed as a stateless field). Unbounded `Channel`. Fresh `Regex` per call (no cache). Preview trimmed of trailing `\r`/`\n`.
- **Exclusions live in the caller, not the lib.** `FileGrepper` ships no prune list — `SearchService` owns the hardcoded dir skip set and passes a `SkipDirectory` predicate over full paths. Keeps the engine a pure grep primitive (KISS / least surprise).
- **Producer/consumer via a linked CTS.** `GrepAsync` runs the `Parallel.ForEachAsync` pump as a detached task writing to the channel; the iterator reads the channel under the caller's `ct`. A `CreateLinkedTokenSource(ct)` drives the pump so that **either** external cancellation **or** early enumerator disposal winds the walk down promptly — a caller that `break`s without cancelling won't hang on a full directory walk. The pump funnels its terminal state (success, cancellation, or hard failure like a missing root) through `channel.Writer.Complete(ex)`, so exceptions surface cleanly to the reader with no unobserved-task risk.

### Problems encountered
- **`RegexOptions.NonBacktracking | RegexOptions.Compiled` is illegal** — the two are mutually exclusive; combining them throws at `Regex` construction. The ticket's step 6 specified both. Kept `NonBacktracking` (ReDoS safety on user-typed patterns, which the ticket deliberately chose over rg-compatibility) and dropped `Compiled`. Interactive search doesn't benefit enough from `Compiled` to justify losing the linear-time guarantee.
- **`Encoding.UTF8` never throws on bad bytes** — the shared instance uses replacement-char fallback, so "skip files that fail UTF-8 decode" (a Constraint) wouldn't actually skip anything. Used `new UTF8Encoding(false, throwOnInvalidBytes: true)` and caught `DecoderFallbackException` (note: it derives from `ArgumentException`, **not** `IOException`, so it needs its own catch) to skip non-UTF-8 files silently. The NUL sniff catches most binaries first; the throwing decoder catches text in other encodings.

### Interesting tidbits
- `FileSystemEnumerable<T>` predicate/transform delegates take `ref FileSystemEntry` (a ref struct) — lambdas must be written `(ref FileSystemEntry entry) => ...`. `entry.ToFullPath()` yields the full path (no trailing separator, so `Path.GetFileName` returns the dir/file name cleanly). `ShouldRecursePredicate` gates directory descent; `ShouldIncludePredicate` also fires for directories, so filter with `!entry.IsDirectory`.
- `Stream.ReadAtLeastAsync(buffer, len, throwOnEndOfStream: false, ct)` is the clean way to peek a fixed prefix for the binary sniff — reads until full or EOF in one call. (Async rules out the `stackalloc byte[8192]` the ticket sketched; a per-file heap buffer is negligible under `ProcessorCount` fan-out.)
- `StreamReader.ReadLineAsync` already strips the line terminator, so the `TrimEnd('\r','\n')` is belt-and-suspenders to mirror the old rg-JSON behavior exactly.

### Verification
- Exercised the lib end-to-end via a throwaway console harness against this repo (no GUI): literal + regex parity, dir-skip (0/167 hits under bin/obj), 1-based column, binary-NUL skip, and pre-cancelled-token → `OperationCanceledException` all pass. GUI-level Test Plan items (run.ps1 launch, click-to-navigate) remain manual — engine + `SearchService` wiring proven, build clean.

### Related areas affected
- `docs/stack.md` still says **.NET 9** while the code targets `net10.0` — left as-is per ticket; tracked as the third spin-off follow-up.
- Follow-up spin-offs remain open: FileGrepper test project, `.gitignore`-aware search predicate.
