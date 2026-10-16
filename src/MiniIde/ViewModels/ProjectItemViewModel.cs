using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniIde.Models;
using MiniIde.Services;

namespace MiniIde.ViewModels;

/// <summary>What a project card asks of its owner. The card owns its own selection state; everything that
/// touches tabs or processes belongs to the window's view model, which is the only thing that can see them.</summary>
public interface IProjectHost
{
    void StartRun(ProjectItemViewModel project, RunSelection selection);
    void ShowTab(TabViewModelBase tab);
    void FindProblems(ProjectItemViewModel project);
    void ManageNuGet(ProjectItemViewModel project);
    void SetStarred(ProjectItemViewModel project, bool starred);
}

/// <summary>One project card in the left panel: identity, a Run button that always launches the default
/// selection, a dropdown of every other runnable selection (one click = one run), and a button per live run.
///
/// <para>Cards are long-lived: a reload of the project list updates the card for the same path in place
/// (<see cref="Update"/>) rather than replacing it, so the run buttons don't blink out because a project
/// file was saved.</para>
///
/// <para>No sticky "last used" selection: the plain Run button always runs the default configuration, and the
/// dropdown's rows run their own selection instantly. The default row carries a "(default)" suffix so users
/// can tell which one the plain button would pick.</para></summary>
public partial class ProjectItemViewModel : ViewModelBase
{
    private const string DefaultConfiguration = "Debug";

    /// <summary>Label for the profile slot that means "pass no <c>-lp</c> at all", i.e. let <c>dotnet run</c>
    /// pick its own default. Used in menu labels alongside the configuration name.</summary>
    private const string NoProfileLabel = "(dotnet default)";

    private readonly IProjectHost _host;
    private IReadOnlyList<string> _solutionConfigurations = Array.Empty<string>();

    public ProjectItemViewModel(ProjectEntry entry, IReadOnlyList<string> configurations, IProjectHost host)
    {
        _host = host;
        _entry = entry;
        LiveRuns.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowsRunRow));
        Update(entry, configurations);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Path), nameof(Name), nameof(IconGlyph), nameof(IconColor), nameof(IsRunnable), nameof(ShowsRunRow), nameof(RunTip))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private ProjectEntry _entry;

    public string Path => Entry.Path;
    public string Name => Entry.Name;
    public string IconGlyph => Entry.IconGlyph;
    public IBrush IconColor => Entry.IconColor;

    /// <summary>Whether the card offers Run at all. A library has nothing to run, so it shows no Run button
    /// (rather than a disabled one) and sorts to the bottom of the list.</summary>
    public bool IsRunnable => Entry.IsRunnable;

    /// <summary>Flat list of every runnable selection — the cartesian product of configurations and launch
    /// profiles — in dropdown order. The default selection carries a "(default)" suffix. Rebuilt on reload;
    /// never mutated otherwise, so the list is stable while the flyout is open.</summary>
    public ObservableCollection<RunChoice> RunOptions { get; } = new();

    /// <summary>The run tabs whose process is still going, in start order. One button each, beside Run. A run
    /// whose tab was closed stays here — closing a tab never stops its process — so this is how it is reached
    /// again.</summary>
    public ObservableCollection<OutputTabViewModel> LiveRuns { get; } = new();

    /// <summary>Whether the card shows its Run row. A runnable project always does; any other only while it
    /// still has live runs (it was reclassified mid-run), so those runs stay reachable.</summary>
    public bool ShowsRunRow => IsRunnable || LiveRuns.Count > 0;

    private bool CanRun => Entry.IsRunnable && _solutionConfigurations.Count > 0;

    /// <summary>What the plain Run button will do, or why it can't — a disabled button with no reason on it is
    /// a dead control.</summary>
    public string RunTip =>
        !Entry.IsRunnable ? $"{Name} isn't a runnable project"
        : _solutionConfigurations.Count == 0 ? "The solution declares no build configurations"
        : $"{(Entry.Kind == ProjectKind.Tst ? "Test" : "Run")} {Name} ({Describe(DefaultSelection)})";

    /// <summary>The selection the plain Run button runs: Debug (or the first configuration the solution
    /// declares, if Debug isn't there) with no launch profile.</summary>
    private RunSelection DefaultSelection => new(PickDefaultConfiguration(), null);

    /// <summary>How a selection reads in a tab header, a run button and a status message. One helper so they
    /// can't drift.</summary>
    public static string Describe(RunSelection selection) =>
        selection.LaunchProfile is { } profile ? $"{selection.Configuration}, {profile}" : selection.Configuration;

    /// <summary>Nothing here is gated on a run already being live: repeat presses launch concurrent instances,
    /// each in its own tab.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run() => _host.StartRun(this, DefaultSelection);

    [RelayCommand]
    private void ShowRun(OutputTabViewModel? tab)
    {
        if (tab is not null) _host.ShowTab(tab);
    }

    [RelayCommand]
    private void FindProblems() => _host.FindProblems(this);

    [RelayCommand]
    private void ManageNuGet() => _host.ManageNuGet(this);

    private static readonly IBrush StarredBrush = new SolidColorBrush(Color.Parse("#E8C547"));
    private static readonly IBrush UnstarredBrush = new SolidColorBrush(Color.Parse("#6A6A72"));

    /// <summary>Whether the user starred this project, pinning it to the top of the list. Set by the owner, which
    /// also persists it; the card only asks for the change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarGlyph), nameof(StarColor), nameof(StarTip))]
    private bool _isStarred;

    public string StarGlyph => IsStarred ? ActionIcon.Star : ActionIcon.StarOutline;
    public IBrush StarColor => IsStarred ? StarredBrush : UnstarredBrush;
    public string StarTip => IsStarred ? "Unstar (stop pinning to the top)" : "Star (pin to the top)";

    [RelayCommand]
    private void ToggleStar() => _host.SetStarred(this, !IsStarred);

    /// <summary>Points the card at a freshly read <paramref name="entry"/> (same path, possibly a different kind)
    /// and the solution's current configurations, and rebuilds the dropdown rows.</summary>
    public void Update(ProjectEntry entry, IReadOnlyList<string> configurations)
    {
        Entry = entry;
        _solutionConfigurations = configurations;
        RunCommand.NotifyCanExecuteChanged();

        var profiles = entry.Kind == ProjectKind.Tst
            ? Array.Empty<string>()
            : LaunchProfileReader.Read(entry.Path);

        RunOptions.Clear();
        if (configurations.Count == 0) { OnPropertyChanged(nameof(RunTip)); return; }

        // Profile slots: no-profile is always present (as the only slot for test projects and projects without
        // launchSettings), followed by every profile the project declares. "No profile" first so each config
        // leads with its plainest form.
        var profileSlots = new List<(string? Value, string? LabelPart)> { (null, profiles.Count > 0 ? NoProfileLabel : null) };
        foreach (var profile in profiles) profileSlots.Add((profile, profile));

        var defaultSelection = DefaultSelection;
        foreach (var configuration in configurations)
        {
            foreach (var slot in profileSlots)
            {
                var selection = new RunSelection(configuration, slot.Value);
                var label = slot.LabelPart is null ? configuration : $"{configuration} - {slot.LabelPart}";
                if (selection == defaultSelection) label += "   (default)";
                RunOptions.Add(new RunChoice(label, () => _host.StartRun(this, selection)));
            }
        }
        OnPropertyChanged(nameof(RunTip));
    }

    /// <summary>The configuration the plain Run button picks: <c>Debug</c> if the solution declares it,
    /// otherwise whatever the solution does declare, otherwise the literal <c>Debug</c> (so the fallback is
    /// still a name <c>dotnet run</c> accepts rather than empty).</summary>
    private string PickDefaultConfiguration()
    {
        foreach (var c in _solutionConfigurations)
            if (string.Equals(c, DefaultConfiguration, StringComparison.OrdinalIgnoreCase)) return c;
        return _solutionConfigurations.Count > 0 ? _solutionConfigurations[0] : DefaultConfiguration;
    }
}
