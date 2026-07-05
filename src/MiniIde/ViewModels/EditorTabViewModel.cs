using System.IO;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniIde.Models;

namespace MiniIde.ViewModels;

public partial class EditorTabViewModel : TabViewModelBase
{
    public TextDocument Document { get; }
    public HighlightMode Mode { get; }

    [ObservableProperty] private int _caretOffset;

    public EditorTabViewModel(string filePath) : base(filePath)
    {
        Mode = HighlightModeExtensions.FromExtension(Path.GetExtension(filePath));
        Document = new TextDocument(File.ReadAllText(filePath));
        Document.TextChanged += (_, _) => { IsDirty = true; OnPropertyChanged(nameof(Header)); };
    }

    public override async Task SaveAsync()
    {
        await File.WriteAllTextAsync(FilePath, Document.Text);
        IsDirty = false;
    }
}
