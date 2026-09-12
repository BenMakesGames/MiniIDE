using MiniIde.ViewModels;
using Shouldly;
using Xunit;

namespace MiniIde.Tests;

/// <summary>The run strip's synchronous state machine — which of Stop/Run a run tab is offering, and whether
/// it is enabled. No process is started here: the half that needs a live <c>dotnet</c> (a run going live, then
/// exiting) is owned by <see cref="MiniIde.Services.RunSession"/>, so what is worth pinning down in isolation
/// is the guard side — a tab that isn't a run tab offers neither button, and a tab with nothing running
/// refuses Stop rather than throwing at it.</summary>
public sealed class OutputTabViewModelTests
{
    private static OutputTabViewModel NuGetTab() => new("nuget:", "NuGet - Output");

    private static OutputTabViewModel RunTab(Action<OutputTabViewModel> start) =>
        new("run:x#1", "X - Output", start);

    [Fact]
    public void TabWithNoStarter_IsNotARunTab_AndOffersNeitherButton()
    {
        var tab = NuGetTab();

        tab.HasRun.ShouldBeFalse();
        tab.RunAgainCommand.CanExecute(null).ShouldBeFalse();
        tab.StopRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void RunTab_WithNothingLive_OffersRunAndNotStop()
    {
        var tab = RunTab(_ => { });

        tab.HasRun.ShouldBeTrue();
        tab.IsRunLive.ShouldBeFalse();
        tab.RunState.ShouldBe("Exited");
        tab.RunAgainCommand.CanExecute(null).ShouldBeTrue();
        tab.StopRunCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void RunAgain_InvokesTheStarter_WithTheTabItself()
    {
        OutputTabViewModel? started = null;
        var calls = 0;
        var tab = RunTab(t => { started = t; calls++; });

        tab.RunAgainCommand.Execute(null);

        calls.ShouldBe(1);
        started.ShouldBeSameAs(tab);
    }

    // The close and shutdown paths call StopRun() on every output tab unconditionally, so "nothing running"
    // has to be a no-op rather than a null dereference — CanExecute only ever gated the button.
    [Fact]
    public void StopRun_WithNothingRunning_IsANoOp()
    {
        var tab = RunTab(_ => { });

        Should.NotThrow(() => tab.StopRun());

        tab.IsStopping.ShouldBeFalse();
        tab.RunState.ShouldBe("Exited");
    }

    [Fact]
    public void StoppingState_DisablesStop_AndStillDoesNotOfferRun()
    {
        var tab = RunTab(_ => { });
        // Stand in for the window between "the kill was asked for" and "the process actually exited": the run
        // is still live, so Run must stay hidden, and Stop must be greyed rather than clickable a second time.
        tab.IsRunLive = true;
        tab.IsStopping = true;

        tab.RunState.ShouldBe("Stopping");
        tab.StopRunCommand.CanExecute(null).ShouldBeFalse();
        tab.RunAgainCommand.CanExecute(null).ShouldBeFalse();
        tab.StopTip.ShouldBe("Already stopping — waiting for the process to exit");
    }
}
