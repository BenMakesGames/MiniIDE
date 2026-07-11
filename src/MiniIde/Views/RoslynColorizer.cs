using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Microsoft.CodeAnalysis.Classification;

namespace MiniIde.Views;

internal class RoslynColorizer : DocumentColorizingTransformer
{
    private readonly Func<string, Task<IReadOnlyList<ClassifiedSpan>>> _classify;
    private IReadOnlyList<ClassifiedSpan> _spans = System.Array.Empty<ClassifiedSpan>();

    public RoslynColorizer(Func<string, Task<IReadOnlyList<ClassifiedSpan>>> classify) { _classify = classify; }

    public async Task RefreshAsync(string source)
    {
        try { _spans = await _classify(source); }
        catch { _spans = System.Array.Empty<ClassifiedSpan>(); }
    }

    public void Clear() => _spans = System.Array.Empty<ClassifiedSpan>();

    /// <summary>The classification of the cached span covering <paramref name="offset"/>, or null if none.
    /// Reads the existing span cache synchronously — no reclassification.</summary>
    public string? ClassificationAt(int offset)
    {
        foreach (var span in _spans)
            if (offset >= span.TextSpan.Start && offset < span.TextSpan.End)
                return span.ClassificationType;
        return null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        int lineStart = line.Offset;
        int lineEnd = line.EndOffset;
        foreach (var span in _spans)
        {
            if (span.TextSpan.End <= lineStart) continue;
            if (span.TextSpan.Start >= lineEnd) break;
            var start = Math.Max(span.TextSpan.Start, lineStart);
            var end = Math.Min(span.TextSpan.End, lineEnd);
            var brush = BrushFor(span.ClassificationType);
            if (brush is null) continue;
            ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    // Shared, immutable brushes: ColorizeLine runs per visible span per redraw, so a fresh
    // SolidColorBrush per call would allocate dozens of throwaway brushes every frame.
    private static readonly IBrush KeywordBrush = new ImmutableSolidColorBrush(Color.FromRgb(86, 156, 214));
    private static readonly IBrush StringBrush = new ImmutableSolidColorBrush(Color.FromRgb(206, 145, 120));
    private static readonly IBrush NumberBrush = new ImmutableSolidColorBrush(Color.FromRgb(181, 206, 168));
    private static readonly IBrush CommentBrush = new ImmutableSolidColorBrush(Color.FromRgb(106, 153, 85));
    private static readonly IBrush TypeBrush = new ImmutableSolidColorBrush(Color.FromRgb(78, 201, 176));
    private static readonly IBrush MethodBrush = new ImmutableSolidColorBrush(Color.FromRgb(220, 220, 170));
    private static readonly IBrush MemberBrush = new ImmutableSolidColorBrush(Color.FromRgb(156, 220, 254));
    private static readonly IBrush NamespaceBrush = new ImmutableSolidColorBrush(Color.FromRgb(200, 200, 200));

    private static IBrush? BrushFor(string kind) => kind switch
    {
        ClassificationTypeNames.Keyword or ClassificationTypeNames.ControlKeyword or ClassificationTypeNames.PreprocessorKeyword
            => KeywordBrush,
        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral or ClassificationTypeNames.StringEscapeCharacter
            => StringBrush,
        ClassificationTypeNames.NumericLiteral
            => NumberBrush,
        ClassificationTypeNames.Comment or ClassificationTypeNames.XmlDocCommentText
            => CommentBrush,
        ClassificationTypeNames.ClassName or ClassificationTypeNames.StructName or ClassificationTypeNames.InterfaceName
            or ClassificationTypeNames.EnumName or ClassificationTypeNames.DelegateName or ClassificationTypeNames.RecordClassName
            => TypeBrush,
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName
            => MethodBrush,
        ClassificationTypeNames.PropertyName or ClassificationTypeNames.FieldName or ClassificationTypeNames.ConstantName
        or ClassificationTypeNames.LocalName or ClassificationTypeNames.ParameterName
            => MemberBrush,
        ClassificationTypeNames.NamespaceName => NamespaceBrush,
        _ => null
    };
}
