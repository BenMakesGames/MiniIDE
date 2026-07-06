using Avalonia.Media;

namespace MiniIde.Models;

public record ProjectEntry(string Path, string Name, ProjectKind Kind)
{
    public string IconGlyph => Kind.GetInfo().Glyph;
    public IBrush IconColor => Kind.GetInfo().Color;
    public bool IsRunnable => Kind.GetInfo().IsRunnable;
}
