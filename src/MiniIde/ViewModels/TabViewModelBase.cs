using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MiniIde.ViewModels;

public abstract partial class TabViewModelBase : ViewModelBase
{
    public string FilePath { get; }
    public virtual string Header => Path.GetFileName(FilePath) + (IsDirty ? " *" : "");

    [ObservableProperty] private bool _isDirty;

    public event Func<TabViewModelBase, Task>? RequestClose;

    public IAsyncRelayCommand SaveCommand { get; }

    protected TabViewModelBase(string filePath)
    {
        FilePath = filePath;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public abstract Task SaveAsync();

    [RelayCommand]
    public Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Header));

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };

    public static TabViewModelBase CreateForFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var imageExt in ImageExtensions)
            if (ext == imageExt) return new ImageTabViewModel(path);
        return new EditorTabViewModel(path);
    }
}
