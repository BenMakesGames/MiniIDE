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
    private readonly Func<CancellationToken, Task> _prepareWorkspace;
    private readonly Func<SourceLocation, Task> _openHit;
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

    /// <param name="prepareWorkspace">Loads the Roslyn workspace and overlays unsaved editor buffers onto it.
    /// Injected rather than called directly so this panel analyzes exactly what the user sees, without needing
    /// to know that open tabs exist.</param>
    public ProblemsViewModel(
        WorkspaceService workspace,
        SolutionService sln,
        Func<CancellationToken, Task> prepareWorkspace,
        Func<SourceLocation, Task> openHit)
    {
        _workspace = workspace;
        _sln = sln;
        _prepareWorkspace = prepareWorkspace;
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
        //
        // `settled` closes the race that leaves the panel looking hung: progress is *posted* to the UI thread,
        // so a message raised moments before the analysis finished can still be sitting in the dispatcher queue
        // when we write the final count — and would then overwrite it with "Analyzing SomeProject", leaving no
        // way to tell a finished run from a stuck one. Written and read only on the UI thread.
        var settled = false;
        void OnProgress(string m) => Dispatcher.UIThread.Post(() =>
        {
            if (!settled && ReferenceEquals(_cts, cts)) Status = m;
        });
        _workspace.Progress += OnProgress;
        try
        {
            await _prepareWorkspace(ct);
            var diagnostics = await _workspace.GetDiagnosticsAsync(ct);
            ct.ThrowIfCancellationRequested();
            _results = diagnostics;
            ErrorCount = diagnostics.Count(d => d.Severity == ProblemSeverity.Error);
            WarningCount = diagnostics.Count(d => d.Severity == ProblemSeverity.Warning);
            RebuildTree();
            Placeholder = "No problems found.";
            settled = true;
            Status = $"{Plural.Of(ErrorCount, "error")}, {Plural.Of(WarningCount, "warning")}";
        }
        // Only the current run may touch shared state — a superseded run (its _cts already replaced by a newer
        // refresh) must stay silent so it can't reset IsBusy or clobber the newer run's status.
        catch (OperationCanceledException) { settled = true; if (ReferenceEquals(_cts, cts)) Status = "Canceled"; }
        catch (Exception ex) { settled = true; if (ReferenceEquals(_cts, cts)) Status = ex.Message; }
        finally { _workspace.Progress -= OnProgress; if (ReferenceEquals(_cts, cts)) IsBusy = false; }
    }

    /// <summary>Activates a leaf via the injected open-callback. No-op for locationless diagnostics.</summary>
    public void Activate(ProblemLeaf leaf)
    {
        if (leaf.Item.Location is null) return;
        _ = _openHit(leaf.Item.Location);
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
        foreach (var g in BuildGroups()) Groups.Add(g);
        ShowPlaceholder = Groups.Count == 0;
    }

    // Group ordering is a property of the panel, not of the grouping mode: errors first, then by the mode's
    // own sort key. Stated once here so the two modes cannot drift apart on it.
    private IEnumerable<ProblemGroup> BuildGroups() =>
        (Grouping == ProblemGrouping.ByFile ? BuildByFile() : BuildByCode())
            .OrderBy(g => g.HasError ? 0 : 1)
            .ThenBy(g => g.SortKey, StringComparer.OrdinalIgnoreCase);

    // File → issues in that file (locationless ones collapse into a single "(No file)" group). Children by line.
    private IEnumerable<ProblemGroup> BuildByFile() =>
        _results.GroupBy(p => p.Location?.File).Select(g =>
        {
            var name = g.Key is null ? "(No file)" : _sln.ToRelativePath(g.Key);
            return Group(
                g.OrderBy(p => p.Location?.Line).ThenBy(p => p.Location?.Column),
                sortKey: name,
                header: items => $"{name} ({items.Count})",
                // Parent shows the file, so lead with the location + code.
                leaf: p => p.Location is { } at ? $"{p.Id}  Ln {at.Line}: {p.Message}" : $"{p.Id}: {p.Message}");
        });

    // Diagnostic code → occurrences across files. Header carries a representative message + count.
    private IEnumerable<ProblemGroup> BuildByCode() =>
        _results.GroupBy(p => p.Id).Select(g => Group(
            g.OrderBy(p => p.Location?.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Location?.Line).ThenBy(p => p.Location?.Column),
            sortKey: g.Key,
            header: items => $"{g.Key} — {items[0].Message} ({items.Count})",
            // Parent shows the code, so lead with the file:line where it occurred.
            leaf: p => p.Location is { } at
                ? $"{_sln.ToRelativePath(at.File)}({at.Line},{at.Column}): {p.Message}"
                : $"(no file): {p.Message}"));

    // The shape both modes share: materialize the sorted items, wrap each as a leaf, flag the group as an
    // error group if any child is one.
    private static ProblemGroup Group(
        IEnumerable<ProblemItem> sorted,
        string sortKey,
        Func<IReadOnlyList<ProblemItem>, string> header,
        Func<ProblemItem, string> leaf)
    {
        var items = sorted.ToList();
        return new ProblemGroup(
            header(items),
            items.Select(p => new ProblemLeaf(p, leaf(p))).ToList(),
            items.Any(p => p.Severity == ProblemSeverity.Error),
            sortKey);
    }
}
