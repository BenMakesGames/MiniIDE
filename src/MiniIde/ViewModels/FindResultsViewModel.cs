using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>The Find panel. Owns its own results and status — it is filled either by a text search it runs
/// itself (<see cref="SearchAsync"/>) or by a symbol query handing it hits (<see cref="ShowResults"/>).
/// Both produce <see cref="FindHit"/>s, so the panel never learns which one it is showing.</summary>
public partial class FindResultsViewModel : ViewModelBase
{
    private readonly SearchService _search;
    private readonly SolutionService _sln;
    private readonly Func<SourceLocation, Task> _openHit;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _caseSensitive;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private FindHit? _selected;

    // A symbol query (find-usages / implementations / subclasses) puts the panel in "context mode": the input
    // row (query box, Search button, checkboxes) is swapped for a banner naming what was searched. A plain-text
    // search clears it. The XAML gates the two mutually-exclusive rows on this flag (IsVisible / !IsVisible).
    [ObservableProperty] private bool _isContextResult;
    [ObservableProperty] private string _contextLabel = "";

    public ObservableCollection<FindHit> Results { get; } = new();

    public FindResultsViewModel(SearchService search, SolutionService sln, Func<SourceLocation, Task> openHit)
    { _search = search; _sln = sln; _openHit = openHit; }

    partial void OnSelectedChanged(FindHit? value)
    {
        if (value is null) return;
        _ = _openHit(value.Location);
    }

    /// <summary>Replaces the panel's contents with symbol-query results. The one entry point for every
    /// symbol-driven fill — find-references, go-to-implementation, go-to-subclasses — so the panel stays
    /// origin-agnostic: the caller supplies the result <paramref name="noun"/> ("reference", "implementation",
    /// "subclass") and the status reads "N nouns". Pass <paramref name="pluralSuffix"/> for a noun whose plural
    /// isn't a bare "s" ("subclass" → "es"). The view never touches <see cref="Results"/> or
    /// <see cref="Status"/> itself.
    ///
    /// <para>Puts the panel in context mode: <paramref name="bannerLabel"/> (e.g. <c>Usages of "Foo"</c>)
    /// replaces the input row until a text search runs or the banner is closed.</para></summary>
    public void ShowResults(IReadOnlyList<FindHit> hits, string noun, string bannerLabel, string pluralSuffix = "s")
    {
        Results.Clear();
        foreach (var r in hits) Results.Add(r);
        Status = Plural.Of(hits.Count, noun, pluralSuffix);
        ContextLabel = bannerLabel;
        IsContextResult = true;
    }

    /// <summary>Clears the panel and explains why — used when no symbol resolves under the caret. Stays in
    /// normal (input-row) mode: there is nothing to name, so no banner.</summary>
    public void ShowNoResults(string status)
    {
        Results.Clear();
        Status = status;
        ContextLabel = "";
        IsContextResult = false;
    }

    /// <summary>Reverts a symbol-query banner to the normal input row, clearing the inputs (query blank, both
    /// checkboxes off) but <b>leaving <see cref="Results"/> and <see cref="Status"/> untouched</b> — the hits
    /// the banner described are still there to browse.</summary>
    [RelayCommand]
    private void CloseContext()
    {
        IsContextResult = false;
        ContextLabel = "";
        Query = "";
        UseRegex = false;
        CaseSensitive = false;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(Query) || _sln.SolutionPath is null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Results.Clear();
        // A text search always shows the input row — leave context mode if a symbol query put us in it.
        IsContextResult = false;
        ContextLabel = "";
        IsSearching = true;
        Status = "Searching...";
        var root = System.IO.Path.GetDirectoryName(_sln.SolutionPath)!;
        try
        {
            await foreach (var hit in _search.SearchAsync(root, Query, UseRegex, CaseSensitive, _cts.Token))
                Dispatcher.UIThread.Post(() => Results.Add(hit));
            Status = Plural.Of(Results.Count, "match", "es");
        }
        catch (OperationCanceledException) { Status = "Canceled"; }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsSearching = false; }
    }
}
