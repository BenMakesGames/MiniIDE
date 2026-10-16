using MiniIde.Models;
using MiniIde.ViewModels;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The project card's own state: which configuration Run uses, how a reload treats the user's pick,
/// and when Run is offered at all.</summary>
public sealed class ProjectItemViewModelTests
{
    private sealed class RecordingHost : IProjectHost
    {
        public List<RunSelection> Runs { get; } = new();
        public void StartRun(ProjectItemViewModel project, RunSelection selection) => Runs.Add(selection);
        public void ShowTab(TabViewModelBase tab) { }
        public void FindProblems(ProjectItemViewModel project) { }
        public void ManageNuGet(ProjectItemViewModel project) { }
        public void SetStarred(ProjectItemViewModel project, bool starred) { }
    }

    // No launchSettings.json exists beside a path in a fresh temp folder, so no profiles are offered.
    private static ProjectEntry Entry(ProjectKind kind = ProjectKind.Exe) =>
        new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "App.csproj"), "App", kind);

    private static readonly string[] DebugRelease = ["Debug", "Release"];

    [Fact]
    public void DefaultsToDebug_WhateverOrderTheSolutionDeclares()
    {
        var card = new ProjectItemViewModel(Entry(), ["Release", "Debug"], new RecordingHost());

        card.SelectedConfiguration.ShouldBe("Debug");
        card.Configurations.Single(c => c.IsSelected).Value.ShouldBe("Debug");
    }

    [Fact]
    public void Run_UsesThePickedConfiguration()
    {
        var host = new RecordingHost();
        var card = new ProjectItemViewModel(Entry(), DebugRelease, host);

        card.SelectConfigurationCommand.Execute("Release");
        card.RunCommand.Execute(null);

        host.Runs.ShouldHaveSingleItem().Configuration.ShouldBe("Release");
        card.Configurations.Single(c => c.IsSelected).Value.ShouldBe("Release");
    }

    [Fact]
    public void Update_KeepsAPickTheSolutionStillDeclares()
    {
        var entry = Entry();
        var card = new ProjectItemViewModel(entry, DebugRelease, new RecordingHost());
        card.SelectConfigurationCommand.Execute("Release");

        card.Update(entry, DebugRelease);

        card.SelectedConfiguration.ShouldBe("Release");
    }

    [Fact]
    public void Update_FallsBackToDebug_WhenThePickedConfigurationDisappears()
    {
        // `dotnet run -c <gone>` doesn't fail — it quietly builds a DEBUG-less binary — so a vanished pick must
        // not be carried.
        var entry = Entry();
        var card = new ProjectItemViewModel(entry, ["Debug", "Release", "Staging"], new RecordingHost());
        card.SelectConfigurationCommand.Execute("Staging");

        card.Update(entry, DebugRelease);

        card.SelectedConfiguration.ShouldBe("Debug");
    }

    [Fact]
    public void Run_IsNotOffered_ForALibrary()
    {
        var card = new ProjectItemViewModel(Entry(ProjectKind.Lib), DebugRelease, new RecordingHost());

        card.RunCommand.CanExecute(null).ShouldBeFalse();
        card.IsRunnable.ShouldBeFalse();
        card.ShowsRunRow.ShouldBeFalse();
    }

    [Fact]
    public void RunRow_StaysVisible_WhileAReclassifiedProjectStillHasLiveRuns()
    {
        var exe = Entry();
        var card = new ProjectItemViewModel(exe, DebugRelease, new RecordingHost());
        card.LiveRuns.Add(new OutputTabViewModel("run:x", "App - Output", _ => { }));

        card.Update(exe with { Kind = ProjectKind.Lib }, DebugRelease);

        card.ShowsRunRow.ShouldBeTrue(); // the live run's button must stay reachable
        card.LiveRuns.Clear();
        card.ShowsRunRow.ShouldBeFalse();
    }

    [Fact]
    public void Run_BecomesAvailable_WhenAReloadReclassifiesTheProject()
    {
        var lib = Entry(ProjectKind.Lib);
        var card = new ProjectItemViewModel(lib, DebugRelease, new RecordingHost());

        card.Update(lib with { Kind = ProjectKind.Exe }, DebugRelease);

        card.RunCommand.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void RadioGroups_AreUniquePerProject()
    {
        // Every card's flyout lives in one window; a shared group name would let one card's pick uncheck
        // another's.
        var a = new ProjectItemViewModel(Entry(), DebugRelease, new RecordingHost());
        var b = new ProjectItemViewModel(Entry(), DebugRelease, new RecordingHost());

        a.Configurations[0].Group.ShouldNotBe(b.Configurations[0].Group);
    }
}
