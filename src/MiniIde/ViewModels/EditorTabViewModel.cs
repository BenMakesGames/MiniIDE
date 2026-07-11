using System.IO;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using MiniIde.Models;

namespace MiniIde.ViewModels;

/// <summary>A file-backed code tab. Construct via <see cref="TabViewModelBase.CreateForFileAsync"/> — the
/// content is read off the UI thread and handed in, so nothing here blocks on the disk.</summary>
public class EditorTabViewModel : DocumentTabViewModel
{
    public HighlightMode Mode { get; }

    internal EditorTabViewModel(string filePath, string content)
        : base(FileId(filePath), filePath, new TextDocument(content))
    {
        Mode = Path.GetExtension(filePath).ToFileKind().GetInfo().Highlight;
        Document.TextChanged += (_, _) => IsDirty = true;
    }

    public override async Task SaveAsync()
    {
        await File.WriteAllTextAsync(FilePath!, Document.Text);
        IsDirty = false;
    }
}
