using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using MiniIde.Models;

namespace MiniIde.Services;

public class SolutionService
{
    public string? SolutionPath { get; private set; }
    public IReadOnlyList<ProjectEntry> Projects { get; private set; } = System.Array.Empty<ProjectEntry>();

    /// <summary>The build configurations the loaded solution declares — <c>Debug</c> and <c>Release</c>
    /// for one that declares none, plus any custom entries a <c>.slnx</c> or a classic <c>.sln</c> adds.
    /// Empty before any solution is open. Solution-level on purpose: asking each project instead costs an
    /// MSBuild process launch apiece for an answer the already-open solution model has for free.</summary>
    public IReadOnlyList<string> Configurations { get; private set; } = System.Array.Empty<string>();

    /// <summary>The solution-relative form of an absolute path, anchored on the solution directory.
    /// Returns <paramref name="absolutePath"/> unchanged when no solution is loaded. The solution file
    /// itself yields its bare filename.</summary>
    public string ToRelativePath(string absolutePath) =>
        SolutionPath is null
            ? absolutePath
            : Path.GetRelativePath(Path.GetDirectoryName(SolutionPath)!, absolutePath);

    /// <summary>Reads the solution's project list and configurations. Metadata only — no MSBuild evaluation —
    /// so it is cheap enough to re-run whenever a solution or project file changes on disk.</summary>
    public async Task LoadAsync(string path, CancellationToken ct = default)
    {
        var serializer = SolutionSerializers.GetSerializerByMoniker(path)
            ?? throw new System.InvalidOperationException($"Unrecognized solution format: {path}");
        var model = await serializer.OpenAsync(path, ct);
        var dir = Path.GetDirectoryName(path)!;

        Projects = model.SolutionProjects
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.FilePath)))
            .Select(abs => new ProjectEntry(abs, Path.GetFileNameWithoutExtension(abs), ProjectClassifier.Classify(abs)))
            // Runnable projects first: they are what the cards are mostly for, and a library's card has no Run.
            .OrderBy(e => !e.IsRunnable)
            .ThenBy(e => e.Name, System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Configurations = model.BuildTypes.ToArray();
        SolutionPath = path;
    }
}
