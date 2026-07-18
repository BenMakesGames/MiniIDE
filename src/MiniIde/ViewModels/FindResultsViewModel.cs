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
/// itself (<see cref="SearchAsync"/>) or by find-references handing it hits (<see cref="ShowReferences"/>).
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

    public ObservableCollection<FindHit> Results { get; } = new();

    public FindResultsViewModel(SearchService search, SolutionService sln, Func<SourceLocation, Task> openHit)
    { _search = search; _sln = sln; _openHit = openHit; }

    partial void OnSelectedChanged(FindHit? value)
    {
        if (value is null) return;
        _ = _openHit(value.Location);
    }

    /// <summary>Replaces the panel's contents with reference results. The one entry point for find-references
    /// — the view never touches <see cref="Results"/> or <see cref="Status"/> itself.</summary>
    public void ShowReferences(IReadOnlyList<FindHit> references)
    {
        Results.Clear();
        foreach (var r in references) Results.Add(r);
        Status = Plural.Of(references.Count, "reference");
    }

    /// <summary>Clears the panel and explains why — used when no symbol resolves under the caret.</summary>
    public void ShowNoResults(string status)
    {
        Results.Clear();
        Status = status;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrEmpty(Query) || _sln.SolutionPath is null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Results.Clear();
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
