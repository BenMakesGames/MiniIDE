using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;

namespace MiniIde.ViewModels;

public abstract partial class TabViewModelBase : ViewModelBase
{
    /// <summary>Namespaced identity used for all dedup/reuse (never the title or raw path). File tabs use
    /// <see cref="FileId"/>; output tabs use <c>run:</c>/<c>nuget:</c> keys. Two tabs may share a display
    /// title (e.g. a project named "NuGet") as long as their ids differ.</summary>
    public string TabId { get; }

    /// <summary>The backing file, or <c>null</c> for tabs with no file (e.g. output tabs).</summary>
    public string? FilePath { get; }

    public virtual string Header => Path.GetFileName(FilePath) + (IsDirty ? " *" : "");

    [ObservableProperty] private bool _isDirty;

    public event Func<TabViewModelBase, Task>? RequestClose;

    public IAsyncRelayCommand SaveCommand { get; }

    protected TabViewModelBase(string tabId, string? filePath)
    {
        TabId = tabId;
        FilePath = filePath;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
    }

    public abstract Task SaveAsync();

    [RelayCommand]
    public Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(Header));

    /// <summary>The <c>file:</c>-namespaced identity for a path — normalized to a full, case-folded path so
    /// the same file always dedups to one tab regardless of how the path was spelled.</summary>
    public static string FileId(string path) => "file:" + Path.GetFullPath(path).ToLowerInvariant();

    public static TabViewModelBase CreateForFile(string path) =>
        Path.GetExtension(path).ToFileKind().GetInfo().OpensAsImageTab
            ? new ImageTabViewModel(path)
            : new EditorTabViewModel(path);
}
