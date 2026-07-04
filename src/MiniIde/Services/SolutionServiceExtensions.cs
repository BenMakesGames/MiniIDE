using MiniIde.Models;

namespace MiniIde.Services;

public static class SolutionServiceExtensions
{
    public static void EnsureExpanded(TreeNode projectNode) => SolutionService.PopulateProjectFiles(projectNode);
}
