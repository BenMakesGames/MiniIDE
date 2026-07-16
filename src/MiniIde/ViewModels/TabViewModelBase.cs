using System;
using System.IO;
using System.Threading.Tasks;
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

    // No dirty marker: the editor is a read-only window onto disk (see README's "no hand-typed edits"
    // law), so a tab's header is exactly its file name and never carries a " *". The `?? ""` only guards the
    // fileless base case — every fileless tab (output) overrides Header, so in practice FilePath is non-null.
    public virtual string Header => Path.GetFileName(FilePath) ?? "";

    public event Func<TabViewModelBase, Task>? RequestClose;

    protected TabViewModelBase(string tabId, string? filePath)
    {
        TabId = tabId;
        FilePath = filePath;
    }

    [RelayCommand]
    public Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>The <c>file:</c>-namespaced identity for a path — normalized to a full, case-folded path so
    /// the same file always dedups to one tab regardless of how the path was spelled.</summary>
    public static string FileId(string path) => "file:" + Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>Reads <paramref name="path"/> and builds the tab that can show it. Async because the read is
    /// real I/O — the tab types take their already-loaded content, so there is no constructor that blocks the
    /// UI thread on the disk. Throws if the file can't be read; the caller decides how to surface that.</summary>
    public static async Task<TabViewModelBase> CreateForFileAsync(string path) =>
        Path.GetExtension(path).ToFileKind().GetInfo().OpensAsImageTab
            ? new ImageTabViewModel(path)
            : new EditorTabViewModel(path, await File.ReadAllTextAsync(path));
}
