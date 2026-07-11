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

/// <summary>The Roslyn semantic layer: go-to-definition, find-references, and compiler diagnostics.
///
/// <para>All reads go through the private <see cref="_solution"/> snapshot, never
/// <c>_ws.CurrentSolution</c>. That is deliberate and load-bearing — it lets <see cref="SyncDocumentsAsync"/>
/// overlay unsaved editor buffers by forking the immutable snapshot in memory, with no way for an edit to
/// reach the disk.</para></summary>
public class WorkspaceService : IDisposable
{
    private MSBuildWorkspace? _ws;
    private Solution? _solution;
    private readonly SemaphoreSlim _lock = new(1, 1);
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

    /// <summary>Overlays unsaved editor buffers onto the solution snapshot so semantic queries see what the
    /// user is actually looking at. Without this, every query resolves against the on-disk text: once an edit
    /// shifts offsets, go-to-definition silently lands on the wrong symbol.
    ///
    /// <para>This forks <see cref="_solution"/> in memory and <b>never</b> calls
    /// <c>MSBuildWorkspace.TryApplyChanges</c>, which persists the new text to disk — i.e. would silently
    /// autosave the user's file behind their back, destroying the unsaved-buffer model outright.</para>
    ///
    /// <para>Cheap by construction: <c>WithDocumentText</c> is lazy — it re-uses the snapshot structurally and
    /// merely marks the project's compilation stale (measured at ~0.3ms per call, no parse, no compile). The
    /// re-bind is paid by the next semantic query, all of which are explicitly user-initiated (F12, Shift+F12,
    /// Problems refresh) and already slow enough to show status. Nothing runs in the background.</para>
    ///
    /// <para>Documents whose text already matches are skipped: re-applying identical text would still
    /// invalidate the cached compilation and turn a ~16ms warm query back into a ~700ms cold one.</para></summary>
    /// <param name="buffers">Unsaved editor buffers, as (absolute path, current text). Must be snapshotted by
    /// the caller — AvaloniaEdit's <c>TextDocument</c> is thread-affine, this method is not.</param>
    public async Task SyncDocumentsAsync(
        IReadOnlyList<(string Path, string Text)> buffers, CancellationToken ct = default)
    {
        if (_solution is null || buffers.Count == 0) return;
        foreach (var (path, text) in buffers)
        {
            ct.ThrowIfCancellationRequested();
            var doc = FindDocument(path);
            if (doc is null) continue; // open file that isn't part of the solution (e.g. a .md)
            var updated = SourceText.From(text);
            var current = await doc.GetTextAsync(ct);
            if (current.ContentEquals(updated)) continue;
            _solution = _solution.WithDocumentText(doc.Id, updated);
        }
    }

    public async Task<SourceLocation?> GoToDefinitionAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var symbol = await ResolveSymbolAsync(filePath, position, ct);
        if (symbol is null) return null;
        var def = await SymbolFinder.FindSourceDefinitionAsync(symbol, _solution!, ct) ?? symbol;
        var loc = def.Locations.Length > 0 ? def.Locations[0] : null;
        if (loc is null || !loc.IsInSource) return null;
        return ToSourceLocation(loc.SourceTree!.FilePath, loc.GetLineSpan());
    }

    /// <summary>
    /// Returns the symbol's references (possibly empty when the symbol has none), or <c>null</c> when no
    /// symbol resolves at <paramref name="position"/>. Callers use the null case to distinguish a genuine
    /// "no symbol here" from a legitimate "symbol found, zero references."
    /// </summary>
    public async Task<IReadOnlyList<FindHit>?> FindReferencesAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var symbol = await ResolveSymbolAsync(filePath, position, ct);
        if (symbol is null) return null;

        var results = new List<FindHit>();
        foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, _solution!, ct))
            foreach (var loc in reference.Locations)
            {
                var span = loc.Location.GetLineSpan();
                var text = await loc.Document.GetTextAsync(ct);
                var lineText = text.Lines[span.StartLinePosition.Line].ToString();
                results.Add(new FindHit(
                    ToSourceLocation(loc.Location.SourceTree!.FilePath, span), lineText.Trim()));
            }
        return results;
    }

    /// <summary>The symbol referenced or declared at <paramref name="position"/>, or null when the file isn't
    /// in the solution or nothing resolves there. Shared by both symbol queries so they cannot disagree about
    /// what "the symbol under the caret" means.</summary>
    private async Task<ISymbol?> ResolveSymbolAsync(string filePath, int position, CancellationToken ct)
    {
        var doc = FindDocument(filePath);
        if (doc is null) return null;
        var model = await doc.GetSemanticModelAsync(ct);
        var root = await doc.GetSyntaxRootAsync(ct);
        if (model is null || root is null) return null;
        var parent = root.FindToken(position).Parent;
        if (parent is null) return null;
        return model.GetSymbolInfo(parent, ct).Symbol ?? model.GetDeclaredSymbol(parent, ct);
    }

    /// <summary>
    /// Compiles every project in the loaded solution and returns its Error + Warning compiler diagnostics as
    /// <see cref="ProblemItem"/>s. Info/Hidden and pragma-suppressed diagnostics are excluded; duplicates
    /// (same id+severity+message+location, e.g. from a multi-targeted project's per-TFM compilations) are
    /// collapsed. Returns empty when no solution is loaded. Compilation is the expensive step and may be slow
    /// on a first, cold load — it runs off the UI thread, honors <paramref name="ct"/> between projects, and
    /// reports per-project progress via the <see cref="Progress"/> event.
    /// </summary>
    public Task<IReadOnlyList<ProblemItem>> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var solution = _solution;
        if (solution is null) return Task.FromResult<IReadOnlyList<ProblemItem>>(System.Array.Empty<ProblemItem>());

        // Task.Run, not a bare async method: Roslyn's GetCompilationAsync does its binding work on the calling
        // thread, so awaiting it from the UI thread freezes the window for the whole analysis — and, worse,
        // the Progress posts it queues can only be pumped once the loop finishes, so they land *after* the
        // caller's final "N errors, M warnings" and overwrite it. Getting off the UI thread fixes both.
        return Task.Run<IReadOnlyList<ProblemItem>>(async () =>
        {
            var results = new List<ProblemItem>();
            // ProblemItem is a record: structural equality already defines "the same diagnostic", so the
            // set does the de-duplication with no hand-rolled key to keep in sync with the record's fields.
            var seen = new HashSet<ProblemItem>();
            foreach (var project in solution.Projects)
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
                    var location = diag.Location.IsInSource
                        ? ToSourceLocation(diag.Location.SourceTree!.FilePath, diag.Location.GetLineSpan())
                        : null;
                    var item = new ProblemItem(diag.Id, severity, diag.GetMessage(), location);
                    if (seen.Add(item)) results.Add(item);
                }
            }
            return results;
        }, ct);
    }

    // The one place Roslyn's 0-based LinePosition becomes a 1-based, editor-facing SourceLocation.
    private static SourceLocation ToSourceLocation(string file, FileLinePositionSpan span) =>
        new(file, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);

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
