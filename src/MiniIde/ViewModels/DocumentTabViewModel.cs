using AvaloniaEdit.Document;

namespace MiniIde.ViewModels;

/// <summary>A tab whose content is a <see cref="TextDocument"/> rendered by an AvaloniaEdit
/// <c>TextEditor</c> — a file-backed code tab or a streamed output tab. This is the single contract the
/// view's editor binder talks to; tabs with no document (e.g. <see cref="ImageTabViewModel"/>) derive from
/// <see cref="TabViewModelBase"/> directly. The document is supplied at construction and never swapped, so
/// the binder can key its per-tab wiring on it.</summary>
public abstract class DocumentTabViewModel : TabViewModelBase
{
    public TextDocument Document { get; }

    protected DocumentTabViewModel(string tabId, string? filePath, TextDocument document)
        : base(tabId, filePath) => Document = document;
}
