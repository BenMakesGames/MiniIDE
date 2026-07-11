namespace MiniIde.Models;

/// <summary>A navigable hit with the source line it was found on — produced by both the text search
/// (<c>SearchService</c>) and find-references (<c>WorkspaceService</c>), which is why the Find panel can
/// render either without caring which one filled it.</summary>
public record FindHit(SourceLocation Location, string Preview);
