using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniIde.ViewModels;

/// <summary>Owns the tab strip: the collection, the active-tab selection, and every operation on the strip —
/// open/show/get-or-create/close/revalidate — behind one small interface. Every tab enters through
/// <see cref="Open"/>, so none can be added without its close wiring.
///
/// <para>The strip also owns the two active-tab side effects — <see cref="FileWatcherTabViewModel"/> polling
/// and <see cref="EditorTabViewModel"/> revalidate-on-show — so no caller has to re-wire them after a
/// <see cref="ActiveTab"/> swap.</para></summary>
public partial class TabStrip : ViewModelBase
{
    private readonly Action<string> _setStatus;

    public TabStrip(Action<string> setStatus)
    {
        _setStatus = setStatus;
    }

    public ObservableCollection<TabViewModelBase> Tabs { get; } = new();

    [ObservableProperty] private TabViewModelBase? _activeTab;

    /// <summary>Opens (or re-activates) the tab for <paramref name="path"/>. Genuinely async: the file read
    /// happens off the UI thread. A file that can't be read reports to status rather than throwing into an
    /// <c>async void</c> event handler and taking the app down.</summary>
    public async Task OpenFileAsync(string path)
    {
        var id = TabViewModelBase.FileId(path);
        var existing = Tabs.FirstOrDefault(t => t.TabId == id);
        if (existing is not null) { ActiveTab = existing; return; }

        TabViewModelBase tab;
        try { tab = await TabViewModelBase.CreateForFileAsync(path); }
        catch (Exception ex) { _setStatus($"Could not open {Path.GetFileName(path)}: {ex.Message}"); return; }

        Open(tab);
        ActiveTab = tab;
    }

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

    // Returns Task (not async) so it still satisfies the RequestClose Func<,Task> contract: with save-on-close
    // gone under the read-only law, nothing here awaits.
    public Task CloseAsync(TabViewModelBase tab)
    {
        // A run tab's process deliberately survives its tab: only the tab's own Stop ends a run. Its card keeps
        // a button for it, which is the way back. An analysis, on the other hand, has nobody left to report to.
        (tab as ProblemsTabViewModel)?.Cancel();
        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (ActiveTab == tab)
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        return Task.CompletedTask;
    }

    /// <summary>Re-homes any tab open on a renamed file (its <c>TabId</c> is get-only, so it must be closed and
    /// reopened). A case-only rename leaves the case-folded <c>FileId</c> unchanged, so nothing needs doing.</summary>
    public async Task RehomeMovedAsync(string oldPath, string newPath)
    {
        var oldId = TabViewModelBase.FileId(oldPath);
        if (string.Equals(oldId, TabViewModelBase.FileId(newPath), StringComparison.Ordinal)) return;

        var open = Tabs.FirstOrDefault(t => t.TabId == oldId);
        if (open is null) return;
        var wasActive = ReferenceEquals(ActiveTab, open);
        await CloseAsync(open);
        if (wasActive) await OpenFileAsync(newPath);
    }

    /// <summary>Brings every open editor tab up to date with disk, eagerly — for the moments that deliberately
    /// bypass revalidate-on-show: an explicit "Reload solution", and the refresh a rename does after its own
    /// writes. Each tab stamp-gates its own read and content-compares before touching the buffer, so an
    /// unchanged file is a stat and a no-op — no flicker, no caret reset.</summary>
    public async Task ReloadEditorsAsync()
    {
        foreach (var tab in Tabs.OfType<EditorTabViewModel>().ToList())
            await tab.RevalidateFromDiskAsync();
    }

    /// <summary>Revalidates the currently-active editor tab against disk, if there is one. Called from
    /// focus-time refresh.</summary>
    public async Task RevalidateActiveAsync()
    {
        if (ActiveTab is EditorTabViewModel active) await RevalidateAsync(active);
    }

    /// <summary>Editor tabs revalidate when shown. The stamp gate inside means an unchanged file costs a stat,
    /// so activating a tab is never a disk read for nothing.</summary>
    partial void OnActiveTabChanged(TabViewModelBase? oldValue, TabViewModelBase? newValue)
    {
        (oldValue as FileWatcherTabViewModel)?.StopPolling();
        (newValue as FileWatcherTabViewModel)?.StartPolling();
        if (newValue is EditorTabViewModel tab) _ = RevalidateAsync(tab);
    }

    // Fire-and-forget from a property setter, so it must swallow into the status bar: an exception escaping
    // here would be an unobserved task, not something a caller could catch.
    private async Task RevalidateAsync(EditorTabViewModel tab)
    {
        try { await tab.RevalidateFromDiskAsync(); }
        catch (Exception ex) { _setStatus(ex.Message); }
    }
}
