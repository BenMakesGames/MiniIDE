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

/// <summary>One project card in the left panel: identity, its own Run selection (runnable projects only), and a
/// button per live run.
///
/// <para>Cards are long-lived: a reload of the project list updates the card for the same path in place
/// (<see cref="Update"/>) rather than replacing it, so neither the user's configuration pick nor the run
/// buttons blink out because a project file was saved.</para></summary>
public partial class ProjectItemViewModel : ViewModelBase
{
    private const string DefaultConfiguration = "Debug";

    /// <summary>Label for the profile row that means "pass no <c>-lp</c> at all", which is where every session
    /// starts and the only way back once a profile has been picked.</summary>
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

    /// <summary>The flyout's two groups. Rebuilt only when their inputs change (a reload); picking a row mutates
    /// the rows' <see cref="RunChoice.IsSelected"/> instead, so the collections stay stable while the flyout is
    /// open.</summary>
    public ObservableCollection<RunChoice> Configurations { get; } = new();
    public ObservableCollection<RunChoice> LaunchProfiles { get; } = new();

    /// <summary>The run tabs whose process is still going, in start order. One button each, beside Run. A run
    /// whose tab was closed stays here — closing a tab never stops its process — so this is how it is reached
    /// again.</summary>
    public ObservableCollection<OutputTabViewModel> LiveRuns { get; } = new();

    /// <summary>Whether the card shows its Run row. A runnable project always does; any other only while it
    /// still has live runs (it was reclassified mid-run), so those runs stay reachable.</summary>
    public bool ShowsRunRow => IsRunnable || LiveRuns.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTip))]
    private string _selectedConfiguration = DefaultConfiguration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTip))]
    private string? _selectedLaunchProfile;

    /// <summary>Whether the flyout has a profile group to show. False for a project with no launch settings and
    /// for every test project, since <c>dotnet test</c> takes no profile.</summary>
    [ObservableProperty] private bool _hasLaunchProfiles;

    private bool CanRun => Entry.IsRunnable && _solutionConfigurations.Count > 0;

    /// <summary>What Run will do, or why it can't — a disabled button with no reason on it is a dead control.</summary>
    public string RunTip =>
        !Entry.IsRunnable ? $"{Name} isn't a runnable project"
        : _solutionConfigurations.Count == 0 ? "The solution declares no build configurations"
        : $"{(Entry.Kind == ProjectKind.Tst ? "Test" : "Run")} {Name} ({Describe(CurrentSelection)})";

    /// <summary>A test project runs under <c>dotnet test</c>, which has no <c>--launch-profile</c> at all.
    /// Dropping the profile here keeps that authoritative rather than trusting the flyout to have offered none.</summary>
    private RunSelection CurrentSelection =>
        new(SelectedConfiguration, Entry.Kind == ProjectKind.Tst ? null : SelectedLaunchProfile);

    /// <summary>How a selection reads in a tab header, a run button and a status message. One helper so they
    /// can't drift.</summary>
    public static string Describe(RunSelection selection) =>
        selection.LaunchProfile is { } profile ? $"{selection.Configuration}, {profile}" : selection.Configuration;

    /// <summary>Nothing here is gated on a run already being live: repeat presses launch concurrent instances,
    /// each in its own tab.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private void Run() => _host.StartRun(this, CurrentSelection);

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

    [RelayCommand]
    private void SelectConfiguration(string? configuration)
    {
        if (configuration is not null) SelectedConfiguration = configuration;
    }

    [RelayCommand]
    private void SelectLaunchProfile(string? profile) => SelectedLaunchProfile = profile;

    partial void OnSelectedConfigurationChanged(string value) => SyncSelection();
    partial void OnSelectedLaunchProfileChanged(string? value) => SyncSelection();

    /// <summary>Points the card at a freshly read <paramref name="entry"/> (same path, possibly a different kind)
    /// and the solution's current configurations, carrying the user's selections over only where they are still
    /// valid.</summary>
    public void Update(ProjectEntry entry, IReadOnlyList<string> configurations)
    {
        Entry = entry;
        _solutionConfigurations = configurations;
        RunCommand.NotifyCanExecuteChanged();

        // Re-resolved by name with teeth: `dotnet run -c <gone>` doesn't fail, it quietly builds a DEBUG-less
        // binary into a bin folder of that name. So a configuration that disappeared falls back to Debug.
        SelectedConfiguration = PickConfiguration(SelectedConfiguration);

        var profiles = entry.Kind == ProjectKind.Tst
            ? Array.Empty<string>()
            : LaunchProfileReader.Read(entry.Path);
        // Naming a profile the project doesn't have is a hard `dotnet run` error, so a vanished one is dropped.
        if (SelectedLaunchProfile is { } selected && !profiles.Contains(selected, StringComparer.Ordinal))
            SelectedLaunchProfile = null;

        var configurationGroup = $"{entry.Path}|configuration";
        Configurations.Clear();
        foreach (var configuration in configurations)
            Configurations.Add(new RunChoice(configuration, configuration, configurationGroup, SelectConfigurationCommand,
                string.Equals(configuration, SelectedConfiguration, StringComparison.OrdinalIgnoreCase)));

        var profileGroup = $"{entry.Path}|profile";
        LaunchProfiles.Clear();
        if (profiles.Count > 0)
        {
            LaunchProfiles.Add(new RunChoice(NoProfileLabel, null, profileGroup, SelectLaunchProfileCommand, SelectedLaunchProfile is null));
            foreach (var profile in profiles)
                LaunchProfiles.Add(new RunChoice(profile, profile, profileGroup, SelectLaunchProfileCommand,
                    string.Equals(profile, SelectedLaunchProfile, StringComparison.Ordinal)));
        }
        HasLaunchProfiles = LaunchProfiles.Count > 0;
        OnPropertyChanged(nameof(RunTip));
    }

    /// <summary>Moves the radio marks to the current selections without touching either collection — see
    /// <see cref="RunChoice.IsSelected"/> for why the rows are updated rather than rebuilt.</summary>
    private void SyncSelection()
    {
        foreach (var choice in Configurations)
            choice.IsSelected = string.Equals(choice.Value, SelectedConfiguration, StringComparison.OrdinalIgnoreCase);
        foreach (var choice in LaunchProfiles)
            choice.IsSelected = string.Equals(choice.Value, SelectedLaunchProfile, StringComparison.Ordinal);
    }

    /// <summary>The configuration to select given <paramref name="preferred"/>: itself when the solution still
    /// declares it, otherwise <c>Debug</c>, otherwise whatever the solution does declare. Never returns a name
    /// the solution doesn't have — that is the whole point.</summary>
    private string PickConfiguration(string preferred)
    {
        foreach (var c in _solutionConfigurations)
            if (string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var c in _solutionConfigurations)
            if (string.Equals(c, DefaultConfiguration, StringComparison.OrdinalIgnoreCase)) return c;
        return _solutionConfigurations.Count > 0 ? _solutionConfigurations[0] : DefaultConfiguration;
    }
}
