using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using MiniIde.Models;

namespace MiniIde.Services;

public class WorkspaceService : IDisposable
{
    private MSBuildWorkspace? _ws;
    private Solution? _solution;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public bool IsLoaded => _solution is not null;
    public event Action<string>? Progress;

    public async Task EnsureLoadedAsync(string solutionPath, CancellationToken ct = default)
    {
        if (_solution is not null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_solution is not null) return;
            _ws = MSBuildWorkspace.Create();
            _ws.SkipUnrecognizedProjects = true;
            _ws.WorkspaceFailed += (_, e) => Progress?.Invoke($"[warn] {e.Diagnostic.Message}");
            var progress = new Progress<ProjectLoadProgress>(p =>
                Progress?.Invoke($"{p.Operation} {Path.GetFileNameWithoutExtension(p.FilePath)}"));
            _solution = await _ws.OpenSolutionAsync(solutionPath, progress, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<(string File, int Line, int Column)?> GoToDefinitionAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var doc = FindDocument(filePath);
        if (doc is null) return null;
        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return null;
        var root = await doc.GetSyntaxRootAsync(ct);
        if (root is null) return null;
        var token = root.FindToken(position);
        var symbol = model.GetSymbolInfo(token.Parent!, ct).Symbol
            ?? model.GetDeclaredSymbol(token.Parent!, ct);
        if (symbol is null) return null;
        var def = await SymbolFinder.FindSourceDefinitionAsync(symbol, _solution!, ct) ?? symbol;
        var loc = def.Locations.Length > 0 ? def.Locations[0] : null;
        if (loc is null || !loc.IsInSource) return null;
        var line = loc.GetLineSpan();
        return (loc.SourceTree!.FilePath, line.StartLinePosition.Line + 1, line.StartLinePosition.Character + 1);
    }

    /// <summary>
    /// Returns the symbol's reference locations (possibly empty when the symbol has zero references),
    /// or <c>null</c> when no symbol resolves at <paramref name="position"/>. Callers use the null case
    /// to distinguish a genuine "no symbol here" from a legitimate "symbol found, zero references."
    /// </summary>
    public async Task<IReadOnlyList<(string File, int Line, int Column, string Preview)>?> FindReferencesAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var results = new List<(string, int, int, string)>();
        var doc = FindDocument(filePath);
        if (doc is null) return null;
        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model is null || root is null) return null;
        var token = root.FindToken(position);
        var symbol = model.GetSymbolInfo(token.Parent!, ct).Symbol
            ?? model.GetDeclaredSymbol(token.Parent!, ct);
        if (symbol is null) return null;
        var refs = await SymbolFinder.FindReferencesAsync(symbol, _solution!, ct);
        foreach (var r in refs)
        {
            foreach (var loc in r.Locations)
            {
                var span = loc.Location.GetLineSpan();
                var text = await loc.Document.GetTextAsync(ct);
                var lineText = text.Lines[span.StartLinePosition.Line].ToString();
                results.Add((loc.Location.SourceTree!.FilePath, span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1, lineText.Trim()));
            }
        }
        return results;
    }

    /// <summary>
    /// Compiles every project in the loaded solution and returns its Error + Warning compiler diagnostics as
    /// <see cref="ProblemItem"/>s. Info/Hidden and pragma-suppressed diagnostics are excluded; duplicates
    /// (same id+file+line+column+message, e.g. from a multi-targeted project's per-TFM compilations) are
    /// collapsed. Returns empty when no solution is loaded. Compilation is the expensive step and may be slow
    /// on a first, cold load — it runs off the UI thread, honors <paramref name="ct"/> between projects, and
    /// reports per-project progress via the <see cref="Progress"/> event.
    /// </summary>
    public async Task<IReadOnlyList<ProblemItem>> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        if (_solution is null) return System.Array.Empty<ProblemItem>();
        var results = new List<ProblemItem>();
        var seen = new HashSet<string>();
        foreach (var project in _solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            Progress?.Invoke($"Analyzing {project.Name}");
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var diag in compilation.GetDiagnostics(ct))
            {
                if (diag.IsSuppressed) continue;
                if (diag.Severity is not (DiagnosticSeverity.Error or DiagnosticSeverity.Warning)) continue;

                var severity = diag.Severity == DiagnosticSeverity.Error ? ProblemSeverity.Error : ProblemSeverity.Warning;
                string? file = null;
                int line = 0, column = 0;
                if (diag.Location.IsInSource)
                {
                    var span = diag.Location.GetLineSpan();
                    file = diag.Location.SourceTree!.FilePath;
                    line = span.StartLinePosition.Line + 1;
                    column = span.StartLinePosition.Character + 1;
                }
                var message = diag.GetMessage();
                if (!seen.Add($"{diag.Id}|{file}|{line}|{column}|{message}")) continue;
                results.Add(new ProblemItem(diag.Id, severity, message, file, line, column));
            }
        }
        return results;
    }

    public void UpdateDocumentText(string filePath, string text)
    {
        if (_solution is null || _ws is null) return;
        var doc = FindDocument(filePath);
        if (doc is null) return;
        var updated = _solution.WithDocumentText(doc.Id, SourceText.From(text));
        if (_ws.TryApplyChanges(updated)) _solution = _ws.CurrentSolution;
    }

    private Document? FindDocument(string filePath)
    {
        if (_solution is null) return null;
        var full = Path.GetFullPath(filePath);
        foreach (var p in _solution.Projects)
            foreach (var d in p.Documents)
                if (string.Equals(d.FilePath, full, StringComparison.OrdinalIgnoreCase))
                    return d;
        return null;
    }

    public void Dispose() { _ws?.Dispose(); _lock.Dispose(); }
}
