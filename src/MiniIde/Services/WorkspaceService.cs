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

    public async Task<IReadOnlyList<(string File, int Line, int Column, string Preview)>> FindReferencesAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var results = new List<(string, int, int, string)>();
        var doc = FindDocument(filePath);
        if (doc is null) return results;
        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model is null || root is null) return results;
        var token = root.FindToken(position);
        var symbol = model.GetSymbolInfo(token.Parent!, ct).Symbol
            ?? model.GetDeclaredSymbol(token.Parent!, ct);
        if (symbol is null) return results;
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
