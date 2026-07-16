using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using MiniIde.Models;

namespace MiniIde.Services;

/// <summary>The Roslyn semantic layer: go-to-definition, find-references, and compiler diagnostics.
///
/// <para>All reads go through the private <see cref="_solution"/> snapshot, never <c>_ws.CurrentSolution</c>.
/// That is deliberate and load-bearing — it lets <see cref="ReconcileWithDiskAsync"/> reflect the
/// authoritative disk (external tools own the writes; see README's "no hand-typed edits" law) by forking the
/// immutable snapshot in memory. Reconciliation is one-directional, disk → view: a read-only view holds no
/// edit to push back, so there is deliberately no way for anything here to reach the filesystem.</para></summary>
public class WorkspaceService : IDisposable
{
    private MSBuildWorkspace? _ws;
    private Solution? _solution;
    private string? _solutionPath;
    // Structural fingerprint of what was loaded: project files (path + content hash) and the set of source
    // file paths. Recomputed identically on load and on every reconcile, so a difference means the disk's
    // *structure* actually changed — files/projects added or removed, or a project file edited — never a
    // glob-vs-MSBuild disagreement. Text-only edits leave it unchanged and go through the overlay instead.
    private string _manifestFingerprint = string.Empty;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public event Action<string>? Progress;

    /// <summary>Whether the (expensive) MSBuild snapshot has been built yet. Lets focus-time reconciliation
    /// stay a no-op until the first semantic query has paid the cold-start — focus must never trigger it.</summary>
    public bool IsLoaded => _solution is not null;

    public async Task EnsureLoadedAsync(string solutionPath, CancellationToken ct = default)
    {
        if (_solution is not null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_solution is not null) return;
            _solutionPath = solutionPath;
            await LoadSolutionAsync(ct);
        }
        finally { _lock.Release(); }
    }

    // Builds (or rebuilds) the MSBuildWorkspace from scratch — the expensive cold start — and captures the
    // structural fingerprint of what it loaded. Assumes the lock is held and _solutionPath is set. A fresh
    // build is exactly how a structural change (added/removed file or project) is absorbed, since
    // WithDocumentText can neither add nor drop documents.
    private async Task LoadSolutionAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = MSBuildWorkspace.Create();
        _ws.SkipUnrecognizedProjects = true;
        _ws.WorkspaceFailed += (_, e) => Progress?.Invoke($"[warn] {e.Diagnostic.Message}");
        var progress = new Progress<ProjectLoadProgress>(p =>
            Progress?.Invoke($"{p.Operation} {Path.GetFileNameWithoutExtension(p.FilePath)}"));
        _solution = await _ws.OpenSolutionAsync(_solutionPath!, progress, ct);
        _manifestFingerprint = await ComputeManifestFingerprintAsync(_solutionPath!, ct);
    }

    /// <summary>Brings the snapshot up to date with the authoritative disk. Every semantic query funnels
    /// through here (before F12 / Shift+F12 / Problems) and the window fires it on focus, so the view is
    /// never frozen on stale text.
    ///
    /// <para><b>Structural drift</b> — a source file or project added/removed, or a project file edited —
    /// forces a real reload (<see cref="LoadSolutionAsync"/>), because <c>WithDocumentText</c> can neither add
    /// nor remove documents. <b>Text drift</b> — the same file set with changed contents — is folded in with a
    /// cheap in-memory <c>WithDocumentText</c> overlay (<see cref="OverlayDiskTextAsync"/>).</para>
    ///
    /// <para>No-op until <see cref="EnsureLoadedAsync"/> has run (focus must not trigger the cold start).
    /// Never writes disk — reconciliation is strictly disk → view.</para></summary>
    public async Task ReconcileWithDiskAsync(CancellationToken ct = default)
    {
        if (_solution is null || _solutionPath is null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_solution is null || _solutionPath is null) return;
            var current = await ComputeManifestFingerprintAsync(_solutionPath, ct);
            if (!string.Equals(current, _manifestFingerprint, StringComparison.Ordinal))
            {
                await LoadSolutionAsync(ct); // fresh snapshot already reflects disk; nothing left to overlay
                return;
            }
            await OverlayDiskTextAsync(ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Forces a full rebuild of the snapshot from disk regardless of drift — backs the explicit
    /// "Reload solution" command so a user-invoked reload refreshes the semantic snapshot, not just the tree.
    /// No-op if the workspace was never loaded (the first query will load it fresh anyway).</summary>
    public async Task ReloadIfLoadedAsync(CancellationToken ct = default)
    {
        if (_solution is null || _solutionPath is null) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_solution is null || _solutionPath is null) return;
            await LoadSolutionAsync(ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>The disk-reflection overlay: for every document whose on-disk text has drifted from the
    /// snapshot, forks <see cref="_solution"/> via <c>WithDocumentText</c> so the next query resolves against
    /// current disk. Without it, once an external edit shifts offsets, go-to-definition silently lands on the
    /// wrong symbol.
    ///
    /// <para>Drift is decided by <b>content</b> (<c>ContentEquals</c>), never mtime: an operation-write that
    /// leaves a file's content unchanged (e.g. a NuGet restore touching a project's source) forks nothing, and
    /// a same-length overwrite that a file's size could hide still forks. Skipping unchanged files matters —
    /// re-applying identical text still invalidates the cached compilation, turning a warm query cold.</para>
    ///
    /// <para>Forks the immutable snapshot in memory and <b>never</b> calls <c>MSBuildWorkspace.TryApplyChanges</c>
    /// (which would persist the text to disk). Under the read-only law there is no edit to push back, so any
    /// disk write here would be a bug. Assumes the lock is held.</para></summary>
    private async Task OverlayDiskTextAsync(CancellationToken ct)
    {
        foreach (var project in _solution!.Projects)
            foreach (var doc in project.Documents)
            {
                ct.ThrowIfCancellationRequested();
                if (doc.FilePath is null || !File.Exists(doc.FilePath)) continue;
                var updated = SourceText.From(await File.ReadAllTextAsync(doc.FilePath, ct));
                var current = await doc.GetTextAsync(ct);
                if (current.ContentEquals(updated)) continue;
                _solution = _solution.WithDocumentText(doc.Id, updated);
            }
    }

    // The cheap metadata-only .slnx/.sln parse (no MSBuild eval — see docs/roslyn.md) plus a directory walk:
    // enough to notice files/projects appearing or disappearing without paying a full reload just to check.
    // Project files are content-hashed (an external .csproj edit changes what compiles); source files
    // contribute only their paths (content changes are the overlay's job, add/remove is structural).
    private static async Task<string> ComputeManifestFingerprintAsync(string solutionPath, CancellationToken ct)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath);
        if (serializer is null) return string.Empty;
        var model = await serializer.OpenAsync(solutionPath, ct);
        var dir = Path.GetDirectoryName(solutionPath)!;

        var lines = new List<string>();
        var docs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in model.SolutionProjects)
        {
            ct.ThrowIfCancellationRequested();
            var proj = Path.GetFullPath(Path.Combine(dir, p.FilePath));
            lines.Add("P:" + proj.ToLowerInvariant() + "=" + HashFile(proj));
            var projDir = Path.GetDirectoryName(proj);
            if (projDir is not null)
                foreach (var f in EnumerateSourceFiles(projDir))
                    docs.Add(Path.GetFullPath(f).ToLowerInvariant());
        }
        foreach (var d in docs) lines.Add("D:" + d);

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    // Recursive *.cs walk that skips the same build/VCS directories as the tree and search (IdeDirectories),
    // so the manifest tracks the source set MSBuild's default glob would, and can't drift from the tree view.
    private static IEnumerable<string> EnumerateSourceFiles(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (IdeDirectories.Pruned.Contains(Path.GetFileName(sub))) continue;
            foreach (var f in EnumerateSourceFiles(sub)) yield return f;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs")) yield return f;
    }

    private static string HashFile(string path)
    {
        if (!File.Exists(path)) return "<missing>";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
