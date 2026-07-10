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

## API map

| Feature | API |
|---|---|
| Syntax highlight | `Classifier.GetClassifiedSpansAsync` |
| Jump-to-def | `SymbolFinder.FindSourceDefinitionAsync` |
| Find usages | `SymbolFinder.FindReferencesAsync` |
| Errors/warnings | `Compilation.GetDiagnostics` |
