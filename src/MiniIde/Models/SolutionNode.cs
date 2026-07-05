using System.Collections.ObjectModel;
using Avalonia.Media;

namespace MiniIde.Models;

public enum NodeKind { Solution, Project, Folder, File }

public class TreeNode
{
    public required string Name { get; init; }
    public required NodeKind Kind { get; init; }
    public string? Path { get; init; }
    public ProjectKind? ProjectKind { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = new();
    public bool IsExpanded { get; set; }
    public bool IsLoaded { get; set; }

    public string IconGlyph => FileIconMap.From(this).Glyph;
    public IBrush IconColor => FileIconMap.From(this).Color;
}
