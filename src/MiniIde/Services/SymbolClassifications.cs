using System.Collections.Generic;
using Microsoft.CodeAnalysis.Classification;

namespace MiniIde.Services;

/// <summary>Decides whether the thing under the caret is something a Roslyn symbol query (go to definition /
/// find usages) could resolve, based on how Roslyn classified it.</summary>
public static class SymbolClassifications
{
    /// <summary>The classifications that name a resolvable symbol. An <em>allowlist</em>, not a denylist of
    /// keywords/strings/comments: a classification Roslyn adds in future should default to "not a symbol"
    /// rather than silently enabling Find-usages on it. Exact <see cref="ClassificationTypeNames"/> constants —
    /// no substring guessing at Roslyn's dotted variants ("keyword - control", "string - verbatim", …).</summary>
    private static readonly HashSet<string> Resolvable =
    [
        ClassificationTypeNames.Identifier,
        ClassificationTypeNames.ClassName,
        ClassificationTypeNames.StructName,
        ClassificationTypeNames.InterfaceName,
        ClassificationTypeNames.EnumName,
        ClassificationTypeNames.DelegateName,
        ClassificationTypeNames.RecordClassName,
        ClassificationTypeNames.RecordStructName,
        ClassificationTypeNames.TypeParameterName,
        ClassificationTypeNames.MethodName,
        ClassificationTypeNames.ExtensionMethodName,
        ClassificationTypeNames.PropertyName,
        ClassificationTypeNames.FieldName,
        ClassificationTypeNames.EventName,
        ClassificationTypeNames.ConstantName,
        ClassificationTypeNames.EnumMemberName,
        ClassificationTypeNames.LocalName,
        ClassificationTypeNames.ParameterName,
        ClassificationTypeNames.LabelName,
        ClassificationTypeNames.NamespaceName,
    ];

    /// <summary>Whether a symbol action can plausibly resolve at a position classified as
    /// <paramref name="covering"/>.
    ///
    /// <para>Takes <em>every</em> classification covering the position, not just one, because Roslyn layers
    /// <b>additive</b> classifications on top of the semantic one: a <c>static</c> field is reported as both
    /// "field name" <em>and</em> "static symbol", a reassigned local as both "local name" and "reassigned
    /// variable". Asking only the first span makes eligibility depend on which of the two Roslyn happens to
    /// return first — so a static field's menu would enable or grey out arbitrarily.</para>
    ///
    /// <para>An empty set means the document hasn't been classified yet (the caller opened a file and
    /// right-clicked before the classifier finished). Allow it: the query itself reports "No symbol found"
    /// cheaply, which beats greying out the menu on a file that has only just opened.</para></summary>
    public static bool AllowSymbolActions(IReadOnlyList<string> covering)
    {
        if (covering.Count == 0) return true;
        foreach (var classification in covering)
            if (Resolvable.Contains(classification)) return true;
        return false;
    }
}
