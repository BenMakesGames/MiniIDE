using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using MiniIde.Models;

namespace MiniIde.Services;

public class SolutionService
{
    public string? SolutionPath { get; private set; }
    public IReadOnlyList<ProjectEntry> Projects { get; private set; } = System.Array.Empty<ProjectEntry>();

    public async Task<TreeNode> LoadAsync(string path, CancellationToken ct = default)
    {
        SolutionPath = path;
        var dir = Path.GetDirectoryName(path)!;
        var serializer = SolutionSerializers.GetSerializerByMoniker(path)
            ?? throw new System.InvalidOperationException($"Unrecognized solution format: {path}");
        SolutionModel model = await serializer.OpenAsync(path, ct);

        var entries = new List<ProjectEntry>();
        var root = new TreeNode { Name = Path.GetFileName(path), Kind = NodeKind.Solution, Path = path, IsExpanded = true };
        foreach (var p in model.SolutionProjects)
        {
            var abs = Path.GetFullPath(Path.Combine(dir, p.FilePath));
            var kind = ProjectClassifier.Classify(abs);
            var name = Path.GetFileNameWithoutExtension(p.FilePath);
            var display = $"{kind.ToString().ToLowerInvariant()} {name}";
            entries.Add(new ProjectEntry(abs, display, kind));
            var projNode = new TreeNode
            {
                Name = name,
                Kind = NodeKind.Project,
                Path = abs
            };
            PopulateProjectFiles(projNode);
            root.Children.Add(projNode);
        }
        Projects = entries;
        return root;
    }

    public static void PopulateProjectFiles(TreeNode projectNode)
    {
        if (projectNode.IsLoaded || projectNode.Path is null) return;
        var dir = Path.GetDirectoryName(projectNode.Path)!;
        var root = BuildFolder(dir, dir);
        foreach (var c in root.Children) projectNode.Children.Add(c);
        projectNode.IsLoaded = true;
    }

    private static TreeNode BuildFolder(string root, string dir)
    {
        var node = new TreeNode { Name = Path.GetFileName(dir), Kind = NodeKind.Folder, Path = dir };
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or ".vs" or ".git") continue;
            node.Children.Add(BuildFolder(root, sub));
        }
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".cs" or ".csproj" or ".json" or ".xml" or ".axaml" or ".xaml" or ".md" or ".txt" or ".editorconfig")
                node.Children.Add(new TreeNode { Name = Path.GetFileName(f), Kind = NodeKind.File, Path = f });
        }
        return node;
    }
}
