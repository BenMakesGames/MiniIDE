using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;

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

    public static TabViewModelBase CreateForFile(string path) =>
        Path.GetExtension(path).ToFileKind().GetInfo().OpensAsImageTab
            ? new ImageTabViewModel(path)
            : new EditorTabViewModel(path);
}
