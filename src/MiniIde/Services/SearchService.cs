using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using BenMakesGames.FileGrepper;
using MiniIde.Models;

namespace MiniIde.Services;

public class SearchService
{
    private readonly FileGrepper _grepper = new();

    // Build artifacts and VCS/tooling dirs the IDE never wants to search. The grep engine
    // itself has no baked-in prune list — exclusions live here, in the caller.
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".vs", "packages" };

    public async IAsyncEnumerable<FindHit> SearchAsync(
        string root, string query, bool regex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new GrepOptions(
            Regex: regex,
            SkipDirectory: static path => SkippedDirectories.Contains(Path.GetFileName(path)));

        await foreach (var hit in _grepper.GrepAsync(root, query, options, ct))
            yield return new FindHit(hit.File, hit.Line, hit.Column, hit.Preview);
    }
}
