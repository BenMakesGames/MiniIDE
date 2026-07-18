using System;
using AvaloniaEdit;
using MiniIde.Models;
using MiniIde.Services;
using MiniIde.ViewModels;

namespace MiniIde.Views;

/// <summary>What the code-editor context menu needs to know about the caret, resolved in one place: the term
/// to search for, and whether a symbol action (find usages / go to definition) can resolve there.
///
/// <para>This is editor/language semantics, not window plumbing — it lives here so <c>MainWindow</c> can set
/// three menu properties from one value instead of re-deriving the answer inline.</para></summary>
/// <param name="Term">The selection if there is one, else the identifier under the caret, else null.</param>
/// <param name="SymbolEligible">Whether the caret sits on something a Roslyn symbol query could resolve.</param>
/// <param name="ImplementationEligible">Whether Go to Implementation could plausibly resolve here (an
/// interface or an overridable member) — a stricter, kind-aware subset of <paramref name="SymbolEligible"/>.</param>
/// <param name="SubclassEligible">Whether Go to Subclasses could plausibly resolve here (a class,
/// record-class, or interface) — likewise a kind-aware subset.</param>
internal readonly record struct CodeSymbolContext(
    string? Term, bool SymbolEligible, bool ImplementationEligible, bool SubclassEligible)
{
    public static readonly CodeSymbolContext None = new(null, false, false, false);

    /// <param name="editor">The active code editor, or null when the active tab isn't one.</param>
    /// <param name="colorizer">That editor's colorizer, for its cached classifications.</param>
    public static CodeSymbolContext At(TextEditor? editor, RoslynColorizer? colorizer)
    {
        if (editor?.Document is null || editor.DataContext is not EditorTabViewModel tab) return None;

        var text = editor.Document.Text;
        var offset = editor.CaretOffset;

        // Symbol actions need three things: a C# document, an identifier character actually under the caret,
        // and a classification that names something resolvable. The caret already sits on the clicked token
        // (TabEditorBinder places it there on right-click), so this agrees with the F12 / Shift+F12 shortcuts.
        // The two kind-aware gates share the same first two conditions and the same cached covering set; only
        // the allowlist differs, so Go to Implementation / Go to Subclasses light up on a narrower set than the
        // coarse SymbolEligible (see SymbolClassifications).
        var isCSharpIdentifier =
            tab.Mode == HighlightMode.CSharp
            && offset >= 0 && offset < text.Length && IsIdentifierChar(text[offset]);
        var covering = colorizer?.ClassificationsAt(offset) ?? Array.Empty<string>();

        var symbolEligible = isCSharpIdentifier && SymbolClassifications.AllowSymbolActions(covering);
        var implementationEligible = isCSharpIdentifier && SymbolClassifications.AllowImplementationActions(covering);
        var subclassEligible = isCSharpIdentifier && SymbolClassifications.AllowSubclassActions(covering);

        return new CodeSymbolContext(
            TermAt(editor, text, offset), symbolEligible, implementationEligible, subclassEligible);
    }

    /// <summary>The query term for the Search action: the selection if non-empty, else the identifier run
    /// under the caret, else null.</summary>
    private static string? TermAt(TextEditor editor, string text, int offset)
    {
        var selection = editor.SelectedText;
        if (!string.IsNullOrEmpty(selection)) return selection;

        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;
        int start = offset, end = offset;
        while (start > 0 && IsIdentifierChar(text[start - 1])) start--;
        while (end < text.Length && IsIdentifierChar(text[end])) end++;
        return end > start ? text[start..end] : null;
    }

    private static bool IsIdentifierChar(char c) => c == '_' || char.IsLetterOrDigit(c);

    /// <summary>Menu-safe rendering of <see cref="Term"/>: ellipsized so a long selection can't stretch the
    /// menu across the screen, and with underscores doubled.
    ///
    /// <para>A <c>MenuItem</c> header reads <c>_</c> as an access-key marker — that's how the static headers
    /// get their mnemonics ("_Open in Explorer"). So a term like <c>_active</c> would render as "active" with
    /// the "a" underlined, quietly lying about what will be searched for. <c>__</c> escapes to a literal
    /// underscore. Only the <em>header</em> needs this; the query itself uses the raw <see cref="Term"/>.</para></summary>
    public string SearchHeader
    {
        get
        {
            if (string.IsNullOrEmpty(Term)) return "Search solution";
            var shown = Term.Length <= 30 ? Term : Term[..30] + "…";
            return $"Search solution for \"{shown.Replace("_", "__")}\"";
        }
    }
}
