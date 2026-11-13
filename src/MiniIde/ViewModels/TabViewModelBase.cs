using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace MiniIde.ViewModels;

public abstract partial class TabViewModelBase : ViewModelBase
{
    /// <summary>Namespaced identity used for all dedup/reuse (never the title): <c>run:</c>/<c>nuget:</c>/<c>git:</c>
    /// keys and the like. Two tabs may share a display title (e.g. a project named "NuGet") as long as their ids
    /// differ.</summary>
    public string TabId { get; }

    public abstract string Header { get; }

    public event Func<TabViewModelBase, Task>? RequestClose;

    protected TabViewModelBase(string tabId)
    {
        TabId = tabId;
    }

    [RelayCommand]
    public Task CloseAsync() => RequestClose?.Invoke(this) ?? Task.CompletedTask;
}
