using System;

namespace MiniIde.Models;

public enum HighlightMode { CSharp, Xml, Json, None }

public static class HighlightModeExtensions
{
    public static HighlightMode FromExtension(string extension)
    {
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return HighlightMode.CSharp;
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase)) return HighlightMode.Xml;
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return HighlightMode.Json;
        return HighlightMode.None;
    }
}
