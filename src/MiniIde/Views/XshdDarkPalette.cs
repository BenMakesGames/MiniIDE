using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace MiniIde.Views;

internal static class XshdDarkPalette
{
    private static readonly HashSet<string> _tuned = new();

    public static IHighlightingDefinition? Tune(string name)
    {
        var def = HighlightingManager.Instance.GetDefinition(name);
        if (def is null) return null;
        if (!_tuned.Add(name)) return def;
        foreach (var color in def.NamedHighlightingColors)
        {
            var target = name switch
            {
                "XML" => XmlColor(color.Name),
                "Json" => JsonColor(color.Name),
                _ => (Color?)null
            };
            if (target is { } c) color.Foreground = new SimpleHighlightingBrush(c);
        }
        return def;
    }

    private static Color? XmlColor(string n) => n switch
    {
        "Comment" => Color.FromRgb(106, 153, 85),
        "CData" => Color.FromRgb(206, 145, 120),
        "DocType" => Color.FromRgb(128, 128, 128),
        "XmlDeclaration" => Color.FromRgb(128, 128, 128),
        "XmlTag" => Color.FromRgb(86, 156, 214),
        "AttributeName" => Color.FromRgb(156, 220, 254),
        "AttributeValue" => Color.FromRgb(206, 145, 120),
        "Entity" => Color.FromRgb(215, 186, 125),
        "BrokenEntity" => Color.FromRgb(244, 71, 71),
        _ => null
    };

    private static Color? JsonColor(string n) => n switch
    {
        "Bool" => Color.FromRgb(86, 156, 214),
        "Number" => Color.FromRgb(181, 206, 168),
        "String" => Color.FromRgb(206, 145, 120),
        "Null" => Color.FromRgb(86, 156, 214),
        "FieldName" => Color.FromRgb(156, 220, 254),
        "Punctuation" => Color.FromRgb(220, 220, 220),
        _ => null
    };
}
