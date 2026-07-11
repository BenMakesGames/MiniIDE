using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

public partial class ProblemsViewModel : ViewModelBase
{
    private readonly WorkspaceService _workspace;
    private readonly SolutionService _sln;
    private readonly Func<string, int, int, Task> _openHit;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<ProblemItem> _results = System.Array.Empty<ProblemItem>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private bool _showPlaceholder = true;
    [ObservableProperty] private string _placeholder = "Press Refresh to analyze the solution.";
    [ObservableProperty] private ProblemGrouping _grouping = ProblemGrouping.ByFile;

    public ObservableCollection<ProblemGroup> Groups { get; } = new();

    // Radio-button facades over the enum: setting one true selects that mode; OnGroupingChanged re-notifies
    // both so the other radio unchecks. Avoids a bool<->enum converter for a two-value toggle.
    public bool IsByFile { get => Grouping == ProblemGrouping.ByFile; set { if (value) Grouping = ProblemGrouping.ByFile; } }
    public bool IsByCode { get => Grouping == ProblemGrouping.ByCode; set { if (value) Grouping = ProblemGrouping.ByCode; } }

    public ProblemsViewModel(WorkspaceService workspace, SolutionService sln, Func<string, int, int, Task> openHit)
    {
        _workspace = workspace;
        _sln = sln;
        _openHit = openHit;
    }

    /// <summary>Re-evaluates the Refresh command's enablement. Called by the owner once a solution loads,
    /// since <see cref="SolutionService.SolutionPath"/> is not observable.</summary>
    public void NotifyCanRefreshChanged() => RefreshCommand.NotifyCanExecuteChanged();

    private bool CanRefresh() => !IsBusy && _sln.SolutionPath is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_sln.SolutionPath is null) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        IsBusy = true;
        Status = "Analyzing...";
        // Surface per-project progress in this panel's own status bar for the duration of the run only —
        // Workspace.Progress fires on a background thread, and scoping the subscription keeps unrelated
        // workspace loads (go-to-def, find-refs) from writing over the results summary.
        void OnProgress(string m) => Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_cts, cts)) Status = m; });
        _workspace.Progress += OnProgress;
        try
        {
            await _workspace.EnsureLoadedAsync(_sln.SolutionPath, ct);
            var diagnostics = await _workspace.GetDiagnosticsAsync(ct);
            ct.ThrowIfCancellationRequested();
            _results = diagnostics;
            ErrorCount = diagnostics.Count(d => d.Severity == ProblemSeverity.Error);
            WarningCount = diagnostics.Count(d => d.Severity == ProblemSeverity.Warning);
            RebuildTree();
            Placeholder = "No problems found.";
            Status = $"{Count(ErrorCount, "error")}, {Count(WarningCount, "warning")}";
        }
        // Only the current run may touch shared state — a superseded run (its _cts already replaced by a newer
        // refresh) must stay silent so it can't reset IsBusy or clobber the newer run's status.
        catch (OperationCanceledException) { if (ReferenceEquals(_cts, cts)) Status = "Canceled"; }
        catch (Exception ex) { if (ReferenceEquals(_cts, cts)) Status = ex.Message; }
        finally { _workspace.Progress -= OnProgress; if (ReferenceEquals(_cts, cts)) IsBusy = false; }
    }

    /// <summary>Activates a leaf via the injected open-callback. No-op for locationless diagnostics.</summary>
    public void Activate(ProblemLeaf leaf)
    {
        if (!leaf.HasLocation) return;
        _ = _openHit(leaf.Item.File!, leaf.Item.Line, leaf.Item.Column);
    }

    partial void OnGroupingChanged(ProblemGrouping value)
    {
        OnPropertyChanged(nameof(IsByFile));
        OnPropertyChanged(nameof(IsByCode));
        RebuildTree();
    }

    // Re-groups the already-analyzed results without re-running analysis.
    private void RebuildTree()
    {
        Groups.Clear();
        var groups = Grouping == ProblemGrouping.ByFile ? BuildByFile() : BuildByCode();
        foreach (var g in groups) Groups.Add(g);
        ShowPlaceholder = Groups.Count == 0;
    }

    // File → issues in that file (locationless collapse into a single "(No file)" group). Children by line.
    private IEnumerable<ProblemGroup> BuildByFile() =>
        _results
            .GroupBy(p => p.File)
            .Select(g =>
            {
                var name = g.Key is null ? "(No file)" : _sln.ToRelativePath(g.Key);
                var items = g.OrderBy(p => p.Line).ThenBy(p => p.Column).ToList();
                var leaves = items.Select(p => new ProblemLeaf(p, ByFileDisplay(p))).ToList();
                var hasError = items.Any(p => p.Severity == ProblemSeverity.Error);
                return new ProblemGroup($"{name} ({items.Count})", leaves, hasError, name);
            })
            .OrderBy(g => g.HasError ? 0 : 1)
            .ThenBy(g => g.SortKey, StringComparer.OrdinalIgnoreCase);

    // Diagnostic code → occurrences across files. Header carries a representative message + count.
    private IEnumerable<ProblemGroup> BuildByCode() =>
        _results
            .GroupBy(p => p.Id)
            .Select(g =>
            {
                var items = g
                    .OrderBy(p => p.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Line).ThenBy(p => p.Column)
                    .ToList();
                var leaves = items.Select(p => new ProblemLeaf(p, ByCodeDisplay(p))).ToList();
                var hasError = items.Any(p => p.Severity == ProblemSeverity.Error);
                return new ProblemGroup($"{g.Key} — {items[0].Message} ({items.Count})", leaves, hasError, g.Key);
            })
            .OrderBy(g => g.HasError ? 0 : 1)
            .ThenBy(g => g.SortKey, StringComparer.OrdinalIgnoreCase);

    // By-file leaf: parent shows the file, so lead with the location + code.
    private static string ByFileDisplay(ProblemItem p) =>
        p.HasLocation ? $"{p.Id}  Ln {p.Line}: {p.Message}" : $"{p.Id}: {p.Message}";

    // By-code leaf: parent shows the code, so lead with the file:line where it occurred.
    private string ByCodeDisplay(ProblemItem p) =>
        p.HasLocation ? $"{_sln.ToRelativePath(p.File!)}({p.Line},{p.Column}): {p.Message}" : $"(no file): {p.Message}";

    // "1 error", "0 errors", "3 warnings" — no parenthetical "(s)".
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}
