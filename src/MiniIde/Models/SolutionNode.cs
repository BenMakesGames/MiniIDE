using System.Collections.ObjectModel;

namespace MiniIde.Models;

public enum NodeKind { Solution, Project, Folder, File }

public class TreeNode
{
    public required string Name { get; init; }
    public required NodeKind Kind { get; init; }
    public string? Path { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = new();
    public bool IsExpanded { get; set; }
    public bool IsLoaded { get; set; }
}
