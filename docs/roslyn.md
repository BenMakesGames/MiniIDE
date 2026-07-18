# Roslyn notes

## Package pins

- `Microsoft.CodeAnalysis.Workspaces.MSBuild` **>= 5.6.0** — required for native `.slnx`.
- Avoid Roslyn 4.9.0 — hang bug ([roslyn#75967](https://github.com/dotnet/roslyn/issues/75967)).

## .slnx

Native since Roslyn 5.0 ([PR #77326](https://github.com/dotnet/roslyn/pull/77326)). `MSBuildWorkspace.OpenSolutionAsync("x.slnx")` works direct.

Cheap metadata-only parse (no MSBuild eval) via `Microsoft.VisualStudio.SolutionPersistence`:

```csharp
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
var model = await SolutionSerializers.SlnXml.OpenAsync("My.slnx", ct);
foreach (var p in model.SolutionProjects) { /* p.FilePath */ }
```

Use this for solution-tree view. Defer `MSBuildWorkspace.OpenProjectAsync` per-project until user opens/navigates.

## Cold-start strategy

Roslyn forces per-project compile on `OpenSolutionAsync` — no true lazy. Split workspaces:

- `AdhocWorkspace` → open file, syntactic highlight, local nav. Instant.
- `MSBuildWorkspace` → deferred. First triggered by cross-project features (find refs, go-to-def outside file).

Historical worst case: 300 projects = hours ([roslyn#14325](https://github.com/dotnet/roslyn/issues/14325)). Report progress via `IProgress<ProjectLoadProgress>`. Set `SkipUnrecognizedProjects = true`.

## Disk reconcile (read-only-view law)

The editor is a read-only window onto disk; external tools own writes. `WorkspaceService.ReconcileWithDiskAsync`
brings the snapshot up to date before every semantic query and on window focus. Strictly disk → view — it
**never** `TryApplyChanges` (that would write the file back).

Two modes. Normally it drains a **pending-set** the OS change feed pushed in (`MarkPathsChanged`) and overlays
only those documents. `RequestFullRescan` (cold start, structural change, watcher overflow, focus, or no
watcher at all) falls back to the whole-solution pass. See `disk-watching.md` for the feed.

- **Content drift** (same file set, changed text) → fork the immutable snapshot with `WithDocumentText`. Skip
  files whose text already matches (`SourceText.ContentEquals`): a needless fork invalidates the cached
  compilation and turns a warm query cold. Compare by **content, never mtime** — operation-writes bump mtime
  without a real change; same-length overwrites change content mtime can't see.
- **Two gates, not one.** A `(LastWriteTimeUtc, Length)` stamp decides whether to **read**; content decides
  whether to **fork**. The stamp is a read pre-filter, *not* a drift decision — that's what keeps
  content-never-mtime intact. Only the fallback poll can miss a change preserving **both** mtime and length;
  the watcher fires on the write regardless, so the primary path still catches it.
- **Invariant that makes stamps safe**: write a stamp *only* alongside the read that produced the snapshot's
  text, and *sample it before* that read. Every stamp then describes a disk state at or before the snapshot's
  content, so a write racing a read leaves a stale stamp and is re-read. Stamps cost redundant reads, never
  missed changes. (Corollary: never stamp at load time — a load stamped after `OpenSolutionAsync` would record
  a racing write's own stamp and swallow it.)
- **Structural drift** (file/project added/removed, `.csproj` edited) → `WithDocumentText` can't add/remove
  documents, so tear down and rebuild via a fresh `OpenSolutionAsync`.
- **Detecting structural drift cheaply & stably**: fingerprint the disk the *same way* on load and on each
  check — `SolutionPersistence` project list + each `.csproj`'s content hash + the set of `.cs` paths under
  each project dir (prune `IdeDirectories`). Disk-vs-disk, so a difference is a real change. Do **not** diff a
  glob against Roslyn's document set — MSBuild's include list and a naive `**/*.cs` glob disagree (`<Compile
  Remove>`, linked files), which would force a reload on every reconcile.
- Keep `EnsureLoadedAsync`'s build-once early-return for the expensive cold start; the reconcile is the
  separate refresh path layered on top.
- Both gates are counted (`Stats()`: documents stat'd → read → forked): read/stat is the stamp gate's payoff,
  fork/read is the content gate's. See `disk-watching.md §Observing it`.
- The fallback pass is O(documents) in `stat`s but only O(changed) in reads, so an idle focus does zero reads.
  The manifest walk still hashes every `.csproj` on that pass.

## MSBuild init

Call `MSBuildLocator.RegisterDefaults()` **before** first Roslyn touch. Common gotcha.

## Classifier standalone

Syntactic spans (keywords/strings/comments) — `SyntaxTree` alone, no workspace.
Semantic spans (type vs var vs method) — need `Compilation` + refs. Minimum `AdhocWorkspace` with `typeof(object).Assembly` reference.

## Compiler diagnostics (Problems panel)

`await project.GetCompilationAsync(ct)` → `compilation.GetDiagnostics(ct)` yields compiler diagnostics (all `CS####`, incl. nullable). Analyzer diagnostics (StyleCop/Roslynator) need `CompilationWithAnalyzers` — not this.

- Filter `Severity` to `Error`/`Warning`; drop `Info`/`Hidden`.
- **`GetDiagnostics()` includes suppressed entries** — drop `IsSuppressed == true` (`#pragma warning disable`) to match a real build.
- **Multi-target = one `Project` (and `Compilation`) per TFM** → duplicate diagnostics. De-dup on `id|file|line|col|message` via a `HashSet`.
- **Locationless** (`!Location.IsInSource`, e.g. missing assembly ref): no file to navigate to; keep them but bucket under `(No file)`.
- `Diagnostic.Location` → `(file, line, col)`: same mapping as go-to-def — `GetLineSpan()` → `SourceTree.FilePath`, `StartLinePosition.Line + 1`, `.Character + 1`.
- Compiling every project is the cold-start cost — run off the UI thread (already `async`), honor `ct` between projects, emit per-project progress via the existing `Progress` event. Map to plain model records; keep Roslyn types inside the service.

## Rename (safe solution-wide refactor)

`Renamer.RenameSymbolAsync(Solution, ISymbol, SymbolRenameOptions, string newName, ct)` → `Task<Solution>`.
Lives in **`Microsoft.CodeAnalysis.Workspaces`** (namespace `Microsoft.CodeAnalysis.Rename`) — transitively
present via `Microsoft.CodeAnalysis.CSharp.Workspaces`. Forks the immutable snapshot; **doesn't** write disk.

- `SymbolRenameOptions` is a record-struct: `RenameOverloads` / `RenameInStrings` / `RenameInComments` /
  `RenameFile` init flags. For code-references-only, set **all four `false`** (see the `RenameFile` gotcha).
- **Gate on in-source first**: `SymbolFinder.FindSourceDefinitionAsync(symbol, solution, ct)` — null (or no
  in-source location) means a framework/NuGet symbol; refuse (no metadata-as-source rewrite).
- **`RenameFile: true` is too aggressive for a "type whose file name matches" rule.** It renames the
  *declaring* file of **any** renamed symbol to `<newName>.cs` — renaming a *method* `Target` in `Code.cs`
  renames the file to `Fetch.cs`, not just a type in a matching-named file. So leave `RenameFile: false` and
  compute the move yourself: only when the symbol is an `INamedTypeSymbol` **and** its declaring file's base
  name equals the (old) type name, move `dir/OldName.cs → dir/NewName.cs`. (Aside: when `RenameFile` *does*
  rename, it updates the document's `Name`, **not** its `FilePath` — so a `FilePath` diff wouldn't even see it.)
- Diff the reference rewrites via `updated.GetChanges(old).GetProjectChanges().GetChangedDocuments()` — returns
  the changed `DocumentId`s (the method is `GetChangedDocuments()`, **not** `…DocumentIds()`). Redirect the
  type-file's new text to the move's new path; every other changed doc keeps its `FilePath`.
- **Conflicts aren't publicly readable.** The public overload resolves them internally; `RenameAnnotation` /
  the `ConflictEngine` types are `internal`. To block on the common "new name already names a member" case, do
  a pre-flight `INamespaceOrTypeSymbol.GetMembers(newName)` check on the symbol's container instead.
- **Persist explicitly, never `TryApplyChanges`** (which writes via the workspace and fights the read-only
  law): pull each changed doc's text and `File.WriteAllText`; `File.Move` the type-matched file (move first, so
  no content write targets a path the move invalidates). Case-only file renames need a temp-name hop on
  case-insensitive filesystems.

## API map

| Feature | API |
|---|---|
| Syntax highlight | `Classifier.GetClassifiedSpansAsync` |
| Jump-to-def | `SymbolFinder.FindSourceDefinitionAsync` |
| Find usages | `SymbolFinder.FindReferencesAsync` |
| Go to implementation | `SymbolFinder.FindImplementationsAsync` (interface types/members) + `FindOverridesAsync` (class-member overrides) — union both |
| Go to subclasses | `SymbolFinder.FindDerivedClassesAsync` (class) / `FindDerivedInterfacesAsync` (interface), `transitive: true` |
| Errors/warnings | `Compilation.GetDiagnostics` |
| Safe rename | `Renamer.RenameSymbolAsync` (+ `SymbolRenameOptions`) |

- `FindImplementationsAsync` covers interfaces only, not class-member overrides — union `FindOverridesAsync` so an abstract/virtual base member resolves. `FindDerivedClassesAsync` excludes interface implementations, so an interface caret's "subclasses" are its derived *interfaces*; implementers stay under Go to Implementation.
- All four take `projects: null` (whole solution) and map result `ISymbol`s to `FindHit`s via `symbol.Locations.Where(l => l.IsInSource)` — metadata/decompiled locations are dropped, so a framework implementer contributes no entry.
