using System;
using System.Collections.Frozen;

namespace MiniIde.Models;

// Build artifacts and VCS/tooling directory names the IDE never descends into. Shared by the
// solution-tree walker (SolutionService.BuildFolder) and the global search (SearchService) so the
// two can't drift — previously each kept its own literal list and they disagreed.
public static class IdeDirectories
{
    public static readonly FrozenSet<string> Pruned =
        new[] { ".git", "bin", "obj", "node_modules", ".vs", "packages" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
