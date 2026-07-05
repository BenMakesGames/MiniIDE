using Avalonia.Media;

namespace MiniIde.Models;

public static class FileIconPalette
{
    public static readonly IBrush CSharp     = Brush("#29A03F");
    public static readonly IBrush Json       = Brush("#F5B301");
    public static readonly IBrush Xml        = Brush("#E37933");
    public static readonly IBrush Csproj     = Brush("#B026AD");
    public static readonly IBrush Folder     = Brush("#DCB67A");
    public static readonly IBrush Text       = Brush("#B0B0B0");
    public static readonly IBrush Audio      = Brush("#4EC9B0");
    public static readonly IBrush Image      = Brush("#7D44DD");
    public static readonly IBrush Video      = Brush("#569CD6");
    public static readonly IBrush ProjectExe = Brush("#569CD6");
    public static readonly IBrush ProjectLib = Brush("#569CD6");
    public static readonly IBrush ProjectWeb = Brush("#569CD6");
    public static readonly IBrush ProjectTst = Brush("#569CD6");
    public static readonly IBrush Unknown    = Brush("#808080");

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
