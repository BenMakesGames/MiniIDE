using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

/// <summary>The Roslyn semantic layer: go-to-definition, rename, and compiler diagnostics.
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

    // The stamp of each document's content as it currently sits in _solution. A file whose stamp still
    // matches cannot have been read into a different snapshot, so the reconcile skips it without reading.
    // Only ever touched under _lock (load / reconcile).
    private readonly Dictionary<string, FileStamp> _stamps = new(StringComparer.OrdinalIgnoreCase);

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
        _ws.RegisterWorkspaceFailedHandler(e => Progress?.Invoke($"[warn] {e.Diagnostic.Message}"));
        var progress = new Progress<ProjectLoadProgress>(p =>
            Progress?.Invoke($"{p.Operation} {Path.GetFileNameWithoutExtension(p.FilePath)}"));
        _solution = await _ws.OpenSolutionAsync(_solutionPath!, progress, ct);
        _manifestFingerprint = await ComputeManifestFingerprintAsync(_solutionPath!, ct);
    }

    /// <summary>Brings the snapshot up to date with the authoritative disk. Every semantic query funnels
    /// through here (go to definition, rename, Find problems), so the view is never frozen on stale text.
    ///
    /// <para>Always a whole-solution pass, and cheap because it is gated twice: the manifest fingerprint
    /// decides structural drift, and every document goes through the stamp-gated overlay — a <c>stat</c> each,
    /// a read only for files whose stamp moved. No feed of changed paths is needed or trusted.</para>
    ///
    /// <para><b>Structural drift</b> — a source file or project added/removed, or a project file edited —
    /// forces a real reload (<see cref="LoadSolutionAsync"/>), because <c>WithDocumentText</c> can neither add
    /// nor remove documents. <b>Text drift</b> — the same file set with changed contents — is folded in with a
    /// cheap in-memory <c>WithDocumentText</c> overlay (<see cref="OverlayDocumentAsync"/>).</para>
    ///
    /// <para>No-op until <see cref="EnsureLoadedAsync"/> has run. Never writes disk — reconciliation is
    /// strictly disk → view.</para></summary>
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
            // Iterating the snapshot captured here while OverlayDocumentAsync re-forks _solution is deliberate:
            // a DocumentId survives the fork, so the ids collected from this pass stay addressable.
            foreach (var project in _solution.Projects)
                foreach (var doc in project.Documents)
                    await OverlayDocumentAsync(doc, ct);
        }
        finally { _lock.Release(); }
    }

    /// <summary>Forces a full rebuild of the snapshot from disk regardless of drift — backs the explicit
    /// "Reload solution" command so a user-invoked reload refreshes the semantic snapshot, not just the project
    /// list. No-op if the workspace was never loaded (the first query will load it fresh anyway).</summary>
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

    /// <summary>Reflects one document's on-disk text into the snapshot, forking <see cref="_solution"/> via
    /// <c>WithDocumentText</c> so the next query resolves against current disk. Without it, once an external
    /// edit shifts offsets, go-to-definition silently lands on the wrong symbol.
    ///
    /// <para>Two gates, in order. The <see cref="FileStamp"/> decides whether to <b>read</b>: an untouched
    /// file costs a <c>stat</c>. <b>Content</b> (<c>ContentEquals</c>) decides whether to <b>fork</b>: an
    /// operation-write that leaves content unchanged (e.g. a NuGet restore touching a project's source) forks
    /// nothing even though its stamp moved. Skipping unchanged files matters — re-applying identical text
    /// still invalidates the cached compilation, turning a warm query cold.</para>
    ///
    /// <para>The stamp is recorded <em>only</em> alongside the read that produced the snapshot's content, and
    /// is sampled <em>before</em> that read. Both halves are load-bearing: they make every stamp describe a
    /// disk state at or before the content in <see cref="_solution"/>, so a write racing the read leaves a
    /// stale stamp and gets re-read next time. A stamp can therefore cost a redundant read, never a missed
    /// change.</para>
    ///
    /// <para>Forks the immutable snapshot in memory and <b>never</b> calls <c>MSBuildWorkspace.TryApplyChanges</c>
    /// (which would persist the text to disk). Under the read-only law there is no edit to push back, so any
    /// disk write here would be a bug. Assumes the lock is held.</para></summary>
    private async Task OverlayDocumentAsync(Document doc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (doc.FilePath is null) return;

        var stamp = FileStamp.For(doc.FilePath);
        if (stamp is null) return; // gone or unreadable: keep the last-known text rather than blanking it
        if (_stamps.TryGetValue(doc.FilePath, out var synced) && synced == stamp.Value) return;

        var updated = SourceText.From(await File.ReadAllTextAsync(doc.FilePath, ct));
        _stamps[doc.FilePath] = stamp.Value;

        var current = await doc.GetTextAsync(ct);
        if (current.ContentEquals(updated)) return;
        _solution = _solution!.WithDocumentText(doc.Id, updated);
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

    // Recursive *.cs walk that skips the build/VCS directories in IdeDirectories, so the manifest tracks the
    // source set MSBuild's default glob would.
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

    /// <summary>The symbol referenced or declared at <paramref name="position"/>, or null when the file isn't
    /// in the solution or nothing resolves there. Shared by go-to-definition and the rename
    /// path so none of them can disagree about what "the symbol under the caret" means — all resolve against
    /// the same fresh-disk <see cref="_solution"/> snapshot.</summary>
    public async Task<ISymbol?> ResolveSymbolAsync(string filePath, int position, CancellationToken ct = default)
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

    /// <summary>Describes the symbol at <paramref name="position"/> for the rename UI without leaking a Roslyn
    /// type to the view: its current name (to pre-fill the prompt) and whether it is defined in this solution's
    /// source (only then is it renameable). Returns null when nothing resolves. Resolves against the fresh-disk
    /// snapshot, so the caller must reconcile first (via <c>EnsureWorkspaceReadyAsync</c>).</summary>
    public async Task<RenameTarget?> DescribeRenameTargetAsync(
        string filePath, int position, CancellationToken ct = default)
    {
        var symbol = await ResolveSymbolAsync(filePath, position, ct);
        if (symbol is null) return null;
        var source = _solution is null ? null : await SymbolFinder.FindSourceDefinitionAsync(symbol, _solution, ct);
        var inSolution = source is not null && source.Locations.Any(l => l.IsInSource);
        return new RenameTarget((source ?? symbol).Name, inSolution);
    }

    /// <summary>Computes a solution-wide rename of the symbol at <paramref name="position"/> to
    /// <paramref name="newName"/>, resolving against the fresh-disk snapshot and delegating the refactor to
    /// <see cref="RenameService.ComputeAsync"/>. Returns a plain <see cref="RenameOutcome"/> — the
    /// <c>_solution</c> and the <c>ISymbol</c> never leave the service. Computes only; the caller applies the
    /// outcome to disk via <see cref="RenameService.ApplyToDisk"/>, so this method (like everything here) never
    /// writes the filesystem.</summary>
    public async Task<RenameOutcome> ComputeRenameAsync(
        string filePath, int position, string newName, CancellationToken ct = default)
    {
        if (_solution is null) return RenameOutcome.NoSymbol();
        var symbol = await ResolveSymbolAsync(filePath, position, ct);
        if (symbol is null) return RenameOutcome.NoSymbol();
        return await RenameService.ComputeAsync(_solution, symbol, newName, ct);
    }

    /// <summary>
    /// Compiles <paramref name="projectPath"/>'s project and returns its Error + Warning compiler diagnostics as
    /// <see cref="ProblemItem"/>s. A multi-targeted project is several Roslyn projects sharing one file path;
    /// all of them are analyzed. Info/Hidden and pragma-suppressed diagnostics are excluded; duplicates (same
    /// id+severity+message+location, e.g. from the per-TFM compilations) are collapsed. Returns empty when no
    /// solution is loaded or the project isn't in it. Compilation is the expensive step and may be slow on a
    /// first, cold load — it runs off the UI thread, honors <paramref name="ct"/> between compilations, and
    /// reports progress via the <see cref="Progress"/> event.
    /// </summary>
    public Task<IReadOnlyList<ProblemItem>> GetDiagnosticsAsync(string projectPath, CancellationToken ct = default)
    {
        var solution = _solution;
        if (solution is null) return Task.FromResult<IReadOnlyList<ProblemItem>>(System.Array.Empty<ProblemItem>());
        var full = Path.GetFullPath(projectPath);
        var projects = solution.Projects
            .Where(p => string.Equals(p.FilePath, full, StringComparison.OrdinalIgnoreCase))
            .ToArray();

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
            foreach (var project in projects)
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
