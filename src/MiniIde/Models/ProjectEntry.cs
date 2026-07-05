using Avalonia.Media;

namespace MiniIde.Models;

public record ProjectEntry(string Path, string Name, ProjectKind Kind)
{
    public string IconGlyph => FileIconMap.FromProjectKind(Kind).Glyph;
    public IBrush IconColor => FileIconMap.FromProjectKind(Kind).Color;
    public bool IsRunnable => Kind != ProjectKind.Lib;
}
