using System;
using AvaloniaEdit;
using MiniIde.Models;
using MiniIde.Services;
using MiniIde.ViewModels;

namespace MiniIde.Views;

/// <summary>What the code-editor context menu needs to know about the caret: whether a symbol action (go to
/// definition / rename) can resolve there.
///
/// <para>This is editor/language semantics, not window plumbing — it lives here so <c>MainWindow</c> doesn't
/// re-derive the answer inline.</para></summary>
internal static class CodeSymbolContext
{
    /// <summary>Whether the caret sits on something a Roslyn symbol query could resolve. Needs three things: a
    /// C# document, an identifier character actually under the caret, and a classification that names something
    /// resolvable. The caret already sits on the clicked token (TabEditorBinder places it there on right-click),
    /// so this agrees with the F12 shortcut.</summary>
    /// <param name="editor">The active code editor, or null when the active tab isn't one.</param>
    /// <param name="colorizer">That editor's colorizer, for its cached classifications.</param>
    public static bool IsSymbolEligible(TextEditor? editor, RoslynColorizer? colorizer)
    {
        if (editor?.Document is null || editor.DataContext is not EditorTabViewModel tab) return false;

        var text = editor.Document.Text;
        var offset = editor.CaretOffset;
        var isCSharpIdentifier =
            tab.Mode == HighlightMode.CSharp
            && offset >= 0 && offset < text.Length && IsIdentifierChar(text[offset]);
        var covering = colorizer?.ClassificationsAt(offset) ?? Array.Empty<string>();
        return isCSharpIdentifier && SymbolClassifications.AllowSymbolActions(covering);
    }

    private static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);
}
