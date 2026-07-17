using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using MiniIde.Models;

namespace MiniIde.Services;

/// <summary>The Roslyn refactor layer for a solution-wide safe rename: given the fresh-disk snapshot and the
/// symbol under the caret, it computes every reference rewrite (and, for a type whose file name matches, the
/// file rename) as an in-memory diff, and — as a separate step — writes that diff to disk.
///
/// <para><b>Confirmed API</b> (Roslyn <c>5.6.0</c>, <c>Microsoft.CodeAnalysis.Workspaces</c>):
/// <c>Renamer.RenameSymbolAsync(Solution, ISymbol, SymbolRenameOptions, string newName, CancellationToken)</c>
/// returning a new <see cref="Solution"/>, with <see cref="SymbolRenameOptions"/> exposing
/// <c>RenameOverloads</c> / <c>RenameInStrings</c> / <c>RenameInComments</c> / <c>RenameFile</c> init flags.
/// Only code references + the file rename are wanted, so <c>RenameFile</c> is on and the other three are off
/// (out of scope: comments, strings, overloads).</para>
///
/// <para><b>Compute never touches disk</b> — it forks the immutable snapshot in memory, exactly like
/// <see cref="WorkspaceService"/>'s overlay. <see cref="ApplyToDisk"/> is the only member that writes, mirroring
/// <see cref="NuGetService.SetVersion"/>'s closed-file write; it is deliberately split out so the compute stays
/// side-effect-free and testable. There is no in-app undo — git is the safety net (see the rename dialog copy).</para>
///
/// <para><b>Conflicts</b>: the public <c>RenameSymbolAsync</c> overload resolves conflicts internally and does
/// not surface Roslyn's (internal) conflict-annotation objects. So the collision that the ticket cares about —
/// the new name already naming a member of the same container — is detected here up front via the public
/// semantic model and <em>blocks</em> the rename (Open Decision 2: block, surface the collision, write nothing).
/// Locals/parameters are left to Roslyn (scope-sensitive; not the "existing member" case).</para></summary>
public static class RenameService
{
    /// <summary>Computes the rename against <paramref name="solution"/> for <paramref name="symbol"/> without
    /// writing anything. Returns a <see cref="RenameStatus.NotInSolution"/> outcome when the symbol has no
    /// in-source definition (framework / NuGet), a <see cref="RenameStatus.Conflicts"/> outcome when the new
    /// name collides with an existing member, else a <see cref="RenameStatus.Success"/> outcome carrying the
    /// changed files (path + new text) and any file move.</summary>
    public static async Task<RenameOutcome> ComputeAsync(
        Solution solution, ISymbol symbol, string newName, CancellationToken ct = default)
    {
        // Rename only what this solution owns in source. A framework/NuGet symbol has no in-source definition —
        // there is nothing here to safely rewrite, and no metadata-as-source rewrite is in scope.
        var source = await SymbolFinder.FindSourceDefinitionAsync(symbol, solution, ct);
        if (source is null || !source.Locations.Any(l => l.IsInSource))
            return RenameOutcome.NotInSolution();

        var conflicts = CollisionsWith(source, newName);
        if (conflicts.Count > 0) return RenameOutcome.Conflicted(conflicts);

        var options = new SymbolRenameOptions(
            RenameOverloads: false, RenameInStrings: false, RenameInComments: false, RenameFile: true);
        var updated = await Renamer.RenameSymbolAsync(solution, source, options, newName, ct);

        var changed = new List<RenamedFile>();
        RenameFileMove? move = null;

        foreach (var projectChange in updated.GetChanges(solution).GetProjectChanges())
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                ct.ThrowIfCancellationRequested();
                var oldDoc = solution.GetDocument(docId);
                var newDoc = updated.GetDocument(docId);
                if (oldDoc?.FilePath is null || newDoc?.FilePath is null) continue;

                // RenameFile keeps the same DocumentId but swaps the file path; that is how a type-matched file
                // rename (Foo.cs → Bar.cs) shows up. The renamed document's new text belongs at the NEW path.
                if (!string.Equals(oldDoc.FilePath, newDoc.FilePath, StringComparison.Ordinal))
                    move = new RenameFileMove(oldDoc.FilePath, newDoc.FilePath);

                var text = await newDoc.GetTextAsync(ct);
                changed.Add(new RenamedFile(newDoc.FilePath, text.ToString()));
            }

        return RenameOutcome.Renamed(changed, move);
    }

    /// <summary>Writes a successful <see cref="RenameOutcome"/> to disk: the file move first (so no content
    /// write targets a path the move is about to invalidate), then each changed file's new text. Mirrors
    /// <see cref="NuGetService.SetVersion"/>'s direct closed-file write — no editor round-trip, no
    /// <c>TryApplyChanges</c>. Throws on an I/O failure (e.g. the move's destination already exists — a real
    /// collision); the caller surfaces it and nothing partially-applied is claimed as success.</summary>
    public static void ApplyToDisk(RenameOutcome outcome)
    {
        if (outcome.Status != RenameStatus.Success) return;

        if (outcome.Move is { } move)
        {
            // A case-only rename (Foo.cs → foo.cs) is the same file on a case-insensitive filesystem, so a
            // direct File.Move would see the destination as already existing — hop through a temp name.
            if (string.Equals(move.OldPath, move.NewPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(move.OldPath, move.NewPath, StringComparison.Ordinal))
            {
                var temp = move.OldPath + ".minirename.tmp";
                File.Move(move.OldPath, temp);
                File.Move(temp, move.NewPath);
            }
            else
            {
                File.Move(move.OldPath, move.NewPath);
            }
        }

        foreach (var file in outcome.ChangedFiles)
            File.WriteAllText(file.Path, file.NewText);
    }

    // Blocks when the new name already names a member of the symbol's container (Open Decision 2: block, may be
    // over-strict on a legal overload — revisit if it bites). Scoped to type members / types, whose container
    // is a type or namespace; locals & parameters shadow legally and need scope analysis the public overload
    // doesn't expose, so they are not policed here.
    private static IReadOnlyList<string> CollisionsWith(ISymbol symbol, string newName)
    {
        if (symbol.Kind is not (SymbolKind.Method or SymbolKind.Property or SymbolKind.Field
            or SymbolKind.Event or SymbolKind.NamedType))
            return Array.Empty<string>();

        INamespaceOrTypeSymbol? container = symbol.ContainingType ?? (INamespaceOrTypeSymbol?)symbol.ContainingNamespace;
        if (container is null) return Array.Empty<string>();

        var clashes = new List<string>();
        foreach (var member in container.GetMembers(newName))
        {
            if (SymbolEqualityComparer.Default.Equals(member, symbol)) continue;
            clashes.Add($"{member.Kind} '{newName}' already exists in {container.Name}");
        }
        return clashes;
    }
}
