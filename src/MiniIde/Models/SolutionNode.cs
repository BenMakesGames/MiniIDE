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

    /// <summary>Whether this node is a project — the only kind the tree offers a Run menu on. Files and
    /// folders are not runnable things, and the solution root has its own separate context menu.</summary>
    public bool IsProject => Kind == NodeKind.Project;

    /// <summary>Whether this node can be handed to the app Windows registered for it. Folders are excluded:
    /// shell-executing a folder only opens Explorer, duplicating the "Open in Explorer" action beside it.</summary>
    public bool CanOpenExternally => Path is not null && Kind != NodeKind.Folder;
}
