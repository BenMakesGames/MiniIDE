using System;
using System.IO;
using System.Threading.Tasks;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MiniIde.ViewModels;

public partial class EditorTabViewModel : ViewModelBase
{
    public string FilePath { get; }
    public string Header => System.IO.Path.GetFileName(FilePath) + (IsDirty ? " *" : "");
    public TextDocument Document { get; }
    public bool IsCSharp => System.IO.Path.GetExtension(FilePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private int _caretOffset;

    public event Func<EditorTabViewModel, Task>? RequestClose;

    public EditorTabViewModel(string filePath)
    {
        FilePath = filePath;
        Document = new TextDocument(File.ReadAllText(filePath));
        Document.TextChanged += (_, _) => { IsDirty = true; OnPropertyChanged(nameof(Header)); };
    }

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Header));

    [RelayCommand]
    public async Task SaveAsync()
    {
        await File.WriteAllTextAsync(FilePath, Document.Text);
        IsDirty = false;
    }

    [RelayCommand]
    public Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask;
}
