using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

public partial class FindResultsViewModel : ViewModelBase
{
    private readonly SearchService _search;
    private readonly SolutionService _sln;
    private readonly Func<string, int, int, Task> _openHit;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private FindHit? _selected;

    public ObservableCollection<FindHit> Results { get; } = new();

    public FindResultsViewModel(SearchService search, SolutionService sln, Func<string, int, int, Task> openHit)
    { _search = search; _sln = sln; _openHit = openHit; }

    partial void OnSelectedChanged(FindHit? value)
    {
        if (value is null) return;
        _ = _openHit(value.File, value.Line, value.Column);
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
            await foreach (var hit in _search.SearchAsync(root, Query, UseRegex, _cts.Token))
                Dispatcher.UIThread.Post(() => Results.Add(hit));
            Status = $"{Results.Count} match(es)";
        }
        catch (OperationCanceledException) { Status = "Canceled"; }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsSearching = false; }
    }
}
