using System;
using System.Collections.Frozen;

namespace MiniIde.Models;

// Build artifacts and VCS/tooling directory names the IDE never descends into. Used by the Roslyn
// manifest walk (WorkspaceService) so it tracks the source set MSBuild's default glob would.
public static class IdeDirectories
{
    public static readonly FrozenSet<string> Pruned =
        new[] { ".git", "bin", "obj", "node_modules", ".vs", "packages" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
