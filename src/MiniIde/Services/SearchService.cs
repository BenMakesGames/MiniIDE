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

    public async IAsyncEnumerable<FindHit> SearchAsync(
        string root, string query, bool regex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var options = new GrepOptions(
            Regex: regex,
            SkipDirectory: static path => IdeDirectories.Pruned.Contains(Path.GetFileName(path)));

        await foreach (var hit in _grepper.GrepAsync(root, query, options, ct))
            yield return new FindHit(new SourceLocation(hit.File, hit.Line, hit.Column), hit.Preview);
    }
}
