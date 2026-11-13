using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniIde.ViewModels;

/// <summary>Owns the tab strip: the collection, the active-tab selection, and every operation on the strip —
/// open/show/get-or-create/close — behind one small interface. Every tab enters through <see cref="Open"/>, so
/// none can be added without its close wiring.
///
/// <para>The strip also owns the one active-tab side effect — <see cref="FileWatcherTabViewModel"/> polling —
/// so no caller has to re-wire it after an <see cref="ActiveTab"/> swap.</para></summary>
public partial class TabStrip : ViewModelBase
{
    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();

    [ObservableProperty] private TabViewModelBase? _activeTab;

    /// <summary>Adds <paramref name="tab"/> to the strip with its close wiring. Every tab enters through
    /// here, so none can be added without a working close path.</summary>
    public void Open(TabViewModelBase tab)
    {
        tab.RequestClose += CloseAsync;
        Tabs.Add(tab);
    }

    /// <summary>Activates <paramref name="tab"/>, putting it back on the strip first if it was closed — which is
    /// how a run whose tab was closed is reached again from its project card.</summary>
    public void Show(TabViewModelBase tab)
    {
        if (!Tabs.Contains(tab)) Tabs.Add(tab);
        ActiveTab = tab;
    }

    /// <summary>Gets-or-creates the tab with identity <paramref name="tabId"/> and activates it. Dedup keys on
    /// <see cref="TabViewModelBase.TabId"/>, never the header, so two tabs can share a title while staying
    /// distinct.</summary>
    public T GetOrCreate<T>(string tabId, Func<T> create) where T : TabViewModelBase
    {
        var tab = Tabs.OfType<T>().FirstOrDefault(t => t.TabId == tabId);
        if (tab is null)
        {
            tab = create();
            Open(tab);
        }
        ActiveTab = tab;
        return tab;
    }

    /// <summary>Gets-or-creates the shared output tab for <paramref name="tabId"/>, headered
    /// <paramref name="header"/>. For run output, NuGet restore log, and git transcript.</summary>
    public OutputTabViewModel ShowOutput(string tabId, string header) =>
        GetOrCreate(tabId, () => new OutputTabViewModel(tabId, header));

    /// <summary>A run tab's process deliberately survives its tab: only the tab's own Stop ends a run. Its card
    /// keeps a button for it, which is the way back. Returns Task (not async) to satisfy the
    /// <see cref="TabViewModelBase.RequestClose"/> contract; nothing here awaits.</summary>
    public Task CloseAsync(TabViewModelBase tab)
    {
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        return Task.CompletedTask;
    }

    partial void OnActiveTabChanged(TabViewModelBase? oldValue, TabViewModelBase? newValue)
    {
        (oldValue as FileWatcherTabViewModel)?.StopPolling();
        (newValue as FileWatcherTabViewModel)?.StartPolling();
    }
}
