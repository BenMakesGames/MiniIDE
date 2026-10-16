using MiniIde.Models;
using MiniIde.ViewModels;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The project card's own state: which selection the plain Run button uses, what the dropdown lists,
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
    public void PlainRun_UsesDebug_WhateverOrderTheSolutionDeclares()
    {
        var host = new RecordingHost();
        var card = new ProjectItemViewModel(Entry(), ["Release", "Debug"], host);

        card.RunCommand.Execute(null);

        var selection = host.Runs.ShouldHaveSingleItem();
        selection.Configuration.ShouldBe("Debug");
        selection.LaunchProfile.ShouldBeNull();
    }

    [Fact]
    public void PlainRun_FallsBackToFirstConfiguration_WhenNoDebug()
    {
        var host = new RecordingHost();
        var card = new ProjectItemViewModel(Entry(), ["Staging", "Release"], host);

        card.RunCommand.Execute(null);

        host.Runs.ShouldHaveSingleItem().Configuration.ShouldBe("Staging");
    }

    [Fact]
    public void RunOptions_ListEveryConfiguration_WithDefaultMarked()
    {
        var card = new ProjectItemViewModel(Entry(), DebugRelease, new RecordingHost());

        // No profiles on disk, so one row per configuration.
        card.RunOptions.Select(o => o.Label).ShouldBe(["Debug   (default)", "Release"]);
    }

    [Fact]
    public void RunOptions_InvokingARow_StartsThatRun()
    {
        var host = new RecordingHost();
        var card = new ProjectItemViewModel(Entry(), DebugRelease, host);

        // Release row — the non-default one in a two-config, no-profile project.
        card.RunOptions.Single(o => o.Label == "Release").Run.Execute(null);

        var selection = host.Runs.ShouldHaveSingleItem();
        selection.Configuration.ShouldBe("Release");
        selection.LaunchProfile.ShouldBeNull();
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
}
