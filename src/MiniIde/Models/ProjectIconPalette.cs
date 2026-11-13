using Avalonia.Media;

namespace MiniIde.Models;

public static class ProjectIconPalette
{
    public static readonly IBrush Exe = Brush("#569CD6");
    public static readonly IBrush Lib = Brush("#969696");
    public static readonly IBrush Web = Brush("#AC6ACB");
    public static readonly IBrush Tst = Brush("#DAAD66");

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
